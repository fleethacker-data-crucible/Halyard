using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cascade.Core.Model
{
    public enum WorkItemState
    {
        Open = 1,
        Closed = 2,
    }

    /// <summary>
    /// A provider-agnostic issue. Everything the analysis layer needs is here; nothing
    /// provider-specific leaks past the adapter except the opaque <see cref="ProviderId"/>
    /// (GitHub node id, GitLab global id) that write operations may need.
    /// </summary>
    public sealed class WorkItem
    {
        private static readonly Regex TypeLabelRegex = new Regex(@"^type::(theme|epic|feature|story|task|bug)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public WorkItem(RepositoryRef repository, long number)
        {
            Repository = repository ?? throw new ArgumentNullException("repository");
            Number = number;
            Labels = new List<string>();
            Assignees = new List<string>();
        }

        public RepositoryRef Repository { get; private set; }
        public long Number { get; private set; }
        /// <summary>Opaque provider id (GitHub node id, GitLab global id). Needed for some write operations.</summary>
        public string? ProviderId { get; set; }
        /// <summary>Provider's numeric database id when distinct from <see cref="Number"/> (GitHub issue id).</summary>
        public long? ProviderNumericId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<string> Labels { get; private set; }
        public List<string> Assignees { get; private set; }
        public WorkItemState State { get; set; } = WorkItemState.Open;
        public string? Url { get; set; }
        public string? Milestone { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }

        /// <summary><c>github:owner/repo:12</c> or <c>gitlab:4242:271</c>. Unique across providers.</summary>
        public string Key { get { return Repository.Key + ":" + Number; } }

        private CascadeMeta? _meta;
        private bool _metaParsed;

        /// <summary>Parsed cascade-meta block, or null when the body has none. Cached per body text.</summary>
        public CascadeMeta? Meta
        {
            get
            {
                if (!_metaParsed)
                {
                    _meta = CascadeMeta.Parse(Body);
                    _metaParsed = true;
                }
                return _meta;
            }
        }

        /// <summary>Call after mutating <see cref="Body"/> so <see cref="Meta"/> re-parses.</summary>
        public void InvalidateMeta()
        {
            _metaParsed = false;
            _meta = null;
        }

        /// <summary>Type labels present (<c>type::story</c> → Story). More than one is a finding.</summary>
        public IReadOnlyList<WorkItemType> TypeLabels
        {
            get
            {
                var list = new List<WorkItemType>();
                foreach (var label in Labels)
                {
                    var m = TypeLabelRegex.Match(label ?? string.Empty);
                    if (!m.Success) continue;
                    var t = WorkItemTypes.Parse(m.Groups[1].Value);
                    if (!list.Contains(t)) list.Add(t);
                }
                return list;
            }
        }

        /// <summary>Declared type: cascade-meta wins, then a single unambiguous type:: label, else Unclassified.</summary>
        public WorkItemType Type
        {
            get
            {
                var meta = Meta;
                if (meta != null && meta.Type != WorkItemType.Unclassified) return meta.Type;
                var labels = TypeLabels;
                return labels.Count == 1 ? labels[0] : WorkItemType.Unclassified;
            }
        }

        /// <summary>The meta <c>type</c> as written, even when unknown (for UNKNOWN_DECLARED_TYPE findings).</summary>
        public string? DeclaredTypeText { get { return Meta == null ? null : Meta.Get("type"); } }

        /// <summary>Stable id from cascade-meta, or the provider key when none is declared.</summary>
        public string CanonicalId
        {
            get
            {
                var id = ExplicitId;
                return string.IsNullOrWhiteSpace(id) ? Key : id!;
            }
        }

        public string? ExplicitId { get { return Meta == null ? null : Meta.Id; } }
        public string? ParentId { get { return Meta == null ? null : Meta.Parent; } }
        public string? ThemeId { get { return Meta == null ? null : Meta.Theme; } }
        public bool HasMeta { get { return Meta != null; } }

        public bool HasLabel(string label)
        {
            return Labels.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Value after <c>prefix::</c> for scoped labels, e.g. <c>theme::THEME-x</c> → <c>THEME-x</c>.</summary>
        public string? LabelValue(string prefix)
        {
            var p = prefix.TrimEnd(':') + "::";
            foreach (var l in Labels)
            {
                if (l != null && l.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return l.Substring(p.Length).Trim();
            }
            return null;
        }

        public override string ToString()
        {
            return Key + " " + Title;
        }
    }

    /// <summary>Fields needed to create a work item. Provider adapters translate this to their payloads.</summary>
    public sealed class WorkItemDraft
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new List<string>();
        public string? Milestone { get; set; }
    }

    /// <summary>Partial update; null means "leave unchanged".</summary>
    public sealed class WorkItemPatch
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<string>? Labels { get; set; }
        public WorkItemState? State { get; set; }

        public bool IsEmpty { get { return Title == null && Body == null && Labels == null && State == null; } }
    }

    public sealed class WorkItemQuery
    {
        public WorkItemState? State { get; set; }
        /// <summary>All of these labels must be present (provider AND semantics).</summary>
        public List<string> Labels { get; set; } = new List<string>();
        public int MaxItems { get; set; } = 2000;
        public DateTimeOffset? UpdatedAfter { get; set; }
    }
}
