using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Halyard.Core.Intelligence
{
    /// <summary>One source item prepared for the sandbox: type, id, theme and the body it will be created with.</summary>
    public sealed class IntakeDraft
    {
        public string SourceKey { get; set; } = string.Empty;
        public string SourceRepository { get; set; } = string.Empty;
        public long SourceNumber { get; set; }
        public string? SourceUrl { get; set; }
        public string SourceTitle { get; set; } = string.Empty;
        public WorkItemType Type { get; set; }
        /// <summary>The level as the UI and labels spell it (STORY, FEATURE, ...).</summary>
        public string TypeToken { get { return WorkItemTypes.ToToken(Type); } }
        public string CanonicalId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Theme { get; set; }
        public string? Parent { get; set; }
        public string? Area { get; set; }
        public List<string> Labels { get; set; } = new List<string>();
        public string Narrative { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Confidence { get; set; } = "medium";
        public string Reason { get; set; } = string.Empty;
        /// <summary>Set when the sandbox already holds a copy of this source item.</summary>
        public string? ExistingKey { get; set; }
        public List<string> Gaps { get; set; } = new List<string>();
    }

    public sealed class IntakeManifest
    {
        public string Id { get; set; } = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        public string Target { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        public int Requested { get; set; }
        public List<IntakeRecord> Items { get; set; } = new List<IntakeRecord>();
        public List<string> Skipped { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public string Source { get; set; } = string.Empty;
    }

    public sealed class IntakeRecord
    {
        public string SourceKey { get; set; } = string.Empty;
        public string CanonicalId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public long Number { get; set; }
        public string Key { get; set; } = string.Empty;
        public string? Url { get; set; }
    }

    public sealed class IntakeOptions
    {
        public HashSet<string> Keys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string? Area { get; set; }
        public bool AddReviewLabel { get; set; } = true;
        public string? ManifestDirectory { get; set; }
    }

    /// <summary>
    /// Intake: the bridge from unstructured tickets in the source backlog to structured work items in the
    /// sandbox. Each copy carries the source reference (<c>source_project</c>, <c>source_iid</c>) so the link
    /// back is explicit, keeps the original text as its narrative, and starts the normal sandbox review
    /// (metadata repair, drafting, sizing, parents). The source repository is never written.
    /// </summary>
    public static class Intake
    {
        public const string Label = "cascade-intake";

        public static readonly LabelDefinition[] StandardLabels =
        {
            new LabelDefinition(Labels.Managed, "0f766e", "Created or maintained by Halyard"),
            new LabelDefinition(Label, "0e7490", "Copied from a source backlog by Halyard intake"),
            new LabelDefinition(Labels.ReviewQueue, "f59e0b", "Awaiting human structure review"),
        };

        /// <summary>Deterministic classification: declared meta, then type labels, then title and body shape.</summary>
        public static WorkItemType GuessType(WorkItem item, out string reason)
        {
            if (item.Meta != null && item.Meta.Type != WorkItemType.Unclassified) { reason = "declared in cascade-meta"; return item.Meta.Type; }
            var labels = item.TypeLabels;
            if (labels.Count == 1) { reason = "type:: label"; return labels[0]; }
            var title = Descriptions.ShortName(item.Title);
            var m = Regex.Match(item.Title, @"^\s*(THEME|EPIC|FEATURE|STORY|TASK|BUG)\b", RegexOptions.IgnoreCase);
            if (m.Success) { reason = "title prefix"; return WorkItemTypes.Parse(m.Groups[1].Value); }
            foreach (var l in item.Labels)
            {
                var ll = l.ToLowerInvariant();
                if (ll == "epic" || ll == "kind::epic") { reason = "label '" + l + "'"; return WorkItemType.Epic; }
                if (ll == "feature" || ll == "kind::feature" || ll == "enhancement") { reason = "label '" + l + "'"; return WorkItemType.Feature; }
                if (ll == "bug" || ll == "kind::bug" || ll == "defect") { reason = "label '" + l + "'"; return WorkItemType.Bug; }
                if (ll == "task" || ll == "kind::task" || ll == "chore") { reason = "label '" + l + "'"; return WorkItemType.Task; }
            }
            var body = Descriptions.Narrative(item.Body);
            if (BodyChecks.HasUserNeed(body) || BodyChecks.HasAcceptanceCriteria(body)) { reason = "user need / acceptance criteria in the body"; return WorkItemType.Story; }
            if (BodyChecks.HasSuccessPicture(body) || Regex.IsMatch(title, @"(?i)\b(programme|program|initiative)\b")) { reason = "success picture / initiative wording"; return WorkItemType.Epic; }
            if (BodyChecks.HasDoneWhen(body) || Regex.IsMatch(title, @"(?i)\b(capability|platform|service|pipeline|framework)\b")) { reason = "done-when / capability wording"; return WorkItemType.Feature; }
            if (Regex.IsMatch(title, @"(?i)^(fix|investigate|update|upgrade|rename|remove|add|configure|document)\b") && body.Length < 400) { reason = "short imperative ticket"; return WorkItemType.Task; }
            reason = "default for an unstructured ticket";
            return WorkItemType.Story;
        }

        /// <summary>
        /// A raw ticket: anything outside the sandbox, or a sandbox issue that carries no cascade-meta and no Halyard
        /// labels (the single-repository setup, where people file tickets straight into the sandbox).
        /// </summary>
        public static bool IsRawTicket(WorkItem item, RepositoryRef? sandbox)
        {
            if (sandbox == null || !item.Repository.Equals(sandbox)) return true;
            return !item.HasMeta && !item.HasLabel(Labels.Managed) && !item.HasLabel(Labels.Synthetic) && !item.HasLabel(Label);
        }

        /// <summary>Prepares drafts for the selected source items; already-copied items are marked, not duplicated.</summary>
        public static List<IntakeDraft> Prepare(IReadOnlyList<WorkItem> snapshot, RepositoryRef sandbox, IntakeOptions options, Taxonomy taxonomy)
        {
            var drafts = new List<IntakeDraft>();
            var sandboxItems = snapshot.Where(i => i.Repository.Equals(sandbox)).ToList();
            var existingBySource = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sandboxItems)
            {
                if (s.Meta == null || string.IsNullOrWhiteSpace(s.Meta.SourceProject) || string.IsNullOrWhiteSpace(s.Meta.SourceRef)) continue;
                existingBySource[s.Meta.SourceProject + "#" + s.Meta.SourceRef] = s;
            }
            var usedIds = new HashSet<string>(sandboxItems.Select(i => i.CanonicalId), StringComparer.OrdinalIgnoreCase);
            var themes = sandboxItems.Where(i => i.Type == WorkItemType.Theme).ToList();
            foreach (var item in snapshot.Where(i => options.Keys.Contains(i.Key)))
            {
                if (!IsRawTicket(item, sandbox)) continue; // already structured work, not a source ticket
                string reason;
                var type = GuessType(item, out reason);
                var title = Regex.Replace(Descriptions.ShortName(item.Title), @"^(?i)(theme|epic|feature|story|task|bug)\s*[:\-–]\s*", string.Empty).Trim();
                if (title.Length == 0) title = Descriptions.ShortName(item.Title);
                var id = Descriptions.MakeId(type, title);
                var candidate = id; var n = 2;
                while (usedIds.Contains(candidate)) candidate = id + "-" + n++;
                usedIds.Add(candidate);
                var draft = new IntakeDraft
                {
                    SourceKey = item.Key, SourceRepository = item.Repository.Identifier, SourceNumber = item.Number, SourceUrl = item.Url, SourceTitle = item.Title,
                    Type = type, CanonicalId = candidate, Title = Descriptions.FormatTitle(type, title), Reason = "type " + WorkItemTypes.ToToken(type) + ": " + reason,
                    Narrative = Descriptions.Narrative(item.Body),
                    Area = options.Area ?? item.LabelValue(Labels.AreaPrefix),
                    Confidence = reason.StartsWith("declared") || reason.StartsWith("type::") ? "high" : reason.StartsWith("default") ? "low" : "medium",
                };
                var theme = item.ThemeId ?? item.LabelValue(Labels.ThemePrefix);
                if (theme != null && themes.Any(t => string.Equals(t.CanonicalId, theme, StringComparison.OrdinalIgnoreCase))) draft.Theme = theme;
                else if (theme == null && themes.Count == 1 && type != WorkItemType.Theme) { draft.Theme = themes[0].CanonicalId; draft.Reason += "; only one theme in the sandbox"; }
                WorkItem? existing;
                if (existingBySource.TryGetValue(item.Repository.Identifier + "#" + item.Number, out existing)) draft.ExistingKey = existing.Key;
                var meta = BuildMeta(draft, item);
                draft.Body = Descriptions.Compose(meta, draft.Narrative.Length == 0 ? "## Notes\n_(no description on the source ticket)_" : draft.Narrative);
                draft.Labels = BuildLabels(draft, item, options);
                draft.Gaps = Deficiencies.Evaluate(type, meta, draft.Body).ToList();
                drafts.Add(draft);
            }
            return drafts;
        }

        private static HalyardMeta BuildMeta(IntakeDraft d, WorkItem source)
        {
            var meta = new HalyardMeta();
            meta.Set("type", WorkItemTypes.ToToken(d.Type)).Set("id", d.CanonicalId);
            if (d.Theme != null) meta.Set("theme", d.Theme);
            if (d.Parent != null) meta.Set("parent", d.Parent);
            meta.Set("state", "open");
            if (d.Area != null) meta.Set("area", d.Area);
            switch (d.Type)
            {
                case WorkItemType.Story: meta.Set("points", source.Meta != null && Sizing.IsValidPoints(source.Meta.Points) ? source.Meta.Points : Sizing.Tbd).Set("sprint", Sizing.Tbd); break;
                case WorkItemType.Feature: meta.Set("effort", source.Meta != null && Sizing.IsValidEffort(source.Meta.Effort) ? source.Meta.Effort : Sizing.Tbd); break;
                case WorkItemType.Theme: meta.Set("status", "active"); break;
            }
            meta.Set("source_project", source.Repository.Identifier).Set("source_iid", source.Number.ToString());
            if (!string.IsNullOrEmpty(source.Url)) meta.Set("source_url", source.Url);
            meta.Set("intake", "cascade");
            return meta;
        }

        private static List<string> BuildLabels(IntakeDraft d, WorkItem source, IntakeOptions options)
        {
            var labels = new List<string> { Labels.Managed, Label, WorkItemTypes.ToLabel(d.Type) };
            if (d.Theme != null) labels.Add(Labels.ThemePrefix + d.Theme);
            if (d.Area != null) labels.Add(Labels.AreaPrefix + d.Area);
            if (options.AddReviewLabel) labels.Add(Labels.ReviewQueue);
            foreach (var l in source.Labels) if (l.StartsWith("team::", StringComparison.OrdinalIgnoreCase) || l.StartsWith("blocked", StringComparison.OrdinalIgnoreCase)) labels.Add(l);
            return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string SystemPrompt(IReadOnlyList<WorkItem> themes)
        {
            var sb = new StringBuilder();
            sb.Append("You are Halyard, triaging raw backlog tickets into a THEME > EPIC > FEATURE > STORY > TASK hierarchy. For each ticket decide its level: ");
            sb.Append("THEME = a long-lived lane of work; EPIC = an outcome over several sprints; FEATURE = a capability delivered in one to three sprints; STORY = one sprint-sized increment with a user need; TASK = a small technical step; BUG = a defect. ");
            if (themes.Count > 0) sb.Append("Existing themes: ").Append(string.Join("; ", themes.Select(t => t.CanonicalId + " = " + Descriptions.ShortName(t.Title)))).Append(". Pick the best theme id for each ticket, or null. ");
            sb.Append("Return ONLY a JSON array: [{\"id\": \"<ticket id as given>\", \"type\": \"STORY\", \"theme\": \"THEME-… or null\", \"title\": \"<a clean short title, no prefix>\", \"confidence\": \"low|medium|high\", \"reason\": \"one sentence\"}].");
            return sb.ToString();
        }

        /// <summary>Lets the model override type, theme and title on the deterministic drafts; everything else stays as prepared.</summary>
        public static async Task<List<IntakeDraft>> ClassifyAsync(List<IntakeDraft> drafts, IReadOnlyList<WorkItem> snapshot, RepositoryRef sandbox, ILlmClient llm, IntakeOptions options, Taxonomy taxonomy, CancellationToken cancellation)
        {
            var themes = snapshot.Where(i => i.Repository.Equals(sandbox) && i.Type == WorkItemType.Theme).ToList();
            var byKey = snapshot.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            for (var offset = 0; offset < drafts.Count; offset += 12)
            {
                cancellation.ThrowIfCancellationRequested();
                var slice = drafts.Skip(offset).Take(12).ToList();
                var sb = new StringBuilder();
                foreach (var d in slice)
                {
                    sb.Append("### id: ").Append(d.SourceKey).Append('\n').Append("Title: ").Append(d.SourceTitle).Append('\n');
                    var n = d.Narrative.Length > 1200 ? d.Narrative.Substring(0, 1200) + "…" : d.Narrative;
                    sb.Append(n).Append("\n\n");
                }
                sb.Append("Respond with the JSON array only.");
                LlmResponse response;
                try { response = await llm.ChatAsync(new[] { LlmMessage.System(SystemPrompt(themes)), LlmMessage.User(sb.ToString()) }, cancellation).ConfigureAwait(false); }
                catch (ProviderHttpException ex) { foreach (var d in slice) d.Reason += "; model failed: " + ex.Message; break; }
                JArray? rows = null;
                try { var t = response.Text; var f = Regex.Match(t, @"```(?:json)?\s*([\s\S]*?)```"); if (f.Success) t = f.Groups[1].Value; var s0 = t.IndexOf('['); var e0 = t.LastIndexOf(']'); if (s0 >= 0 && e0 > s0) rows = JArray.Parse(t.Substring(s0, e0 - s0 + 1)); } catch (JsonException) { }
                if (rows == null) { foreach (var d in slice) d.Reason += "; model reply unparsed, deterministic kept"; continue; }
                foreach (var row in rows.OfType<JObject>())
                {
                    var d = slice.FirstOrDefault(x => string.Equals(x.SourceKey, (string?)row["id"], StringComparison.OrdinalIgnoreCase));
                    if (d == null) continue;
                    var type = WorkItemTypes.Parse((string?)row["type"]);
                    var theme = (string?)row["theme"];
                    var title = ((string?)row["title"] ?? string.Empty).Trim();
                    if (type != WorkItemType.Unclassified) d.Type = type;
                    if (!string.IsNullOrWhiteSpace(theme) && themes.Any(t => string.Equals(t.CanonicalId, theme, StringComparison.OrdinalIgnoreCase))) d.Theme = theme;
                    if (title.Length > 3 && title.Length < 140) d.Title = Descriptions.FormatTitle(d.Type, title); else d.Title = Descriptions.FormatTitle(d.Type, Descriptions.ShortName(d.SourceTitle));
                    d.CanonicalId = Descriptions.MakeId(d.Type, Descriptions.ShortName(d.Title));
                    d.Confidence = ((string?)row["confidence"] ?? "medium").Trim().ToLowerInvariant();
                    if (d.Confidence != "high" && d.Confidence != "low") d.Confidence = "medium";
                    d.Reason = "model: " + ((string?)row["reason"] ?? string.Empty).Trim();
                    WorkItem? source;
                    if (byKey.TryGetValue(d.SourceKey, out source) && source != null)
                    {
                        var meta = BuildMeta(d, source);
                        d.Body = Descriptions.Compose(meta, d.Narrative.Length == 0 ? "## Notes\n_(no description on the source ticket)_" : d.Narrative);
                        d.Labels = BuildLabels(d, source, options);
                        d.Gaps = Deficiencies.Evaluate(d.Type, meta, d.Body).ToList();
                    }
                }
            }
            // Re-check id uniqueness after the model may have changed titles.
            var used = new HashSet<string>(snapshot.Where(i => i.Repository.Equals(sandbox)).Select(i => i.CanonicalId), StringComparer.OrdinalIgnoreCase);
            foreach (var d in drafts)
            {
                var id = d.CanonicalId; var n = 2;
                while (!used.Add(d.CanonicalId)) { d.CanonicalId = id + "-" + n++; }
            }
            return drafts;
        }

        public static async Task<IntakeManifest> RunAsync(List<IntakeDraft> drafts, IWorkItemProvider provider, WriteGateway gateway, RepositoryRef sandbox, IntakeOptions options, IProgress<string>? progress, CancellationToken cancellation)
        {
            var manifest = new IntakeManifest { Target = sandbox.Key, Requested = drafts.Count };
            var todo = drafts.Where(d => d.ExistingKey == null).ToList();
            foreach (var d in drafts.Where(d => d.ExistingKey != null)) manifest.Skipped.Add(d.SourceKey + ": already in the sandbox as " + d.ExistingKey);
            if (todo.Count == 0) { manifest.FinishedAt = DateTimeOffset.UtcNow; return manifest; }
            var writer = await gateway.OpenVerifiedAsync(provider, sandbox, new WriteIntent("intake", "Copy " + todo.Count + " source items into the sandbox", manifest.Id), cancellation).ConfigureAwait(false);
            var labels = new List<LabelDefinition>(StandardLabels);
            foreach (var t in todo.Select(d => d.Type).Distinct()) labels.Add(new LabelDefinition(WorkItemTypes.ToLabel(t), "6366f1", "Halyard level"));
            foreach (var theme in todo.Where(d => d.Theme != null).Select(d => d.Theme!).Distinct(StringComparer.OrdinalIgnoreCase)) labels.Add(new LabelDefinition(Labels.ThemePrefix + theme, "0891b2", "Theme lane"));
            foreach (var label in labels)
            {
                cancellation.ThrowIfCancellationRequested();
                try { await writer.EnsureLabelAsync(label, cancellation).ConfigureAwait(false); }
                catch (ProviderHttpException ex) { manifest.Errors.Add("label " + label.Name + ": " + ex.Message); }
            }
            var done = 0;
            foreach (var d in todo)
            {
                cancellation.ThrowIfCancellationRequested();
                if (progress != null) progress.Report("Creating " + (++done) + "/" + todo.Count + ": " + d.Title);
                try
                {
                    var created = await writer.CreateAsync(new WorkItemDraft { Title = d.Title, Body = d.Body, Labels = d.Labels }, cancellation).ConfigureAwait(false);
                    manifest.Items.Add(new IntakeRecord { SourceKey = d.SourceKey, CanonicalId = d.CanonicalId, Type = WorkItemTypes.ToToken(d.Type), Number = created.Number, Key = created.Key, Url = created.Url });
                }
                catch (ProviderHttpException ex)
                {
                    manifest.Errors.Add(d.SourceKey + ": " + ex.Message);
                    if (ex.Status == 401 || ex.Status == 403) break;
                }
            }
            manifest.FinishedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(options.ManifestDirectory))
            {
                Directory.CreateDirectory(options.ManifestDirectory!);
                File.WriteAllText(Path.Combine(options.ManifestDirectory!, "intake-" + manifest.Id + ".json"), JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
            }
            return manifest;
        }

        public static List<IntakeManifest> ReadManifests(string directory)
        {
            var list = new List<IntakeManifest>();
            if (!Directory.Exists(directory)) return list;
            foreach (var f in Directory.GetFiles(directory, "intake-*.json"))
            {
                try { var m = JsonConvert.DeserializeObject<IntakeManifest>(File.ReadAllText(f)); if (m != null) list.Add(m); } catch (JsonException) { }
            }
            return list.OrderByDescending(m => m.StartedAt).ToList();
        }
    }
}
