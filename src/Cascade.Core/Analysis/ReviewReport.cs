using System;
using System.Collections.Generic;
using System.Linq;
using Cascade.Core.Model;
using Cascade.Core.Schema;

namespace Cascade.Core.Analysis
{
    public sealed class ItemReview
    {
        public ItemReview(WorkItem item)
        {
            Key = item.Key;
            Number = item.Number;
            CanonicalId = item.CanonicalId;
            Title = item.Title;
            Url = item.Url;
            Type = item.Type;
            State = item.State;
            Labels = item.Labels.ToList();
            ParentId = item.ParentId;
            ThemeId = item.ThemeId;
            HasMeta = item.HasMeta;
            Points = item.Meta == null ? null : item.Meta.Points;
            Effort = item.Meta == null ? null : item.Meta.Effort;
            Sprint = item.Meta == null ? null : item.Meta.Sprint;
            Area = item.Meta == null ? null : item.Meta.Area;
            UpdatedAt = item.UpdatedAt;
            MissingMeta = new List<string>();
            Deficiencies = new List<string>();
            Findings = new List<Finding>();
        }

        public string Key { get; private set; }
        public long Number { get; private set; }
        public string CanonicalId { get; private set; }
        public string Title { get; private set; }
        public string? Url { get; private set; }
        public WorkItemType Type { get; private set; }
        public WorkItemState State { get; private set; }
        public List<string> Labels { get; private set; }
        public string? ParentId { get; private set; }
        public string? ResolvedParentKey { get; set; }
        public string? ThemeId { get; private set; }
        public bool HasMeta { get; private set; }
        public string? Points { get; private set; }
        public string? Effort { get; private set; }
        public string? Sprint { get; private set; }
        public string? Area { get; private set; }
        public DateTimeOffset? UpdatedAt { get; private set; }
        public List<string> MissingMeta { get; private set; }
        public List<string> Deficiencies { get; private set; }
        public List<Finding> Findings { get; private set; }
        public bool NeedsReview { get { return MissingMeta.Count > 0 || Deficiencies.Count > 0 || Findings.Any(f => f.Severity != Severity.Info); } }
    }

    public sealed class ReviewSummary
    {
        public int Total { get; set; }
        public int Classified { get; set; }
        public int WithMeta { get; set; }
        public int NeedsReview { get; set; }
        public int MissingParents { get; set; }
        public int StoriesWithPoints { get; set; }
        public int Stories { get; set; }
        public int FeaturesWithEffort { get; set; }
        public int Features { get; set; }
        public Dictionary<string, int> ByType { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> DeficiencyCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> FindingCounts { get; set; } = new Dictionary<string, int>();
        public int TotalPoints { get; set; }
    }

    public sealed class ReviewReport
    {
        public ReviewReport(IReadOnlyList<ItemReview> items, TopologyAuditResult audit, IReadOnlyList<ParentReview> parentReviews, ReviewSummary summary, string? labelFilter)
        {
            Items = items;
            Audit = audit;
            ParentReviews = parentReviews;
            Summary = summary;
            LabelFilter = labelFilter;
            GeneratedAt = DateTimeOffset.UtcNow;
        }

        public IReadOnlyList<ItemReview> Items { get; private set; }
        public TopologyAuditResult Audit { get; private set; }
        public IReadOnlyList<ParentReview> ParentReviews { get; private set; }
        public ReviewSummary Summary { get; private set; }
        public string? LabelFilter { get; private set; }
        public DateTimeOffset GeneratedAt { get; private set; }
    }

    /// <summary>
    /// The deterministic, read-only intelligence pass: schema completeness, DoR deficiencies, topology
    /// findings and parent candidates for every item in scope. No provider writes, no LLM calls.
    /// </summary>
    public static class Review
    {
        public static ReviewReport Run(IEnumerable<WorkItem> items, Taxonomy taxonomy, string? labelFilter = null, IEnumerable<WorkItem>? sourceItems = null)
        {
            var list = items.Where(i => i != null).ToList();
            var audit = TopologyAudit.Run(list, sourceItems, labelFilter);
            var parents = RelationshipReview.Run(list, audit.ParentEdges);
            var findingsByKey = audit.Findings.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var reviews = new List<ItemReview>();
            var summary = new ReviewSummary { Total = list.Count };
            foreach (var item in list)
            {
                var r = new ItemReview(item);
                string? parentKey;
                if (audit.ParentEdges.TryGetValue(item.Key, out parentKey)) r.ResolvedParentKey = parentKey;
                r.MissingMeta.AddRange(taxonomy.MissingKeys(item.Type, item.Meta));
                r.Deficiencies.AddRange(Deficiencies.Evaluate(item));
                List<Finding>? f;
                if (findingsByKey.TryGetValue(item.Key, out f) && f != null) r.Findings.AddRange(f);
                reviews.Add(r);

                var typeToken = WorkItemTypes.ToToken(item.Type);
                int c;
                summary.ByType.TryGetValue(typeToken, out c);
                summary.ByType[typeToken] = c + 1;
                if (item.Type != WorkItemType.Unclassified) summary.Classified++;
                if (item.HasMeta) summary.WithMeta++;
                if (r.NeedsReview) summary.NeedsReview++;
                if (WorkItemTypes.ExpectedParent(item.Type) != null && r.ResolvedParentKey == null) summary.MissingParents++;
                if (item.Type == WorkItemType.Story)
                {
                    summary.Stories++;
                    int p;
                    if (item.Meta != null && int.TryParse(item.Meta.Points, out p))
                    {
                        summary.StoriesWithPoints++;
                        summary.TotalPoints += p;
                    }
                }
                if (item.Type == WorkItemType.Feature)
                {
                    summary.Features++;
                    if (item.Meta != null && Sizing.IsValidEffort(item.Meta.Effort) && !string.Equals(item.Meta.Effort, Sizing.Tbd, StringComparison.OrdinalIgnoreCase)) summary.FeaturesWithEffort++;
                }
                foreach (var code in r.Deficiencies)
                {
                    summary.DeficiencyCounts.TryGetValue(code, out c);
                    summary.DeficiencyCounts[code] = c + 1;
                }
                foreach (var finding in r.Findings)
                {
                    summary.FindingCounts.TryGetValue(finding.Code, out c);
                    summary.FindingCounts[finding.Code] = c + 1;
                }
            }
            return new ReviewReport(reviews, audit, parents, summary, labelFilter);
        }
    }
}
