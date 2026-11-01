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
using Halyard.Core.Planning;
using Halyard.Core.Schema;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    public sealed class SkillTagOptions
    {
        /// <summary>Only items without a <c>skills:</c> entry. Default true.</summary>
        public bool OnlyUntagged { get; set; }
        public int MaxItems { get; set; }
        public int BatchSize { get; set; }
        public int MaxSkillsPerItem { get; set; }
        public HashSet<long> OnlyNumbers { get; set; }
        public HashSet<string> OnlyKeys { get; set; }

        public SkillTagOptions()
        {
            OnlyUntagged = true;
            MaxItems = 40;
            BatchSize = 12;
            MaxSkillsPerItem = 3;
        }
    }

    /// <summary>
    /// Proposes <c>skills:</c> for stories and features using the shared taxonomy (Shellback ids), so the
    /// sprint planner can match work to people. Deterministic mode scores skill names against the item's
    /// text and inherits from the parent feature; model mode asks for ids from the taxonomy list and
    /// validates every answer against it.
    /// </summary>
    public static class SkillTagger
    {
        private static readonly string[] StopWords = { "and", "the", "for", "with", "of", "to", "a", "an", "in", "on", "as", "by", "or", "is" };

        public static IReadOnlyList<string> CurrentSkills(WorkItem item)
        {
            var text = item.Meta == null ? null : item.Meta.Get("skills");
            if (string.IsNullOrWhiteSpace(text)) return new string[0];
            return text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        }

        public static List<WorkItem> Candidates(IReadOnlyList<WorkItem> items, SkillTagOptions options)
        {
            return items.Where(i => (i.Type == WorkItemType.Story || i.Type == WorkItemType.Feature) && i.State == WorkItemState.Open)
                .Where(i => !options.OnlyUntagged || CurrentSkills(i).Count == 0)
                .Where(i => options.OnlyNumbers == null || options.OnlyNumbers.Contains(i.Number))
                .Where(i => options.OnlyKeys == null || options.OnlyKeys.Contains(i.Key))
                .Take(options.MaxItems).ToList();
        }

        /// <summary>Deterministic suggestion: parent's skills first, then name matches against title and narrative.</summary>
        public static List<KeyValuePair<string, double>> Suggest(WorkItem item, WorkItem parent, SkillsTaxonomy taxonomy, int max = 3)
        {
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var title = Descriptions.ShortName(item.Title).ToLowerInvariant();
            var text = (title + " " + Descriptions.Narrative(item.Body)).ToLowerInvariant();
            var words = new HashSet<string>(Regex.Split(text, @"[^a-z0-9]+").Where(w => w.Length > 2));
            foreach (var skill in taxonomy.Skills)
            {
                var parts = Regex.Split(skill.Name.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 2 && Array.IndexOf(StopWords, w) < 0).ToList();
                if (parts.Count == 0) continue;
                var hits = parts.Count(p => words.Contains(p) || words.Contains(p.TrimEnd('s')) || words.Any(w => w.StartsWith(p) && w.Length <= p.Length + 3));
                if (hits == 0) continue;
                var score = hits / (double)parts.Count;
                var phrase = @"\b" + Regex.Escape(skill.Name.ToLowerInvariant()) + @"\b";
                if (Regex.IsMatch(text, phrase)) score += 1.0; // whole phrase anywhere
                if (Regex.IsMatch(title, phrase)) score += 0.5; // and in the title
                if (score >= 0.5) scores[skill.Id] = score;
            }
            if (parent != null)
                foreach (var s in CurrentSkills(parent)) if (taxonomy.Contains(s)) scores[s] = (scores.ContainsKey(s) ? scores[s] : 0) + 0.9;
            return scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(max).ToList();
        }

        public static ProposalBatch Deterministic(IReadOnlyList<WorkItem> items, ReviewReport report, SkillsTaxonomy taxonomy, SkillTagOptions options, RepositoryRef target)
        {
            options = options ?? new SkillTagOptions();
            var batch = new ProposalBatch { Kind = ProposalKind.Skills, Target = target.Key, Source = "deterministic" };
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            var parentKey = report.Items.ToDictionary(i => i.Key, i => i.ResolvedParentKey, StringComparer.OrdinalIgnoreCase);
            foreach (var item in Candidates(items, options))
            {
                batch.Considered++;
                string pk; WorkItem parent = null;
                if (parentKey.TryGetValue(item.Key, out pk) && pk != null) byKey.TryGetValue(pk, out parent);
                var suggested = Suggest(item, parent, taxonomy, options.MaxSkillsPerItem);
                if (suggested.Count == 0) { batch.Skipped++; batch.SkipReasons.Add(item.CanonicalId + ": no skill name matches the text and the parent has no skills"); continue; }
                var top = suggested[0].Value;
                var confidence = top >= 1.5 ? "high" : top >= 0.9 ? "medium" : "low";
                batch.Proposals.Add(Make(item, suggested.Select(kv => kv.Key).ToList(), confidence,
                    string.Join("; ", suggested.Select(kv => kv.Key + " (" + kv.Value.ToString("0.0") + (parent != null && CurrentSkills(parent).Contains(kv.Key) ? ", from parent" : ", name match") + ")")), "deterministic", batch));
            }
            return batch;
        }

        public static string SystemPrompt(SkillsTaxonomy taxonomy)
        {
            var sb = new StringBuilder();
            sb.Append("You are Halyard, tagging backlog items with the skills needed to deliver them, so work can be matched to people. ");
            sb.Append("Choose 1-3 skill ids per item from this taxonomy ONLY (id = name):\n");
            foreach (var d in taxonomy.Skills.GroupBy(s => s.Domain))
                sb.Append("- ").Append(taxonomy.Domains.ContainsKey(d.Key) ? taxonomy.Domains[d.Key] : d.Key).Append(": ").Append(string.Join(", ", d.Select(s => s.Id))).Append('\n');
            sb.Append("Return ONLY a JSON array, one object per item: {\"id\": \"<item id as given>\", \"skills\": [\"<skill id>\", ...], \"confidence\": \"low|medium|high\", \"reason\": \"one sentence\"}. Unknown skill ids are discarded.");
            return sb.ToString();
        }

        public static string UserPrompt(IReadOnlyList<WorkItem> items, IReadOnlyDictionary<string, WorkItem> parents)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.Append("### id: ").Append(item.CanonicalId).Append('\n');
                sb.Append("Type: ").Append(WorkItemTypes.ToToken(item.Type)).Append(" · Title: ").Append(Descriptions.ShortName(item.Title)).Append('\n');
                WorkItem parent;
                if (parents.TryGetValue(item.Key, out parent) && parent != null)
                    sb.Append("Parent: ").Append(Descriptions.ShortName(parent.Title)).Append(CurrentSkills(parent).Count > 0 ? " (skills: " + string.Join(", ", CurrentSkills(parent)) + ")" : string.Empty).Append('\n');
                var narrative = Descriptions.Narrative(item.Body);
                if (narrative.Length > 900) narrative = narrative.Substring(0, 900) + "…";
                sb.Append(narrative).Append("\n\n");
            }
            sb.Append("Respond with the JSON array only.");
            return sb.ToString();
        }

        public static async Task<ProposalBatch> RunAsync(IReadOnlyList<WorkItem> items, ReviewReport report, SkillsTaxonomy taxonomy, ILlmClient llm, SkillTagOptions options, RepositoryRef target, IProgress<string> progress, CancellationToken cancellation)
        {
            options = options ?? new SkillTagOptions();
            var batch = new ProposalBatch { Kind = ProposalKind.Skills, Target = target.Key, Source = "llm:" + llm.Name + "/" + llm.Model };
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            var parents = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in report.Items) { WorkItem p = null; if (r.ResolvedParentKey != null) byKey.TryGetValue(r.ResolvedParentKey, out p); parents[r.Key] = p; }
            var candidates = Candidates(items, options);
            batch.Considered = candidates.Count;
            if (candidates.Count == 0) { batch.Warnings.Add("No untagged stories or features in scope."); return batch; }
            var byId = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates) { byId[c.CanonicalId] = c; byId[c.Number.ToString()] = c; byId["#" + c.Number] = c; }
            var raw = new StringBuilder();
            for (var offset = 0; offset < candidates.Count; offset += options.BatchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                var slice = candidates.Skip(offset).Take(options.BatchSize).ToList();
                if (progress != null) progress.Report("Tagging " + (offset + slice.Count) + "/" + candidates.Count + " with " + llm.Model);
                LlmResponse response;
                try { response = await llm.ChatAsync(new[] { LlmMessage.System(SystemPrompt(taxonomy)), LlmMessage.User(UserPrompt(slice, parents)) }, cancellation).ConfigureAwait(false); }
                catch (Providers.ProviderHttpException ex) { batch.Warnings.Add("Model call failed: " + ex.Message); break; }
                raw.Append(response.Text).Append("\n\n");
                JArray rows = ParseArray(response.Text);
                if (rows == null) { batch.Warnings.Add("Model reply for " + slice.Count + " items could not be parsed."); continue; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows.OfType<JObject>())
                {
                    WorkItem item;
                    var id = (string)row["id"] ?? string.Empty;
                    if (!byId.TryGetValue(id, out item) || item == null || !seen.Add(item.Key)) continue;
                    var skills = (row["skills"] as JArray ?? new JArray()).Select(s => ((string)s ?? string.Empty).Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
                    var unknown = skills.Where(s => !taxonomy.Contains(s)).ToList();
                    skills = skills.Where(taxonomy.Contains).Take(options.MaxSkillsPerItem).ToList();
                    if (skills.Count == 0) { batch.Skipped++; batch.SkipReasons.Add(item.CanonicalId + ": model named no skill from the taxonomy" + (unknown.Count > 0 ? " (" + string.Join(", ", unknown) + ")" : string.Empty)); continue; }
                    var confidence = ((string)row["confidence"] ?? "medium").Trim().ToLowerInvariant();
                    if (confidence != "high" && confidence != "low") confidence = "medium";
                    var reason = ((string)row["reason"] ?? string.Empty).Trim();
                    if (unknown.Count > 0) reason += " [dropped unknown ids: " + string.Join(", ", unknown) + "]";
                    batch.Proposals.Add(Make(item, skills, confidence, reason, batch.Source + " json", batch));
                }
            }
            batch.RawModelOutput = LlmClient.Redact(raw.ToString());
            return batch;
        }

        private static Proposal Make(WorkItem item, List<string> skills, string confidence, string reason, string source, ProposalBatch batch)
        {
            var current = CurrentSkills(item);
            return new Proposal
            {
                BatchId = batch.Id, Kind = ProposalKind.Skills, ItemKey = item.Key, ItemNumber = item.Number, ItemCanonicalId = item.CanonicalId, ItemTitle = item.Title,
                CurrentValue = current.Count == 0 ? null : string.Join(", ", current), ProposedValue = string.Join(", ", skills),
                ProposedMeta = new Dictionary<string, string> { { "skills", string.Join(", ", skills) } },
                Confidence = confidence, Reason = reason, Source = source,
            };
        }

        private static JArray ParseArray(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text;
            var fence = Regex.Match(t, @"```(?:json)?\s*([\s\S]*?)```");
            if (fence.Success) t = fence.Groups[1].Value;
            var start = t.IndexOf('[');
            var end = t.LastIndexOf(']');
            if (start < 0 || end <= start)
            {
                try { var obj = JObject.Parse(t.Trim()); foreach (var p in obj.Properties()) { var a = p.Value as JArray; if (a != null) return a; } } catch (JsonException) { }
                return null;
            }
            try { return JArray.Parse(t.Substring(start, end - start + 1)); } catch (JsonException) { return null; }
        }
    }
}
