using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    /// <summary>One write of an apply run: the body the item had before and the body Halyard wrote.</summary>
    public sealed class ApplyJournalEntry
    {
        public string BatchId { get; set; }
        public string ProposalId { get; set; }
        public ProposalKind Kind { get; set; }
        public string ItemKey { get; set; }
        public long ItemNumber { get; set; }
        public string ItemCanonicalId { get; set; }
        public string ItemTitle { get; set; }
        public string PreviousBody { get; set; }
        public string AppliedBody { get; set; }
        public DateTimeOffset AppliedAt { get; set; }
        public DateTimeOffset? RolledBackAt { get; set; }
        public string RollbackNote { get; set; }

        public ApplyJournalEntry()
        {
            BatchId = string.Empty;
            ProposalId = string.Empty;
            ItemKey = string.Empty;
            ItemCanonicalId = string.Empty;
            ItemTitle = string.Empty;
            PreviousBody = string.Empty;
            AppliedBody = string.Empty;
        }
    }

    /// <summary>
    /// Everything one apply (a single batch, or apply-all across batches) wrote. The previous body of every written
    /// item is kept here so the run can be undone later; nothing is ever read back from the provider history.
    /// </summary>
    public sealed class ApplyRun
    {
        public string Id { get; set; }
        public string Target { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public List<string> BatchIds { get; set; }
        public List<ApplyJournalEntry> Entries { get; set; }

        public ApplyRun()
        {
            Id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            Target = string.Empty;
            StartedAt = DateTimeOffset.UtcNow;
            BatchIds = new List<string>();
            Entries = new List<ApplyJournalEntry>();
        }

        public int Pending { get { return Entries.Count(e => e.RolledBackAt == null); } }
        public bool CanRollBack { get { return Pending > 0; } }
    }

    /// <summary>JSON-file persistence for apply runs under <c>output/applied</c>.</summary>
    public sealed class ApplyJournal
    {
        private readonly string _directory;
        private readonly object _lock = new object();

        public ApplyJournal(string directory)
        {
            _directory = directory;
        }

        public string Directory { get { return _directory; } }

        public void Save(ApplyRun run)
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.WriteAllText(PathFor(run.Id), JsonConvert.SerializeObject(run, Formatting.Indented), new UTF8Encoding(false));
            }
        }

        public ApplyRun Load(string runId)
        {
            var path = PathFor(runId);
            lock (_lock)
            {
                return File.Exists(path) ? JsonConvert.DeserializeObject<ApplyRun>(File.ReadAllText(path)) : null;
            }
        }

        /// <summary>Newest first.</summary>
        public List<ApplyRun> LoadAll()
        {
            var list = new List<ApplyRun>();
            if (!System.IO.Directory.Exists(_directory)) return list;
            foreach (var file in System.IO.Directory.GetFiles(_directory, "apply-*.json"))
            {
                try
                {
                    var run = JsonConvert.DeserializeObject<ApplyRun>(File.ReadAllText(file));
                    if (run != null) list.Add(run);
                }
                catch (JsonException) { }
            }
            return list.OrderByDescending(r => r.StartedAt).ToList();
        }

        /// <summary>The most recent run for <paramref name="target"/> that still has writes to undo.</summary>
        public ApplyRun Latest(string target)
        {
            return LoadAll().FirstOrDefault(r => string.Equals(r.Target, target, StringComparison.OrdinalIgnoreCase) && r.CanRollBack);
        }

        public void Delete(string runId)
        {
            lock (_lock)
            {
                var path = PathFor(runId);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private string PathFor(string runId)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) runId = runId.Replace(c, '_');
            return Path.Combine(_directory, "apply-" + runId + ".json");
        }
    }
}
