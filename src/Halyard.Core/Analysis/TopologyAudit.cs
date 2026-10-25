using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Model;

namespace Halyard.Core.Analysis
{
    public enum Severity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    public sealed class Finding
    {
        public Finding(WorkItem item, string code, Severity severity, string explanation)
        {
            Key = item.Key;
            Number = item.Number;
            Title = item.Title;
            Url = item.Url;
            Type = item.Type;
            CanonicalId = item.CanonicalId;
            Code = code;
            Severity = severity;
            Explanation = explanation;
        }

        public string Key { get; private set; }
        public long Number { get; private set; }
        public string Title { get; private set; }
        public string? Url { get; private set; }
        public WorkItemType Type { get; private set; }
        public string CanonicalId { get; private set; }
        public string Code { get; private set; }
        public Severity Severity { get; private set; }
        public string Explanation { get; private set; }
    }

    public sealed class TopologyAuditResult
    {
        public TopologyAuditResult(IReadOnlyList<Finding> findings, IReadOnlyDictionary<string, string> parentEdges, int visibleCount, int sourceCount, int classifiedCount, string? labelFilter)
        {
            Findings = findings;
            ParentEdges = parentEdges;
            VisibleCount = visibleCount;
            SourceCount = sourceCount;
            ClassifiedCount = classifiedCount;
            LabelFilter = labelFilter;
        }

        public IReadOnlyList<Finding> Findings { get; private set; }
        /// <summary>child key → parent key for every resolved, in-scope edge.</summary>
        public IReadOnlyDictionary<string, string> ParentEdges { get; private set; }
        public int VisibleCount { get; private set; }
        public int SourceCount { get; private set; }
        public int ClassifiedCount { get; private set; }
        public string? LabelFilter { get; private set; }
        public int ErrorCount { get { return Findings.Count(f => f.Severity == Severity.Error); } }
        public int WarningCount { get { return Findings.Count(f => f.Severity == Severity.Warning); } }
        public int InfoCount { get { return Findings.Count(f => f.Severity == Severity.Info); } }
        public int AffectedCount { get { return Findings.Select(f => f.Key).Distinct().Count(); } }
    }

    /// <summary>
    /// Read-only topology diagnostics over explicit halyard-meta and type:: labels. Nothing is inferred:
    /// unknown types stay unknown, unresolved parents stay unresolved. When a parent exists in the
    /// source but outside the current label filter, only that fact is disclosed.
    /// </summary>
    public static class TopologyAudit
    {
        public const string UnclassifiedType = "UNCLASSIFIED_TYPE";
        public const string UnknownDeclaredType = "UNKNOWN_DECLARED_TYPE";
        public const string ConflictingTypeLabels = "CONFLICTING_TYPE_LABELS";
        public const string TypeMetadataLabelConflict = "TYPE_METADATA_LABEL_CONFLICT";
        public const string NoCanonicalId = "NO_CANONICAL_ID";
        public const string DuplicateCanonicalId = "DUPLICATE_CANONICAL_ID";
        public const string ParentNotDeclared = "PARENT_NOT_DECLARED";
        public const string SelfParent = "SELF_PARENT";
        public const string ParentUnresolved = "PARENT_UNRESOLVED";
        public const string AmbiguousParentId = "AMBIGUOUS_PARENT_ID";
        public const string ParentOutsideFilter = "PARENT_OUTSIDE_LABEL";
        public const string ParentTypeMismatch = "PARENT_TYPE_MISMATCH";
        public const string ParentCycle = "PARENT_CYCLE";
        public const string ThemeHasParent = "THEME_HAS_PARENT";

        public static TopologyAuditResult Run(IEnumerable<WorkItem> visibleItems, IEnumerable<WorkItem>? sourceItems = null, string? labelFilter = null)
        {
            var visible = visibleItems.Where(i => i != null).ToList();
            var source = sourceItems == null ? visible : sourceItems.Where(i => i != null).ToList();
            if (source.Count == 0) source = visible;

            var index = new Dictionary<string, List<WorkItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
            {
                foreach (var key in new[] { item.CanonicalId, item.Key }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    List<WorkItem>? list;
                    if (!index.TryGetValue(key, out list) || list == null) index[key] = list = new List<WorkItem>();
                    if (!list.Contains(item)) list.Add(item);
                }
            }
            var visibleKeys = new HashSet<string>(visible.Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            var findings = new List<Finding>();
            var edges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in visible)
            {
                var type = item.Type;
                if (type == WorkItemType.Unclassified)
                    findings.Add(new Finding(item, UnclassifiedType, Severity.Warning, "No unambiguous supported type is declared in halyard-meta or type:: labels."));
                var declared = item.DeclaredTypeText;
                if (!string.IsNullOrWhiteSpace(declared) && WorkItemTypes.Parse(declared) == WorkItemType.Unclassified)
                    findings.Add(new Finding(item, UnknownDeclaredType, Severity.Warning, "The halyard-meta type '" + declared + "' is not Theme, Epic, Feature, Story, Task or Bug."));
                var labels = item.TypeLabels;
                if (labels.Count > 1)
                    findings.Add(new Finding(item, ConflictingTypeLabels, Severity.Warning, "More than one type:: label is present; keep the intended one."));
                if (item.Meta != null && item.Meta.Type != WorkItemType.Unclassified && labels.Count > 0 && labels.Any(l => l != item.Meta.Type))
                    findings.Add(new Finding(item, TypeMetadataLabelConflict, Severity.Warning, "The halyard-meta type disagrees with a type:: label."));

                if (string.IsNullOrWhiteSpace(item.ExplicitId))
                    findings.Add(new Finding(item, NoCanonicalId, Severity.Info, "No stable halyard-meta id; the provider number is used for display only."));
                else
                {
                    List<WorkItem>? same;
                    if (index.TryGetValue(item.CanonicalId, out same) && same != null && same.Count > 1)
                        findings.Add(new Finding(item, DuplicateCanonicalId, Severity.Error, "This stable id belongs to " + same.Count + " imported work items and cannot identify a unique parent."));
                }

                var parentId = item.ParentId;
                var expected = WorkItemTypes.ExpectedParent(type);
                if (string.IsNullOrWhiteSpace(parentId))
                {
                    if (expected != null)
                        findings.Add(new Finding(item, ParentNotDeclared, Severity.Warning, "No parent is declared in halyard-meta; a " + WorkItemTypes.ToToken(type) + " normally belongs under a " + WorkItemTypes.ToToken(expected.Value) + "."));
                    continue;
                }
                if (type == WorkItemType.Theme)
                    findings.Add(new Finding(item, ThemeHasParent, Severity.Warning, "Themes are top-level lanes and should not declare a parent."));
                if (string.Equals(parentId, item.CanonicalId, StringComparison.OrdinalIgnoreCase) || string.Equals(parentId, item.Key, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new Finding(item, SelfParent, Severity.Error, "This work item refers to itself as its parent."));
                    continue;
                }
                List<WorkItem>? matches;
                if (!index.TryGetValue(parentId!, out matches) || matches == null)
                {
                    findings.Add(new Finding(item, ParentUnresolved, Severity.Warning, "Declared parent '" + parentId + "' was not found in the imported source."));
                    continue;
                }
                if (matches.Count != 1)
                {
                    findings.Add(new Finding(item, AmbiguousParentId, Severity.Error, "Multiple imported work items match the declared parent '" + parentId + "'."));
                    continue;
                }
                var parent = matches[0];
                if (!visibleKeys.Contains(parent.Key))
                {
                    findings.Add(new Finding(item, ParentOutsideFilter, Severity.Info, "The declared parent exists in the source but is outside the current label filter."));
                    continue;
                }
                edges[item.Key] = parent.Key;
                if (expected != null && parent.Type != expected.Value)
                    findings.Add(new Finding(item, ParentTypeMismatch, Severity.Warning, "A " + WorkItemTypes.ToToken(type) + " normally belongs under a " + WorkItemTypes.ToToken(expected.Value) + ", but the parent is declared as " + WorkItemTypes.ToToken(parent.Type) + "."));
            }

            // Report each true cycle member only.
            var cycleNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in visible)
            {
                var path = new List<string>();
                var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var cursor = item.Key;
                while (edges.ContainsKey(cursor))
                {
                    int pos;
                    if (positions.TryGetValue(cursor, out pos))
                    {
                        for (var i = pos; i < path.Count; i++) cycleNodes.Add(path[i]);
                        break;
                    }
                    positions[cursor] = path.Count;
                    path.Add(cursor);
                    cursor = edges[cursor];
                }
            }
            foreach (var item in visible)
            {
                if (cycleNodes.Contains(item.Key))
                    findings.Add(new Finding(item, ParentCycle, Severity.Error, "The declared parent links form a circular chain."));
            }

            return new TopologyAuditResult(findings, edges, visible.Count, source.Count, visible.Count(v => v.Type != WorkItemType.Unclassified), labelFilter);
        }
    }
}
