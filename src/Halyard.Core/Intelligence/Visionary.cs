using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Halyard.Core.Intelligence
{
    public sealed class VisionTheme
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public List<string> Epics { get; set; } = new List<string>();
        public bool IsNew { get; set; }
    }

    public sealed class VisionPriority
    {
        public int Rank { get; set; }
        public string Epic { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Why { get; set; } = string.Empty;
    }

    /// <summary>A drafted portfolio vision: the document plus the structured parts the proposals are built from.</summary>
    public sealed class VisionDraft
    {
        public string Id { get; set; } = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Source { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Vision { get; set; } = string.Empty;
        public string Markdown { get; set; } = string.Empty;
        public List<VisionTheme> Themes { get; set; } = new List<VisionTheme>();
        public List<VisionPriority> Priorities { get; set; } = new List<VisionPriority>();
        public List<string> Risks { get; set; } = new List<string>();
        public List<string> NextQuarter { get; set; } = new List<string>();
        public string? ProposalBatchId { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class VisionStore
    {
        private readonly string _directory;
        public VisionStore(string directory) { _directory = directory; }

        public void Save(VisionDraft draft)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "vision-" + draft.Id + ".json"), JsonConvert.SerializeObject(draft, Formatting.Indented), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_directory, "vision-" + draft.Id + ".md"), draft.Markdown, new UTF8Encoding(false));
        }

        public List<VisionDraft> LoadAll()
        {
            var list = new List<VisionDraft>();
            if (!Directory.Exists(_directory)) return list;
            foreach (var f in Directory.GetFiles(_directory, "vision-*.json"))
            {
                try { var d = JsonConvert.DeserializeObject<VisionDraft>(File.ReadAllText(f)); if (d != null) list.Add(d); }
                catch (JsonException) { }
            }
            return list.OrderByDescending(d => d.CreatedAt).ToList();
        }

        public VisionDraft? Load(string id)
        {
            var path = Path.Combine(_directory, "vision-" + id + ".json");
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<VisionDraft>(File.ReadAllText(path));
        }
    }

    /// <summary>
    /// The "upward" half of the product owner: reads the whole backlog (themes, epics, features, progress,
    /// blockers, skill gaps) and drafts a portfolio vision with priorities, risks and the next quarter, plus
    /// theme-membership proposals for epics the draft places differently. Deterministic summary without a
    /// model; the model adds judgement on top of the same facts.
    /// </summary>
    public static class Visionary
    {
        public static string SystemPrompt()
        {
            return "You are Halyard acting as a product owner for an engineering portfolio. From the backlog facts you are given (themes, epics, features with progress, velocity, " +
                   "ready backlog, blockers, skill gaps) draft a portfolio vision for leadership. Be concrete and grounded in the items given; do not invent systems, people or dates. " +
                   "Return ONLY a JSON object with keys: " +
                   "\"vision\" (2-3 sentences), " +
                   "\"themes\" (array of {\"id\": existing THEME id or \"NEW\", \"title\", \"purpose\" (1-2 sentences), \"epics\": [EPIC ids that belong under it]}), " +
                   "\"priorities\" (array of {\"rank\", \"epic\": EPIC id, \"why\" one sentence}, most important first, at most 8), " +
                   "\"risks\" (array of strings, from blockers, skill gaps, thin backlog, at-risk features), " +
                   "\"nextQuarter\" (array of 3-6 outcome statements), " +
                   "\"narrative\" (Markdown: '## Vision', '## Strategic themes', '## Priorities', '## Risks', '## Next quarter'; concise, leadership-level, no story checklists).";
        }

        public static string UserPrompt(IReadOnlyList<WorkItem> items, DeliveryReport delivery, IReadOnlyDictionary<string, int> skillGaps)
        {
            var sb = new StringBuilder();
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            sb.Append("Sprint ").Append(delivery.CurrentSprint).Append("; average velocity ").Append(delivery.AverageVelocity).Append(" pts; ready backlog ").Append(delivery.Ready.Stories).Append(" stories / ").Append(delivery.Ready.Points).Append(" pts; open blockers ").Append(delivery.Blockers.Count).Append(".\n\n");
            sb.Append("THEMES\n");
            foreach (var t in items.Where(i => i.Type == WorkItemType.Theme))
                sb.Append("- ").Append(t.CanonicalId).Append(": ").Append(Descriptions.ShortName(t.Title)).Append(" — ").Append(Excerpt(t.Body, "Purpose", 220)).Append('\n');
            sb.Append("\nEPICS (id: title [theme] — outcome)\n");
            foreach (var e in items.Where(i => i.Type == WorkItemType.Epic))
                sb.Append("- ").Append(e.CanonicalId).Append(": ").Append(Descriptions.ShortName(e.Title)).Append(" [").Append(e.ThemeId ?? "no theme").Append("] — ").Append(Excerpt(e.Body, "Outcome", 200)).Append('\n');
            sb.Append("\nFEATURES (title [epic] status % target)\n");
            foreach (var f in delivery.Features)
                sb.Append("- ").Append(f.Title).Append(" [").Append(f.Epic ?? "?").Append("] ").Append(f.Status).Append(' ').Append(f.Percent).Append("% target ").Append(f.Target ?? "none").Append(f.Blocked > 0 ? " (" + f.Blocked + " blocked)" : string.Empty).Append('\n');
            if (delivery.Blockers.Count > 0)
            {
                sb.Append("\nBLOCKERS\n");
                foreach (var b in delivery.Blockers) sb.Append("- ").Append(b.Title).Append(" (").Append(b.Type).Append(", ").Append(b.AgeBusinessDays).Append(" business days)").Append(b.Reason != null ? ": " + b.Reason : string.Empty).Append('\n');
            }
            if (skillGaps.Count > 0) sb.Append("\nSKILL GAPS (nobody on the team): ").Append(string.Join(", ", skillGaps.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))).Append('\n');
            sb.Append("\nRespond with the JSON object only.");
            return sb.ToString();
        }

        private static string Excerpt(string? body, string heading, int max)
        {
            var narrative = Descriptions.Narrative(body);
            var m = Regex.Match(narrative, @"(?im)^##\s*" + Regex.Escape(heading) + @"\s*\r?\n(?<t>(?:(?!^#).*\r?\n?)*)");
            var text = (m.Success ? m.Groups["t"].Value : narrative).Replace("\r", string.Empty).Replace("\n", " ").Trim();
            text = Regex.Replace(text, @"\s+", " ");
            return text.Length > max ? text.Substring(0, max) + "…" : text;
        }

        /// <summary>Deterministic portfolio summary from structure and progress alone. Used without a model and as the skeleton the model fills.</summary>
        public static VisionDraft Summarize(IReadOnlyList<WorkItem> items, DeliveryReport delivery, IReadOnlyDictionary<string, int> skillGaps, string repository)
        {
            var draft = new VisionDraft { Source = "deterministic", Repository = repository };
            var themes = items.Where(i => i.Type == WorkItemType.Theme).ToList();
            var epics = items.Where(i => i.Type == WorkItemType.Epic).ToList();
            foreach (var t in themes)
                draft.Themes.Add(new VisionTheme { Id = t.CanonicalId, Title = Descriptions.ShortName(t.Title), Purpose = Excerpt(t.Body, "Purpose", 240), Epics = epics.Where(e => string.Equals(e.ThemeId, t.CanonicalId, StringComparison.OrdinalIgnoreCase)).Select(e => e.CanonicalId).ToList() });
            var orphanEpics = epics.Where(e => string.IsNullOrWhiteSpace(e.ThemeId) || !themes.Any(t => string.Equals(t.CanonicalId, e.ThemeId, StringComparison.OrdinalIgnoreCase))).ToList();
            // Priorities: epics by at-risk features first, then earliest target, then least progress.
            var rank = 0;
            foreach (var e in epics.OrderByDescending(e => delivery.Features.Count(f => f.Epic == Descriptions.ShortName(e.Title) && f.Status == FeatureStatus.AtRisk)).ThenBy(e => delivery.Features.Where(f => f.Epic == Descriptions.ShortName(e.Title)).Select(f => f.Target ?? "9999").DefaultIfEmpty("9999").Min()).Take(8))
            {
                var feats = delivery.Features.Where(f => f.Epic == Descriptions.ShortName(e.Title)).ToList();
                var pct = feats.Count == 0 ? 0 : (int)Math.Round(feats.Average(f => f.Percent));
                var atRisk = feats.Count(f => f.Status == FeatureStatus.AtRisk);
                draft.Priorities.Add(new VisionPriority { Rank = ++rank, Epic = e.CanonicalId, Title = Descriptions.ShortName(e.Title), Why = feats.Count + " feature(s), " + pct + "% done" + (atRisk > 0 ? ", " + atRisk + " at risk" : string.Empty) + (feats.Count > 0 ? ", earliest target " + feats.Select(f => f.Target ?? "none").OrderBy(x => x).First() : string.Empty) });
            }
            foreach (var b in delivery.Blockers.Where(b => b.Escalated)) draft.Risks.Add("Blocked " + b.AgeBusinessDays + " business days: " + b.Title + (b.Impact != null ? " (" + b.Impact + ")" : string.Empty));
            foreach (var f in delivery.Features.Where(f => f.Status == FeatureStatus.AtRisk)) draft.Risks.Add("At risk: " + f.Title + " — " + f.Reason);
            if (!delivery.Ready.Healthy) draft.Risks.Add("Ready backlog is thin: " + delivery.Ready.Points + " pts against a velocity of " + delivery.AverageVelocity + ".");
            foreach (var g in skillGaps.OrderByDescending(kv => kv.Value).Take(5)) draft.Risks.Add("Skill gap: nobody on the team has " + g.Key + " (" + g.Value + " planned stor" + (g.Value == 1 ? "y" : "ies") + ").");
            foreach (var f in delivery.Features.Where(f => f.Status != FeatureStatus.Done).OrderBy(f => f.Target ?? "9999").Take(5)) draft.NextQuarter.Add(f.Title + " delivered" + (f.Target != null ? " by " + f.Target : string.Empty) + " (" + f.Percent + "% today)");
            draft.Vision = "Deliver " + string.Join(" and ", draft.Themes.Select(t => t.Title.ToLowerInvariant())) + " through " + epics.Count + " epics and " + delivery.Features.Count + " features, " + delivery.FeaturesOnTrack + " of them on track, at about " + delivery.AverageVelocity + " points a sprint.";
            if (orphanEpics.Count > 0) draft.Warnings.Add(orphanEpics.Count + " epic(s) have no theme: " + string.Join(", ", orphanEpics.Select(e => e.CanonicalId)));
            draft.Markdown = RenderMarkdown(draft, delivery);
            return draft;
        }

        public static async Task<VisionDraft> DraftAsync(IReadOnlyList<WorkItem> items, DeliveryReport delivery, IReadOnlyDictionary<string, int> skillGaps, ILlmClient llm, string repository, CancellationToken cancellation)
        {
            var draft = Summarize(items, delivery, skillGaps, repository);
            draft.Source = "llm:" + LlmSettings.ProfileToken(llm.Profile) + "/" + llm.Model;
            LlmResponse response;
            try { response = await llm.ChatAsync(new[] { LlmMessage.System(SystemPrompt()), LlmMessage.User(UserPrompt(items, delivery, skillGaps)) }, cancellation).ConfigureAwait(false); }
            catch (Providers.ProviderHttpException ex)
            {
                draft.Warnings.Add("Model call failed; this is the deterministic summary. " + ex.Message);
                draft.Source = "deterministic (model failed)";
                return draft;
            }
            var obj = ParseObject(response.Text);
            if (obj == null) { draft.Warnings.Add("The model reply could not be parsed; this is the deterministic summary."); draft.Source = "deterministic (reply unparsed)"; return draft; }
            var themeIds = new HashSet<string>(items.Where(i => i.Type == WorkItemType.Theme).Select(i => i.CanonicalId), StringComparer.OrdinalIgnoreCase);
            var epicIds = new HashSet<string>(items.Where(i => i.Type == WorkItemType.Epic).Select(i => i.CanonicalId), StringComparer.OrdinalIgnoreCase);
            var vision = (string?)obj["vision"];
            if (!string.IsNullOrWhiteSpace(vision)) draft.Vision = vision!.Trim();
            if (obj["themes"] is JArray themes && themes.Count > 0)
            {
                draft.Themes.Clear();
                foreach (var t in themes.OfType<JObject>())
                {
                    var id = ((string?)t["id"] ?? "NEW").Trim();
                    var vt = new VisionTheme { Id = themeIds.Contains(id) ? id : "NEW", IsNew = !themeIds.Contains(id), Title = ((string?)t["title"] ?? id).Trim(), Purpose = ((string?)t["purpose"] ?? string.Empty).Trim() };
                    vt.Epics = (t["epics"] as JArray ?? new JArray()).Select(e => ((string?)e ?? string.Empty).Trim()).Where(e => epicIds.Contains(e)).ToList();
                    draft.Themes.Add(vt);
                }
            }
            if (obj["priorities"] is JArray pr && pr.Count > 0)
            {
                draft.Priorities.Clear();
                var rank = 0;
                foreach (var p in pr.OfType<JObject>())
                {
                    var epic = ((string?)p["epic"] ?? string.Empty).Trim();
                    if (!epicIds.Contains(epic)) continue;
                    var item = items.First(i => string.Equals(i.CanonicalId, epic, StringComparison.OrdinalIgnoreCase));
                    draft.Priorities.Add(new VisionPriority { Rank = ++rank, Epic = item.CanonicalId, Title = Descriptions.ShortName(item.Title), Why = ((string?)p["why"] ?? string.Empty).Trim() });
                }
            }
            if (obj["risks"] is JArray risks && risks.Count > 0) draft.Risks = risks.Select(r => ((string?)r ?? string.Empty).Trim()).Where(r => r.Length > 0).ToList();
            if (obj["nextQuarter"] is JArray nq && nq.Count > 0) draft.NextQuarter = nq.Select(r => ((string?)r ?? string.Empty).Trim()).Where(r => r.Length > 0).ToList();
            var narrative = (string?)obj["narrative"];
            draft.Markdown = string.IsNullOrWhiteSpace(narrative) ? RenderMarkdown(draft, delivery) : "# Portfolio vision\n\n_Drafted by " + draft.Source + " on " + draft.CreatedAt.ToString("yyyy-MM-dd") + " from " + repository + "._\n\n" + narrative!.Trim() + "\n\n" + Appendix(draft, delivery);
            return draft;
        }

        /// <summary>Theme-membership proposals for epics the draft places under an existing theme that differs from their declared one.</summary>
        public static ProposalBatch ThemeProposals(VisionDraft draft, IReadOnlyList<WorkItem> items, RepositoryRef target, HashSet<string>? onlyKeys = null)
        {
            var batch = new ProposalBatch { Kind = ProposalKind.ThemeLink, Target = target.Key, Source = draft.Source };
            var epics = items.Where(i => i.Type == WorkItemType.Epic).ToDictionary(i => i.CanonicalId, i => i, StringComparer.OrdinalIgnoreCase);
            foreach (var theme in draft.Themes.Where(t => !t.IsNew))
            {
                foreach (var epicId in theme.Epics)
                {
                    WorkItem? epic;
                    if (!epics.TryGetValue(epicId, out epic) || epic == null) continue;
                    if (onlyKeys != null && !onlyKeys.Contains(epic.Key)) continue;
                    batch.Considered++;
                    if (string.Equals(epic.ThemeId, theme.Id, StringComparison.OrdinalIgnoreCase)) { batch.Skipped++; continue; }
                    batch.Proposals.Add(new Proposal
                    {
                        BatchId = batch.Id, Kind = ProposalKind.ThemeLink, ItemKey = epic.Key, ItemNumber = epic.Number, ItemCanonicalId = epic.CanonicalId, ItemTitle = epic.Title,
                        CurrentValue = epic.ThemeId, ProposedValue = theme.Id, ProposedMeta = new Dictionary<string, string?> { { "theme", theme.Id }, { "parent", theme.Id } },
                        Confidence = string.IsNullOrWhiteSpace(epic.ThemeId) ? "medium" : "low",
                        Reason = "The vision draft places this epic under " + theme.Title + (string.IsNullOrWhiteSpace(epic.ThemeId) ? " (no theme declared today)." : " instead of " + epic.ThemeId + "."),
                        Source = draft.Source,
                    });
                }
            }
            foreach (var t in draft.Themes.Where(t => t.IsNew)) batch.Warnings.Add("Proposed new theme \"" + t.Title + "\" for " + t.Epics.Count + " epic(s); create it by hand, then re-run the draft.");
            return batch;
        }

        private static string RenderMarkdown(VisionDraft d, DeliveryReport delivery)
        {
            var sb = new StringBuilder();
            sb.Append("# Portfolio vision\n\n_Drafted by ").Append(d.Source).Append(" on ").Append(d.CreatedAt.ToString("yyyy-MM-dd")).Append(" from ").Append(d.Repository).Append("._\n\n");
            sb.Append("## Vision\n\n").Append(d.Vision).Append("\n\n");
            sb.Append("## Strategic themes\n\n");
            foreach (var t in d.Themes) sb.Append("### ").Append(t.Title).Append(t.IsNew ? " (proposed new theme)" : string.Empty).Append("\n\n").Append(t.Purpose).Append("\n\n").Append(t.Epics.Count > 0 ? "Epics: " + string.Join(", ", t.Epics) + "\n\n" : string.Empty);
            sb.Append("## Priorities\n\n");
            foreach (var p in d.Priorities) sb.Append(p.Rank).Append(". **").Append(p.Title).Append("** — ").Append(p.Why).Append('\n');
            sb.Append("\n## Risks\n\n");
            foreach (var r in d.Risks) sb.Append("- ").Append(r).Append('\n');
            if (d.Risks.Count == 0) sb.Append("- None surfaced by the backlog.\n");
            sb.Append("\n## Next quarter\n\n");
            foreach (var n in d.NextQuarter) sb.Append("- ").Append(n).Append('\n');
            sb.Append('\n').Append(Appendix(d, delivery));
            return sb.ToString();
        }

        private static string Appendix(VisionDraft d, DeliveryReport delivery)
        {
            var sb = new StringBuilder();
            sb.Append("## Facts this draft rests on\n\n");
            sb.Append("- Sprint ").Append(delivery.CurrentSprint).Append(", average velocity ").Append(delivery.AverageVelocity).Append(" pts, ready backlog ").Append(delivery.Ready.Stories).Append(" stories (").Append(delivery.Ready.Points).Append(" pts)\n");
            sb.Append("- Features: ").Append(delivery.FeaturesOnTrack).Append(" of ").Append(delivery.Features.Count).Append(" on track; blockers: ").Append(delivery.Blockers.Count).Append('\n');
            foreach (var w in d.Warnings) sb.Append("- Note: ").Append(w).Append('\n');
            return sb.ToString();
        }

        private static JObject? ParseObject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text!;
            var fence = Regex.Match(t, @"```(?:json)?\s*([\s\S]*?)```");
            if (fence.Success) t = fence.Groups[1].Value;
            var start = t.IndexOf('{');
            var end = t.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try { return JObject.Parse(t.Substring(start, end - start + 1)); } catch (JsonException) { return null; }
        }
    }
}
