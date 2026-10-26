using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Halyard.Core.Model;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    public enum ProposalKind
    {
        StoryPoints = 1,
        FeatureEffort = 2,
        ParentLink = 3,
        /// <summary>A rewritten narrative (missing sections drafted) and optional meta changes.</summary>
        Refinement = 4,
        /// <summary>Metadata repair: a halyard-meta block built or completed from labels, title and placeholders.</summary>
        Metadata = 5,
        /// <summary>Skills needed for the item (taxonomy ids), for matching work to people.</summary>
        Skills = 6,
        /// <summary>Sprint placement and assignee for a story.</summary>
        SprintPlan = 7,
        /// <summary>Theme membership for an epic (from vision drafting).</summary>
        ThemeLink = 8,
    }

    public enum ProposalDecision
    {
        Pending = 0,
        Approved = 1,
        Rejected = 2,
        Applied = 3,
        Failed = 4,
    }

    /// <summary>
    /// A proposed change to one work item. Proposals are local files until a human approves them and an
    /// explicit apply run writes them to the sandbox. Nothing here touches a provider.
    /// </summary>
    public sealed class Proposal
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public string BatchId { get; set; } = string.Empty;
        public ProposalKind Kind { get; set; }
        public string ItemKey { get; set; } = string.Empty;
        public long ItemNumber { get; set; }
        public string ItemCanonicalId { get; set; } = string.Empty;
        public string ItemTitle { get; set; } = string.Empty;
        public string? CurrentValue { get; set; }
        public string ProposedValue { get; set; } = string.Empty;
        /// <summary>low | medium | high</summary>
        public string Confidence { get; set; } = "medium";
        public string Reason { get; set; } = string.Empty;
        /// <summary>deterministic | llm:&lt;profile&gt; | parsed:&lt;format&gt;</summary>
        public string Source { get; set; } = string.Empty;
        public ProposalDecision Decision { get; set; } = ProposalDecision.Pending;
        public string? DecisionNote { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? DecidedAt { get; set; }
        public DateTimeOffset? AppliedAt { get; set; }
        public string? Error { get; set; }
        /// <summary>Refinement / Metadata: meta keys to set (value null removes the key).</summary>
        public Dictionary<string, string?>? ProposedMeta { get; set; }
        /// <summary>Deficiency or finding codes this proposal is meant to resolve.</summary>
        public List<string> Addresses { get; set; } = new List<string>();
        /// <summary>Refinement: codes still open after the proposal is applied in memory (empty = fully resolves).</summary>
        public List<string> Remaining { get; set; } = new List<string>();

        /// <summary>Human-readable label for the kind.</summary>
        public static string KindToken(ProposalKind kind)
        {
            switch (kind)
            {
                case ProposalKind.StoryPoints: return "storyPoints";
                case ProposalKind.FeatureEffort: return "featureEffort";
                case ProposalKind.ParentLink: return "parentLink";
                case ProposalKind.Refinement: return "refinement";
                case ProposalKind.Skills: return "skills";
                case ProposalKind.SprintPlan: return "sprintPlan";
                case ProposalKind.ThemeLink: return "themeLink";
                default: return "metadata";
            }
        }

        public string MetaKey
        {
            get
            {
                switch (Kind)
                {
                    case ProposalKind.StoryPoints: return "points";
                    case ProposalKind.FeatureEffort: return "effort";
                    case ProposalKind.Skills: return "skills";
                    case ProposalKind.SprintPlan: return "sprint";
                    case ProposalKind.ThemeLink: return "theme";
                    default: return "parent";
                }
            }
        }
    }

    public sealed class ProposalBatch
    {
        public string Id { get; set; } = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        public ProposalKind Kind { get; set; }
        public string Target { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Source { get; set; } = string.Empty;
        public int Considered { get; set; }
        public int Skipped { get; set; }
        public List<string> SkipReasons { get; set; } = new List<string>();
        public List<Proposal> Proposals { get; set; } = new List<Proposal>();
        public string? RawModelOutput { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>JSON-file persistence for proposal batches under <c>output/proposals</c>.</summary>
    public sealed class ProposalStore
    {
        private readonly string _directory;
        private readonly object _lock = new object();

        public ProposalStore(string directory)
        {
            _directory = directory;
        }

        public string Directory { get { return _directory; } }

        public void Save(ProposalBatch batch)
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.WriteAllText(PathFor(batch.Id), JsonConvert.SerializeObject(batch, Formatting.Indented), new UTF8Encoding(false));
            }
        }

        public ProposalBatch? Load(string batchId)
        {
            var path = PathFor(batchId);
            if (!File.Exists(path)) return null;
            lock (_lock)
            {
                return JsonConvert.DeserializeObject<ProposalBatch>(File.ReadAllText(path));
            }
        }

        public List<ProposalBatch> LoadAll()
        {
            var list = new List<ProposalBatch>();
            if (!System.IO.Directory.Exists(_directory)) return list;
            foreach (var file in System.IO.Directory.GetFiles(_directory, "proposals-*.json"))
            {
                try
                {
                    var b = JsonConvert.DeserializeObject<ProposalBatch>(File.ReadAllText(file));
                    if (b != null) list.Add(b);
                }
                catch (JsonException) { }
            }
            return list.OrderByDescending(b => b.CreatedAt).ToList();
        }

        public Proposal? Decide(string batchId, string proposalId, ProposalDecision decision, string? note)
        {
            var batch = Load(batchId);
            if (batch == null) return null;
            var p = batch.Proposals.FirstOrDefault(x => x.Id == proposalId);
            if (p == null) return null;
            if (p.Decision == ProposalDecision.Applied) return p;
            p.Decision = decision;
            p.DecisionNote = note;
            p.DecidedAt = DateTimeOffset.UtcNow;
            Save(batch);
            return p;
        }

        public int DecideAll(string batchId, ProposalDecision decision, string? minimumConfidence)
        {
            var batch = Load(batchId);
            if (batch == null) return 0;
            var count = 0;
            foreach (var p in batch.Proposals.Where(x => x.Decision == ProposalDecision.Pending))
            {
                if (minimumConfidence != null && Rank(p.Confidence) < Rank(minimumConfidence)) continue;
                p.Decision = decision;
                p.DecidedAt = DateTimeOffset.UtcNow;
                count++;
            }
            Save(batch);
            return count;
        }

        private static int Rank(string confidence)
        {
            switch ((confidence ?? string.Empty).ToLowerInvariant())
            {
                case "high": return 3;
                case "medium": return 2;
                case "low": return 1;
                default: return 0;
            }
        }

        /// <summary>Removes a batch file. Returns false when there is no such batch.</summary>
        public bool Delete(string batchId)
        {
            lock (_lock)
            {
                var path = PathFor(batchId);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
        }

        private string PathFor(string batchId)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) batchId = batchId.Replace(c, '_');
            return Path.Combine(_directory, "proposals-" + batchId + ".json");
        }
    }
}
