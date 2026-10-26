using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Schema;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    public sealed class RefineOptions
    {
        public int MaxItems { get; set; }
        /// <summary>Restrict to these item numbers (bulk action from the Review page). Null = every eligible item.</summary>
        public HashSet<long> OnlyNumbers { get; set; }
        /// <summary>Restrict to these item keys (the sandbox items of a multi-repository snapshot). Null = no restriction.</summary>
        public HashSet<string> OnlyKeys { get; set; }

        public RefineOptions()
        {
            MaxItems = 10;
        }
    }

    /// <summary>One parsed model reply for a refinement.</summary>
    public sealed class RefinementDraft
    {
        public string Narrative { get; set; }
        public Dictionary<string, string> Meta { get; set; }
        public string Note { get; set; }
        public string Source { get; set; }

        public RefinementDraft()
        {
            Narrative = string.Empty;
            Meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Note = string.Empty;
            Source = "json";
        }
    }

    /// <summary>
    /// The refinement half of the vision: for items whose definition is incomplete, ask the model to draft
    /// the missing playbook sections while keeping every existing sentence, then check the result against
    /// the same Definition-of-Ready rules the audit uses. Output is a batch of proposals; nothing is written.
    /// Metadata gaps are repaired deterministically (no model) in <see cref="MetadataProposals"/>.
    /// </summary>
    public static class Refiner
    {
        /// <summary>Deficiency codes a drafted narrative can resolve.</summary>
        public static readonly string[] ContentCodes =
        {
            Deficiencies.StoryMissingAcceptanceCriteria, Deficiencies.StoryMissingUserNeed, Deficiencies.StoryMissingTasks,
            Deficiencies.FeatureMissingDoneWhen, Deficiencies.FeatureMissingDescription,
            Deficiencies.EpicMissingOutcome, Deficiencies.EpicMissingSuccessPicture,
            Deficiencies.ThemeMissingPurpose, Deficiencies.ThemeMissingScope,
        };

        /// <summary>Deficiency codes the deterministic metadata repair can resolve.</summary>
        public static readonly string[] MetadataCodes =
        {
            Deficiencies.MissingMeta, Deficiencies.InvalidMeta, Deficiencies.StoryMissingPoints, Deficiencies.StoryInvalidPoints,
            Deficiencies.StoryMissingSprint, Deficiencies.StoryHasEffort, Deficiencies.FeatureHasPoints, Deficiencies.FeatureMissingEffort,
            Deficiencies.FeatureInvalidEffort, Deficiencies.EpicMissingTheme,
        };

        public static bool IsContentGap(string code) { return Array.IndexOf(ContentCodes, code) >= 0; }
        public static bool IsMetadataGap(string code) { return Array.IndexOf(MetadataCodes, code) >= 0; }

        public static string SystemPrompt()
        {
            return "You are Halyard, an Agile backlog refinement assistant working inside a sandbox. You receive one work item (a THEME, EPIC, FEATURE, STORY or TASK) " +
                   "whose definition has gaps, plus the gaps and some context. Draft ONLY the missing sections and return the complete narrative in Markdown.\n" +
                   "Rules:\n" +
                   "- Keep every existing sentence and section of the current narrative verbatim; add, do not rewrite.\n" +
                   "- Use these exact section headings when adding: STORY: '## User need' (As a <role>, I want <goal> so that <benefit>), '## Acceptance criteria' (3-6 testable '- [ ]' checkboxes), '## Tasks' ('- [ ]' checklist). " +
                   "FEATURE: '## Description', '## Definition of done' ('- [ ]' checklist). EPIC: '## Outcome', '## Success picture' (3-7 bullets). THEME: '## Purpose', '## In scope', '## Out of scope'.\n" +
                   "- Be specific to the item's title, parent and siblings; never invent people, dates, systems or credentials; mark anything uncertain with (to confirm).\n" +
                   "- Never include a ~~~halyard-meta block or a '### Halyard' mirror; those are managed by the tool.\n" +
                   "- Return ONLY a JSON object: {\"narrative\": \"<full markdown>\", \"meta\": {<optional halyard-meta keys to set, e.g. \"points\": \"TBD\">}, \"note\": \"<one sentence on what you added>\"}.";
        }

        public static string UserPrompt(WorkItem item, IReadOnlyList<string> gaps, WorkItem parent, IReadOnlyList<WorkItem> siblings)
        {
            var sb = new StringBuilder();
            sb.Append("Type: ").Append(WorkItemTypes.ToToken(item.Type)).Append('\n');
            sb.Append("Title: ").Append(Descriptions.ShortName(item.Title)).Append('\n');
            sb.Append("Id: ").Append(item.CanonicalId).Append('\n');
            sb.Append("Gaps to close: ").Append(string.Join(", ", gaps.Select(g => g + " (" + (Deficiencies.Find(g) == null ? string.Empty : Deficiencies.Find(g).Explanation ?? string.Empty) + ")"))).Append("\n\n");
            if (parent != null)
            {
                sb.Append("Parent ").Append(WorkItemTypes.ToToken(parent.Type)).Append(": ").Append(Descriptions.ShortName(parent.Title)).Append('\n');
                var pn = Descriptions.Narrative(parent.Body);
                if (pn.Length > 700) pn = pn.Substring(0, 700) + "\n…(truncated)";
                sb.Append(pn).Append("\n\n");
            }
            if (siblings.Count > 0)
            {
                sb.Append("Sibling items under the same parent:\n");
                foreach (var s in siblings.Take(8)) sb.Append("- ").Append(Descriptions.ShortName(s.Title)).Append('\n');
                sb.Append('\n');
            }
            var narrative = Descriptions.Narrative(item.Body);
            sb.Append("Current narrative (keep verbatim; add what is missing):\n");
            sb.Append(narrative.Length == 0 ? "(empty)" : narrative.Length > 3500 ? narrative.Substring(0, 3500) + "\n…(truncated)" : narrative);
            sb.Append("\n\nRespond with the JSON object only.");
            return sb.ToString();
        }

        /// <summary>Items with at least one content gap, in report order.</summary>
        public static List<ItemReview> Candidates(ReviewReport report, RefineOptions options)
        {
            return report.Items
                .Where(i => i.Type != WorkItemType.Unclassified && i.Deficiencies.Any(IsContentGap))
                .Where(i => options.OnlyNumbers == null || options.OnlyNumbers.Contains(i.Number))
                .Where(i => options.OnlyKeys == null || options.OnlyKeys.Contains(i.Key))
                .ToList();
        }

        public static async Task<ProposalBatch> RunAsync(IReadOnlyList<WorkItem> items, ReviewReport report, ILlmClient llm, RefineOptions options, RepositoryRef target,
            IProgress<string> progress, CancellationToken cancellation)
        {
            options = options ?? new RefineOptions();
            var batch = new ProposalBatch { Kind = ProposalKind.Refinement, Target = target.Key, Source = "llm:" + LlmSettings.ProfileToken(llm.Profile) + "/" + llm.Model };
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            var candidates = Candidates(report, options);
            batch.Considered = candidates.Count;
            if (candidates.Count == 0)
            {
                batch.Warnings.Add("No items with content gaps" + (options.OnlyNumbers == null ? "." : " among the selected items."));
                return batch;
            }
            var slice = candidates.Take(options.MaxItems).ToList();
            batch.Skipped = candidates.Count - slice.Count;
            if (batch.Skipped > 0) batch.SkipReasons.Add(batch.Skipped + " more items with gaps were not sent this time (raise the limit or run again).");
            var raw = new StringBuilder();
            var done = 0;
            foreach (var review in slice)
            {
                cancellation.ThrowIfCancellationRequested();
                done++;
                WorkItem item;
                if (!byKey.TryGetValue(review.Key, out item) || item == null) continue;
                var gaps = review.Deficiencies.Where(IsContentGap).ToList();
                WorkItem parent = null;
                if (review.ResolvedParentKey != null) byKey.TryGetValue(review.ResolvedParentKey, out parent);
                var siblings = parent == null ? new List<WorkItem>() : report.Items.Where(x => x.ResolvedParentKey == parent.Key && x.Key != item.Key).Select(x => { WorkItem w; return byKey.TryGetValue(x.Key, out w) ? w : null; }).Where(w => w != null).Select(w => w).ToList();
                if (progress != null) progress.Report("Drafting " + done + "/" + slice.Count + ": " + Descriptions.ShortName(item.Title));
                LlmResponse response;
                try
                {
                    response = await llm.ChatAsync(new[] { LlmMessage.System(SystemPrompt()), LlmMessage.User(UserPrompt(item, gaps, parent, siblings)) }, cancellation).ConfigureAwait(false);
                }
                catch (Providers.ProviderHttpException ex)
                {
                    batch.Warnings.Add("Model call failed on " + item.CanonicalId + ": " + ex.Message);
                    if (ex.Status == 401 || ex.Status == 403 || ex.Status == 429) break;
                    continue;
                }
                raw.Append("### ").Append(item.CanonicalId).Append('\n').Append(response.Text).Append("\n\n");
                var draft = ParseReply(response.Text);
                if (draft == null)
                {
                    batch.Warnings.Add("Reply for " + item.CanonicalId + " had no usable narrative; skipped.");
                    continue;
                }
                batch.Proposals.Add(Build(item, gaps, draft, batch));
            }
            batch.RawModelOutput = LlmClient.Redact(raw.ToString());
            return batch;
        }

        /// <summary>Turns a draft into a checked proposal: re-evaluates the DoR rules and measures how much original text survived.</summary>
        public static Proposal Build(WorkItem item, IReadOnlyList<string> gaps, RefinementDraft draft, ProposalBatch batch)
        {
            var current = Descriptions.Narrative(item.Body);
            var proposedNarrative = Descriptions.Narrative(draft.Narrative).Trim();
            var meta = item.Meta == null ? new HalyardMeta() : item.Meta.Clone();
            if (!meta.Has("type")) meta.Type = item.Type;
            foreach (var kv in draft.Meta)
            {
                // The model may only touch placeholder-safe keys; structure (type, id, parent, theme) stays with the deterministic paths.
                if (kv.Key == "points" || kv.Key == "sprint" || kv.Key == "effort" || kv.Key == "status" || kv.Key == "milestone") meta.Set(kv.Key, kv.Value);
            }
            var remaining = Deficiencies.Evaluate(item.Type, meta, Descriptions.Compose(meta, proposedNarrative)).Where(c => gaps.Contains(c)).ToList();
            var preserved = Preservation(current, proposedNarrative);
            var confidence = remaining.Count == 0 && preserved >= 0.7 ? "high" : remaining.Count == 0 || preserved >= 0.7 ? "medium" : "low";
            var reason = draft.Note.Length > 0 ? draft.Note : "Drafted the missing sections.";
            if (remaining.Count > 0) reason += " Still open after this draft: " + string.Join(", ", remaining) + ".";
            if (preserved < 0.7) reason += " Only " + (int)Math.Round(preserved * 100) + "% of the original lines survive; check the diff carefully.";
            var allowedMeta = draft.Meta.Where(kv => kv.Key == "points" || kv.Key == "sprint" || kv.Key == "effort" || kv.Key == "status" || kv.Key == "milestone").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return new Proposal
            {
                BatchId = batch.Id,
                Kind = ProposalKind.Refinement,
                ItemKey = item.Key,
                ItemNumber = item.Number,
                ItemCanonicalId = item.CanonicalId,
                ItemTitle = item.Title,
                CurrentValue = current,
                ProposedValue = proposedNarrative,
                ProposedMeta = allowedMeta.Count == 0 ? null : allowedMeta,
                Addresses = gaps.ToList(),
                Remaining = remaining,
                Confidence = confidence,
                Reason = reason,
                Source = batch.Source + " " + draft.Source,
            };
        }

        /// <summary>Share of the original narrative's substantive lines that appear verbatim in the proposed one.</summary>
        public static double Preservation(string original, string proposed)
        {
            var lines = Lines(original);
            if (lines.Count == 0) return 1.0;
            var set = new HashSet<string>(Lines(proposed), StringComparer.Ordinal);
            return lines.Count(l => set.Contains(l)) / (double)lines.Count;
        }

        private static List<string> Lines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 3 && !l.StartsWith(">")).ToList();
        }

        /// <summary>Tolerant parse: a JSON object (possibly fenced) with a narrative, or raw Markdown with headings.</summary>
        public static RefinementDraft ParseReply(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            var fence = Regex.Match(t, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
            var candidate = fence.Success ? fence.Groups[1].Value : t;
            var start = candidate.IndexOf('{');
            var end = candidate.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    var obj = JObject.Parse(candidate.Substring(start, end - start + 1));
                    var narrative = (string)obj["narrative"] ?? (string)obj["body"] ?? (string)obj["markdown"];
                    if (!string.IsNullOrWhiteSpace(narrative))
                    {
                        var draft = new RefinementDraft { Narrative = narrative, Note = ((string)obj["note"] ?? string.Empty).Trim(), Source = "json" };
                        var meta = obj["meta"] as JObject;
                        if (meta != null)
                            foreach (var p in meta.Properties()) draft.Meta[p.Name.Trim().ToLowerInvariant()] = p.Value.Type == JTokenType.Null ? null : p.Value.ToString().Trim();
                        return draft;
                    }
                }
                catch (JsonException) { }
            }
            // Markdown fallback: strip a code fence and accept when it has at least one heading.
            var md = Regex.Replace(t, @"^```[a-z]*\s*|\s*```$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Multiline).Trim();
            if (Regex.IsMatch(md, @"(?m)^#{1,4}\s+\S")) return new RefinementDraft { Narrative = md, Source = "markdown" };
            return null;
        }

        /// <summary>
        /// Deterministic metadata repair: builds or completes the halyard-meta block from what the item already
        /// says (type label, title, theme/area labels) and policy placeholders (TBD), without a model.
        /// </summary>
        public static ProposalBatch MetadataProposals(IReadOnlyList<WorkItem> items, ReviewReport report, Taxonomy taxonomy, RepositoryRef target, HashSet<long> onlyNumbers = null)
        {
            var batch = new ProposalBatch { Kind = ProposalKind.Metadata, Target = target.Key, Source = "deterministic" };
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            foreach (var review in report.Items)
            {
                if (onlyNumbers != null && !onlyNumbers.Contains(review.Number)) continue;
                WorkItem item;
                if (!byKey.TryGetValue(review.Key, out item) || item == null) continue;
                var gaps = review.Deficiencies.Where(IsMetadataGap).ToList();
                if (review.MissingMeta.Count > 0 && !gaps.Contains(Deficiencies.MissingMeta) && !gaps.Contains(Deficiencies.InvalidMeta)) gaps.Add("missing_required_meta:" + string.Join("+", review.MissingMeta));
                if (gaps.Count == 0) continue;
                batch.Considered++;
                if (item.Type == WorkItemType.Unclassified)
                {
                    batch.Skipped++;
                    batch.SkipReasons.Add("#" + item.Number + ": no type:: label or meta type; a human must classify it first.");
                    continue;
                }
                var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var reasons = new List<string>();
                var meta = item.Meta ?? new HalyardMeta();
                if (!meta.Has("type")) { changes["type"] = WorkItemTypes.ToToken(item.Type); reasons.Add("type from the type:: label"); }
                if (!meta.Has("id")) { changes["id"] = Descriptions.MakeId(item.Type, Descriptions.ShortName(item.Title)); reasons.Add("id from the title"); }
                if (!meta.Has("state")) changes["state"] = item.State == WorkItemState.Closed ? "closed" : "open";
                var theme = item.LabelValue(Safety.Labels.ThemePrefix);
                if (!meta.Has("theme") && theme != null && item.Type != WorkItemType.Theme) { changes["theme"] = theme; reasons.Add("theme from the theme:: label"); }
                var area = item.LabelValue(Safety.Labels.AreaPrefix);
                if (!meta.Has("area") && area != null) { changes["area"] = area; reasons.Add("area from the area:: label"); }
                switch (item.Type)
                {
                    case WorkItemType.Story:
                        if (!meta.Has("points") || !Sizing.IsValidPoints(meta.Points)) { changes["points"] = Sizing.Tbd; reasons.Add(meta.Has("points") ? "points " + meta.Points + " is not a policy value; TBD until re-estimated" : "points TBD until estimated"); }
                        if (!meta.Has("sprint")) { changes["sprint"] = Sizing.Tbd; reasons.Add("sprint TBD until planned"); }
                        if (meta.Has("effort")) { changes["effort"] = null; reasons.Add("stories do not carry an effort band"); }
                        break;
                    case WorkItemType.Feature:
                        if (!meta.Has("effort") || !Sizing.IsValidEffort(meta.Effort)) { changes["effort"] = Sizing.Tbd; reasons.Add("effort TBD until estimated"); }
                        if (meta.Has("points")) { changes["points"] = null; reasons.Add("features do not carry story points"); }
                        break;
                    case WorkItemType.Theme:
                        if (!meta.Has("status")) changes["status"] = "active";
                        break;
                }
                foreach (var key in taxonomy.MissingKeys(item.Type, meta))
                    if (!changes.ContainsKey(key) && !meta.Has(key)) changes[key] = key == "theme" ? (theme ?? string.Empty) : key == "area" ? (area ?? string.Empty) : string.Empty;
                if (changes.Count == 0) { batch.Skipped++; continue; }
                var confidence = changes.ContainsKey("id") ? "medium" : "high";
                if (changes.Values.Any(v => v == string.Empty)) confidence = "low";
                batch.Proposals.Add(new Proposal
                {
                    BatchId = batch.Id,
                    Kind = ProposalKind.Metadata,
                    ItemKey = item.Key,
                    ItemNumber = item.Number,
                    ItemCanonicalId = item.CanonicalId,
                    ItemTitle = item.Title,
                    CurrentValue = item.Meta == null ? null : item.Meta.ToFence(),
                    ProposedValue = string.Join("\n", changes.Select(kv => kv.Key + ": " + (kv.Value ?? "(remove)"))),
                    ProposedMeta = changes,
                    Addresses = gaps,
                    Confidence = confidence,
                    Reason = reasons.Count == 0 ? "Completes the required halyard-meta keys." : string.Join("; ", reasons) + ".",
                    Source = "deterministic",
                });
            }
            return batch;
        }
    }
}
