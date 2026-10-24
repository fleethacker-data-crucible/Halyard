using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cascade.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cascade.Core.Snapshots
{
    /// <summary>A point-in-time copy of a repository's work items. The analysis layer runs on snapshots, never on live calls.</summary>
    public sealed class Snapshot
    {
        public string Id { get; set; } = string.Empty;
        /// <summary>Display form of the repositories in this snapshot: one identifier, or several joined with ", ".</summary>
        public string Repository { get; set; } = string.Empty;
        /// <summary>Every repository the items came from.</summary>
        public List<string> Repositories { get; set; } = new List<string>();
        public ProviderKind Provider { get; set; }
        public DateTimeOffset TakenAt { get; set; }
        public string? LabelFilter { get; set; }
        public List<WorkItem> Items { get; set; } = new List<WorkItem>();
        private int _itemCount;
        /// <summary>Item count, also available when items were not loaded (listing view).</summary>
        public int ItemCount { get { return Items.Count > 0 ? Items.Count : _itemCount; } set { _itemCount = value; } }

        public TimeSpan Age { get { return DateTimeOffset.UtcNow - TakenAt; } }
    }

    /// <summary>JSON persistence under output/snapshots; the newest file is the current snapshot.</summary>
    public sealed class SnapshotStore
    {
        private readonly string _directory;
        private readonly object _lock = new object();
        private Snapshot? _current;

        public SnapshotStore(string directory)
        {
            _directory = directory;
        }

        public Snapshot? Current
        {
            get
            {
                lock (_lock)
                {
                    if (_current == null) _current = LoadLatest();
                    return _current;
                }
            }
        }

        public Snapshot Save(RepositoryRef repository, IEnumerable<WorkItem> items, string? labelFilter)
        {
            return Save(new[] { repository }, items, labelFilter);
        }

        /// <summary>One snapshot across several repositories of the same provider (a backlog that spans repos).</summary>
        public Snapshot Save(IReadOnlyList<RepositoryRef> repositories, IEnumerable<WorkItem> items, string? labelFilter)
        {
            if (repositories == null || repositories.Count == 0) throw new ArgumentException("At least one repository is required.", "repositories");
            var snap = new Snapshot
            {
                Id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4),
                Repository = string.Join(", ", repositories.Select(r => r.Identifier)),
                Repositories = repositories.Select(r => r.Identifier).ToList(),
                Provider = repositories[0].Provider,
                TakenAt = DateTimeOffset.UtcNow,
                LabelFilter = labelFilter,
                Items = items.ToList(),
            };
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(Path.Combine(_directory, "snapshot-" + snap.Id + ".json"), Serialize(snap), new UTF8Encoding(false));
                _current = snap;
                Prune(10);
            }
            return snap;
        }

        public List<Snapshot> List()
        {
            lock (_lock)
            {
                var list = new List<Snapshot>();
                if (!Directory.Exists(_directory)) return list;
                foreach (var f in Directory.GetFiles(_directory, "snapshot-*.json"))
                {
                    try { list.Add(Deserialize(File.ReadAllText(f), false)); }
                    catch (JsonException) { }
                }
                return list.OrderByDescending(s => s.TakenAt).ToList();
            }
        }

        public Snapshot? Load(string id)
        {
            var path = Path.Combine(_directory, "snapshot-" + id + ".json");
            if (!File.Exists(path)) return null;
            lock (_lock) return Deserialize(File.ReadAllText(path), true);
        }

        private Snapshot? LoadLatest()
        {
            if (!Directory.Exists(_directory)) return null;
            var latest = Directory.GetFiles(_directory, "snapshot-*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (latest == null) return null;
            try { return Deserialize(File.ReadAllText(latest), true); }
            catch (JsonException) { return null; }
        }

        private void Prune(int keep)
        {
            foreach (var f in Directory.GetFiles(_directory, "snapshot-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }

        public static string Serialize(Snapshot snap)
        {
            var root = new JObject
            {
                ["id"] = snap.Id,
                ["repository"] = snap.Repository,
                ["repositories"] = new JArray(snap.Repositories.Count == 0 ? new[] { snap.Repository } : snap.Repositories.ToArray()),
                ["provider"] = snap.Provider.ToString().ToLowerInvariant(),
                ["takenAt"] = snap.TakenAt.ToString("o"),
                ["labelFilter"] = snap.LabelFilter,
                ["count"] = snap.Items.Count,
                ["items"] = new JArray(snap.Items.Select(ToJson)),
            };
            return root.ToString(Formatting.None);
        }

        public static JObject ToJson(WorkItem w)
        {
            return new JObject
            {
                ["repository"] = w.Repository.Identifier,
                ["number"] = w.Number,
                ["providerId"] = w.ProviderId,
                ["providerNumericId"] = w.ProviderNumericId,
                ["title"] = w.Title,
                ["body"] = w.Body,
                ["labels"] = new JArray(w.Labels),
                ["assignees"] = new JArray(w.Assignees),
                ["state"] = w.State == WorkItemState.Closed ? "closed" : "open",
                ["url"] = w.Url,
                ["milestone"] = w.Milestone,
                ["createdAt"] = w.CreatedAt == null ? null : w.CreatedAt.Value.ToString("o"),
                ["updatedAt"] = w.UpdatedAt == null ? null : w.UpdatedAt.Value.ToString("o"),
            };
        }

        public static Snapshot Deserialize(string json, bool includeItems)
        {
            var root = JObject.Parse(json);
            var provider = string.Equals((string?)root["provider"], "gitlab", StringComparison.OrdinalIgnoreCase) ? ProviderKind.GitLab : ProviderKind.GitHub;
            var snap = new Snapshot
            {
                Id = (string?)root["id"] ?? string.Empty,
                Repository = (string?)root["repository"] ?? string.Empty,
                Provider = provider,
                TakenAt = DateTimeOffset.Parse((string?)root["takenAt"] ?? DateTimeOffset.UtcNow.ToString("o")),
                LabelFilter = (string?)root["labelFilter"],
            };
            snap.Repositories = (root["repositories"] as JArray ?? new JArray()).Select(r => (string?)r ?? string.Empty).Where(r => r.Length > 0).ToList();
            if (snap.Repositories.Count == 0 && snap.Repository.Length > 0) snap.Repositories.Add(snap.Repository);
            snap.ItemCount = (int?)root["count"] ?? 0;
            if (!includeItems) return snap;
            var repos = new Dictionary<string, RepositoryRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in root["items"] as JArray ?? new JArray())
            {
                var repoId = (string?)j["repository"] ?? snap.Repositories.FirstOrDefault() ?? snap.Repository;
                RepositoryRef? repo;
                if (!repos.TryGetValue(repoId, out repo) || repo == null) { repo = new RepositoryRef(provider, repoId); repos[repoId] = repo; }
                var w = new WorkItem(repo, (long?)j["number"] ?? 0)
                {
                    ProviderId = (string?)j["providerId"],
                    ProviderNumericId = (long?)j["providerNumericId"],
                    Title = (string?)j["title"] ?? string.Empty,
                    Body = (string?)j["body"] ?? string.Empty,
                    State = string.Equals((string?)j["state"], "closed", StringComparison.OrdinalIgnoreCase) ? WorkItemState.Closed : WorkItemState.Open,
                    Url = (string?)j["url"],
                    Milestone = (string?)j["milestone"],
                };
                DateTimeOffset d;
                if (DateTimeOffset.TryParse((string?)j["createdAt"], out d)) w.CreatedAt = d;
                if (DateTimeOffset.TryParse((string?)j["updatedAt"], out d)) w.UpdatedAt = d;
                w.Labels.AddRange((j["labels"] as JArray ?? new JArray()).Select(l => (string?)l ?? string.Empty).Where(l => l.Length > 0));
                w.Assignees.AddRange((j["assignees"] as JArray ?? new JArray()).Select(l => (string?)l ?? string.Empty).Where(l => l.Length > 0));
                snap.Items.Add(w);
            }
            return snap;
        }
    }
}
