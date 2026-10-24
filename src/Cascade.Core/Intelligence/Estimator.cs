using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Analysis;
using Cascade.Core.Llm;
using Cascade.Core.Model;
using Cascade.Core.Schema;

namespace Cascade.Core.Intelligence
{
    public sealed class EstimateOptions
    {
        public ProposalKind Kind { get; set; } = ProposalKind.StoryPoints;
        /// <summary>Only items whose current value is missing or TBD. Default true.</summary>
        public bool OnlyUnsized { get; set; } = true;
        public int MaxItems { get; set; } = 40;
        public int BatchSize { get; set; } = 15;
        /// <summary>Skip Stories without acceptance criteria instead of inventing a size.</summary>
        public bool RequireDefinition { get; set; } = true;
    }

    /// <summary>
    /// Produces story-point and effort proposals. The definability gate runs first; then the model is asked
    /// for a strict JSON array; the reply is parsed tolerantly and clamped to policy. Output is a batch of
    /// proposals for human review. No provider writes happen here.
    /// </summary>
    public static class Estimator
    {
        public static bool IsDefinable(WorkItem item)
        {
            if (item.Type == WorkItemType.Story) return BodyChecks.HasAcceptanceCriteria(item.Body) || BodyChecks.HasUserNeed(item.Body);
            if (item.Type == WorkItemType.Feature) return BodyChecks.HasDoneWhen(item.Body) || BodyChecks.HasSubstantiveText(item.Body, 80);
            return false;
        }

        public static string SystemPrompt(ProposalKind kind)
        {
            if (kind == ProposalKind.FeatureEffort)
            {
                return "You are Cascade, an Agile sizing assistant. For each FEATURE below, propose an effort band using exactly one of: Small, Medium, Large. " +
                       "Small is about one sprint of focused work; Medium two to three sprints across several Stories; Large is multi-sprint with split pressure. " +
                       "Return ONLY a JSON array, one object per feature, with keys: id, effort, confidence (low|medium|high), reason (one sentence). " +
                       "Use the id exactly as given. If a feature is too vaguely defined to size, set effort to \"TBD\" and explain why.";
            }
            return "You are Cascade, an Agile sizing assistant. For each STORY below, propose story points using exactly one of: 1, 2, 3, 5, 8. " +
                   "1-2 is small and clear, 3-5 is a typical sprint Story, 8 is large for one 14-day sprint. Never propose 13 or more; recommend a split instead. " +
                   "Return ONLY a JSON array, one object per story, with keys: id, points, confidence (low|medium|high), reason (one sentence). " +
                   "Use the id exactly as given. If a story has no testable acceptance criteria, set points to \"TBD\" and say what is missing.";
        }

        public static string UserPrompt(IReadOnlyList<WorkItem> items)
        {
            var sb = new StringBuilder();
            sb.Append("Items (").Append(items.Count).Append("):\n\n");
            foreach (var item in items)
            {
                sb.Append("### id: ").Append(item.CanonicalId).Append('\n');
                sb.Append("Title: ").Append(Descriptions.ShortName(item.Title)).Append('\n');
                var narrative = Descriptions.Narrative(item.Body);
                if (narrative.Length > 1800) narrative = narrative.Substring(0, 1800) + "\n…(truncated)";
                sb.Append(narrative).Append("\n\n");
            }
            sb.Append("Respond with the JSON array only.");
            return sb.ToString();
        }

        public static async Task<ProposalBatch> RunAsync(IReadOnlyList<WorkItem> items, ILlmClient llm, EstimateOptions options, RepositoryRef target, IProgress<string>? progress, CancellationToken cancellation)
        {
            options = options ?? new EstimateOptions();
            var kind = options.Kind;
            var wantType = kind == ProposalKind.FeatureEffort ? WorkItemType.Feature : WorkItemType.Story;
            var batch = new ProposalBatch { Kind = kind, Target = target.Key, Source = "llm:" + llm.Profile.ToString().ToLowerInvariant() + "/" + llm.Model };
            var candidates = new List<WorkItem>();
            foreach (var item in items.Where(i => i.Type == wantType))
            {
                batch.Considered++;
                var current = item.Meta == null ? null : (kind == ProposalKind.FeatureEffort ? item.Meta.Effort : item.Meta.Points);
                var sized = !string.IsNullOrWhiteSpace(current) && !string.Equals(current, Sizing.Tbd, StringComparison.OrdinalIgnoreCase);
                if (options.OnlyUnsized && sized) { batch.Skipped++; continue; }
                if (options.RequireDefinition && !IsDefinable(item))
                {
                    batch.Skipped++;
                    batch.SkipReasons.Add(item.CanonicalId + ": not definable (no acceptance criteria / done-when); left TBD");
                    continue;
                }
                candidates.Add(item);
                if (candidates.Count >= options.MaxItems) break;
            }
            if (candidates.Count == 0)
            {
                batch.Warnings.Add("No eligible items: everything is already sized or lacks a definition.");
                return batch;
            }

            var byId = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                byId[c.CanonicalId] = c;
                byId[c.Number.ToString()] = c;
                byId["#" + c.Number] = c;
            }
            var raw = new StringBuilder();
            for (var offset = 0; offset < candidates.Count; offset += options.BatchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                var slice = candidates.Skip(offset).Take(options.BatchSize).ToList();
                if (progress != null) progress.Report("Asking " + llm.Model + " about " + slice.Count + " items (" + (offset + slice.Count) + "/" + candidates.Count + ")");
                LlmResponse response;
                try
                {
                    response = await llm.ChatAsync(new[] { LlmMessage.System(SystemPrompt(kind)), LlmMessage.User(UserPrompt(slice)) }, cancellation).ConfigureAwait(false);
                }
                catch (Providers.ProviderHttpException ex)
                {
                    batch.Warnings.Add("Model call failed: " + ex.Message);
                    break;
                }
                raw.Append(response.Text).Append("\n\n");
                var parsed = EstimateParser.Parse(response.Text);
                if (parsed.Count == 0) batch.Warnings.Add("Model reply for a batch of " + slice.Count + " could not be parsed; those items stay TBD.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var est in parsed)
                {
                    WorkItem? item;
                    if (!byId.TryGetValue(est.Ref, out item) || item == null || !seen.Add(item.Key)) continue;
                    var current = item.Meta == null ? null : (kind == ProposalKind.FeatureEffort ? item.Meta.Effort : item.Meta.Points);
                    var proposed = kind == ProposalKind.FeatureEffort ? Sizing.ClampEffort(est.Effort ?? est.Points) : Sizing.ClampPoints(est.Points ?? est.Effort);
                    var reason = est.Reason;
                    var original = kind == ProposalKind.FeatureEffort ? est.Effort : est.Points;
                    if (!string.IsNullOrEmpty(original) && !string.Equals(original!.Trim(), proposed, StringComparison.OrdinalIgnoreCase))
                        reason = (reason.Length > 0 ? reason + " " : string.Empty) + "[model said " + original + "; clamped to policy value " + proposed + "]";
                    batch.Proposals.Add(new Proposal
                    {
                        BatchId = batch.Id,
                        Kind = kind,
                        ItemKey = item.Key,
                        ItemNumber = item.Number,
                        ItemCanonicalId = item.CanonicalId,
                        ItemTitle = item.Title,
                        CurrentValue = current,
                        ProposedValue = proposed,
                        Confidence = proposed == Sizing.Tbd ? "low" : est.Confidence,
                        Reason = reason,
                        Source = batch.Source + " " + est.Source,
                    });
                }
            }
            batch.RawModelOutput = LlmClient.Redact(raw.ToString());
            return batch;
        }

        /// <summary>Parent-link proposals from deterministic evidence; the top candidate becomes the proposal when it clearly leads.</summary>
        public static ProposalBatch ParentProposals(ReviewReport report, RepositoryRef target, double minimumScore = 2.0)
        {
            var batch = new ProposalBatch { Kind = ProposalKind.ParentLink, Target = target.Key, Source = "deterministic" };
            foreach (var review in report.ParentReviews)
            {
                batch.Considered++;
                if (review.Candidates.Count == 0) { batch.Skipped++; batch.SkipReasons.Add(review.CanonicalId + ": no candidate parent with evidence"); continue; }
                var top = review.Candidates[0];
                if (top.Score < minimumScore) { batch.Skipped++; batch.SkipReasons.Add(review.CanonicalId + ": best candidate score " + top.Score + " below " + minimumScore); continue; }
                var second = review.Candidates.Count > 1 ? review.Candidates[1].Score : 0;
                var confidence = top.Score >= second * 2 && top.Score >= 4 ? "high" : top.Score > second ? "medium" : "low";
                var itemReview = report.Items.FirstOrDefault(i => i.Key == review.Key);
                batch.Proposals.Add(new Proposal
                {
                    BatchId = batch.Id,
                    Kind = ProposalKind.ParentLink,
                    ItemKey = review.Key,
                    ItemNumber = itemReview == null ? 0 : itemReview.Number,
                    ItemCanonicalId = review.CanonicalId,
                    ItemTitle = review.Title,
                    CurrentValue = review.DeclaredParent,
                    ProposedValue = top.CanonicalId,
                    Confidence = confidence,
                    Reason = string.Join("; ", top.Evidence) + (review.Candidates.Count > 1 ? " (runner-up: " + review.Candidates[1].CanonicalId + " " + review.Candidates[1].Score + ")" : string.Empty),
                    Source = "deterministic",
                });
            }
            return batch;
        }
    }
}
