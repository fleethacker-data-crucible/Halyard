using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Serialization;

namespace Halyard.Core.Synthetic
{
    public sealed class SeededItem
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public long Number { get; set; }
        public string Key { get; set; } = string.Empty;
        public string? Url { get; set; }
        public bool NativeLinked { get; set; }
        /// <summary>Injected defect kinds; empty for a complete item.</summary>
        public List<string> Defects { get; set; } = new List<string>();
        /// <summary>Audit codes expected for this item, so a run can be verified end to end.</summary>
        public List<string> ExpectedCodes { get; set; } = new List<string>();
    }

    public sealed class SeedManifest
    {
        public string RunId { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        /// <summary>Set when the run's items were deleted by a teardown; the run then leaves the Sandbox list.</summary>
        public DateTimeOffset? DeletedAt { get; set; }
        public int Requested { get; set; }
        public List<SeededItem> Items { get; set; } = new List<SeededItem>();
        public List<string> Errors { get; set; } = new List<string>();
        public bool Completed { get { return FinishedAt != null && Errors.Count == 0 && Items.Count == Requested; } }
        public int NeedsReview { get { return Items.Count(i => i.Defects.Count > 0); } }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }

        public static SeedManifest FromJson(string json)
        {
            return JsonConvert.DeserializeObject<SeedManifest>(json) ?? new SeedManifest();
        }
    }

    public sealed class SeedOptions
    {
        public string RunId { get; set; } = NewRunId();
        /// <summary>Attempt native parent links (GitHub sub-issues). Best-effort; never fails the seed.</summary>
        public bool NativeLinks { get; set; } = true;
        /// <summary>Adds the review-queue label to items seeded with a deliberate gap, so the human review queue is visible on the provider too.</summary>
        public bool AddReviewLabel { get; set; } = true;
        /// <summary>Directory where the manifest is written; null skips the file.</summary>
        public string? ManifestDirectory { get; set; }

        public static string NewRunId()
        {
            return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        }
    }

    public sealed class SeedProgress
    {
        public int Done { get; set; }
        public int Total { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Creates a synthetic catalogue in the sandbox through the write gateway: labels first, then items in
    /// parent-before-child order so every <c>parent:</c> id already exists. Every item carries the managed,
    /// synthetic and run labels, which is what makes teardown precise.
    /// </summary>
    public static class SandboxSeeder
    {
        public static readonly LabelDefinition[] StandardLabels =
        {
            new LabelDefinition(Labels.Managed, "1d4ed8", "Created or managed by Halyard"),
            new LabelDefinition(Labels.Synthetic, "7c3aed", "Synthetic work item; safe to delete"),
            new LabelDefinition(Labels.ReviewQueue, "f59e0b", "Awaiting human structure review"),
            new LabelDefinition("type::theme", "0f766e", "Halyard work-item type"),
            new LabelDefinition("type::epic", "0e7490", "Halyard work-item type"),
            new LabelDefinition("type::feature", "2563eb", "Halyard work-item type"),
            new LabelDefinition("type::story", "16a34a", "Halyard work-item type"),
            new LabelDefinition("type::task", "64748b", "Halyard work-item type"),
            new LabelDefinition("type::bug", "dc2626", "Halyard work-item type"),
        };

        public static async Task<SeedManifest> SeedAsync(IWorkItemProvider provider, WriteGateway gateway, RepositoryRef target,
            IReadOnlyList<SyntheticWorkItem> catalog, SeedOptions options, IProgress<SeedProgress>? progress, CancellationToken cancellation)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (gateway == null) throw new ArgumentNullException("gateway");
            if (catalog == null) throw new ArgumentNullException("catalog");
            options = options ?? new SeedOptions();
            var intent = new WriteIntent("seed-synthetic", "Seed synthetic hierarchy run " + options.RunId, options.RunId);
            var writer = await gateway.OpenVerifiedAsync(provider, target, intent, cancellation).ConfigureAwait(false);
            var manifest = new SeedManifest { RunId = options.RunId, Target = target.Key, StartedAt = DateTimeOffset.UtcNow, Requested = catalog.Count };
            var ordered = TopologicalOrder(catalog);
            var total = ordered.Count;

            Report(progress, 0, total, "Ensuring labels on " + target.Key);
            var labels = new List<LabelDefinition>(StandardLabels) { new LabelDefinition(Labels.Run(options.RunId), "a21caf", "Synthetic seed run " + options.RunId) };
            foreach (var theme in catalog.Where(c => c.Type == WorkItemType.Theme))
                labels.Add(new LabelDefinition(Labels.ThemePrefix + theme.Id, "0891b2", "Theme lane"));
            foreach (var area in catalog.Select(c => c.Meta.Area).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
                labels.Add(new LabelDefinition(Labels.AreaPrefix + area, "475569", "Product area"));
            foreach (var label in labels)
            {
                cancellation.ThrowIfCancellationRequested();
                try { await writer.EnsureLabelAsync(label, cancellation).ConfigureAwait(false); }
                catch (ProviderHttpException ex) { manifest.Errors.Add("label " + label.Name + ": " + ex.Message); }
            }

            var created = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            foreach (var entry in ordered)
            {
                cancellation.ThrowIfCancellationRequested();
                var meta = entry.Meta.Clone();
                var hasMeta = meta.Count > 0; // a "no metadata" defect ships the narrative alone, as a hand-written ticket would
                if (hasMeta)
                {
                    meta.Set("source", "halyard-synthetic");
                    meta.Set("run_id", options.RunId);
                }
                WorkItem? parent = null;
                if (!string.IsNullOrEmpty(entry.ParentId) && created.TryGetValue(entry.ParentId!, out parent) && hasMeta)
                    meta.Set("parent_ref", "#" + parent.Number);
                var labelsForItem = new List<string>(entry.Labels);
                labelsForItem.Add(Labels.Run(options.RunId));
                if (options.AddReviewLabel && entry.NeedsHumanReview && !labelsForItem.Contains(Labels.ReviewQueue)) labelsForItem.Add(Labels.ReviewQueue);
                var draft = new WorkItemDraft
                {
                    Title = entry.Title,
                    Body = hasMeta ? Schema.Descriptions.Compose(meta, entry.Narrative) : entry.Narrative,
                    Labels = labelsForItem.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
                try
                {
                    var item = await writer.CreateAsync(draft, cancellation).ConfigureAwait(false);
                    if (entry.Closed)
                    {
                        // Finished work from past sprints: closed on the provider so velocity and progress have history.
                        try { item = await writer.UpdateAsync(item, new WorkItemPatch { State = WorkItemState.Closed }, cancellation).ConfigureAwait(false); }
                        catch (ProviderHttpException ex) { manifest.Errors.Add(entry.Id + ": could not close: " + ex.Message); }
                    }
                    created[entry.Id] = item;
                    var seeded = new SeededItem { Id = entry.Id, Type = WorkItemTypes.ToToken(entry.Type), ParentId = entry.ParentId, Number = item.Number, Key = item.Key, Url = item.Url, Defects = entry.Defects.ToList(), ExpectedCodes = entry.ExpectedCodes.ToList() };
                    if (options.NativeLinks && parent != null && provider.Capabilities.NativeParentLinks)
                    {
                        try { seeded.NativeLinked = await writer.LinkParentAsync(item, parent, cancellation).ConfigureAwait(false); }
                        catch (ProviderHttpException) { seeded.NativeLinked = false; }
                    }
                    manifest.Items.Add(seeded);
                }
                catch (ProviderHttpException ex)
                {
                    manifest.Errors.Add(entry.Id + ": " + ex.Message);
                    if (ex.Status == 401 || ex.Status == 403) break; // auth problems will not fix themselves mid-run
                }
                done++;
                Report(progress, done, total, "Created " + entry.Title);
            }
            manifest.FinishedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(options.ManifestDirectory)) WriteManifest(options.ManifestDirectory!, manifest);
            return manifest;
        }

        /// <summary>Parents before children regardless of catalogue order; stable for equal depth.</summary>
        public static List<SyntheticWorkItem> TopologicalOrder(IReadOnlyList<SyntheticWorkItem> catalog)
        {
            var byId = catalog.ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Func<SyntheticWorkItem, int>? depthOf = null;
            depthOf = item =>
            {
                int d;
                if (depth.TryGetValue(item.Id, out d)) return d;
                SyntheticWorkItem? parent;
                d = string.IsNullOrEmpty(item.ParentId) || !byId.TryGetValue(item.ParentId!, out parent) || parent == null || parent == item ? 0 : depthOf!(parent) + 1;
                depth[item.Id] = d;
                return d;
            };
            return catalog.Select((c, i) => new { c, i, d = depthOf(c) }).OrderBy(x => x.d).ThenBy(x => x.i).Select(x => x.c).ToList();
        }

        public static string ManifestPath(string directory, string runId)
        {
            return Path.Combine(directory, "seed-" + runId + ".json");
        }

        public static void WriteManifest(string directory, SeedManifest manifest)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(ManifestPath(directory, manifest.RunId), manifest.ToJson(), new UTF8Encoding(false));
        }

        public static List<SeedManifest> ReadManifests(string directory, bool includeDeleted = false)
        {
            var list = new List<SeedManifest>();
            if (!Directory.Exists(directory)) return list;
            foreach (var file in Directory.GetFiles(directory, "seed-*.json"))
            {
                try { list.Add(SeedManifest.FromJson(File.ReadAllText(file))); }
                catch (JsonException) { }
            }
            return list.Where(m => includeDeleted || m.DeletedAt == null).OrderByDescending(m => m.StartedAt).ToList();
        }

        /// <summary>Marks seed manifests as deleted after a teardown: one run, or every run when <paramref name="runId"/> is null.</summary>
        public static int MarkDeleted(string directory, string? runId, DateTimeOffset when)
        {
            var marked = 0;
            if (!Directory.Exists(directory)) return 0;
            foreach (var file in Directory.GetFiles(directory, "seed-*.json"))
            {
                SeedManifest m;
                try { m = SeedManifest.FromJson(File.ReadAllText(file)); } catch (JsonException) { continue; }
                if (m.DeletedAt != null) continue;
                if (runId != null && !string.Equals(m.RunId, runId, StringComparison.OrdinalIgnoreCase)) continue;
                m.DeletedAt = when;
                File.WriteAllText(file, m.ToJson());
                marked++;
            }
            return marked;
        }

        private static void Report(IProgress<SeedProgress>? progress, int done, int total, string message)
        {
            if (progress != null) progress.Report(new SeedProgress { Done = done, Total = total, Message = message });
        }
    }
}
