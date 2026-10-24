using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Cascade.Core.Model;

namespace Cascade.Core.Analysis
{
    public sealed class ParentCandidate
    {
        public ParentCandidate(WorkItem parent, double score, IReadOnlyList<string> evidence)
        {
            Key = parent.Key;
            CanonicalId = parent.CanonicalId;
            Title = parent.Title;
            Type = parent.Type;
            Score = score;
            Evidence = evidence;
        }

        public string Key { get; private set; }
        public string CanonicalId { get; private set; }
        public string Title { get; private set; }
        public WorkItemType Type { get; private set; }
        public double Score { get; private set; }
        public IReadOnlyList<string> Evidence { get; private set; }
    }

    public sealed class ParentReview
    {
        public ParentReview(WorkItem item, WorkItemType? expectedParentType, IReadOnlyList<ParentCandidate> candidates)
        {
            Key = item.Key;
            CanonicalId = item.CanonicalId;
            Title = item.Title;
            Type = item.Type;
            DeclaredParent = item.ParentId;
            ExpectedParentType = expectedParentType;
            Candidates = candidates;
        }

        public string Key { get; private set; }
        public string CanonicalId { get; private set; }
        public string Title { get; private set; }
        public WorkItemType Type { get; private set; }
        public string? DeclaredParent { get; private set; }
        public WorkItemType? ExpectedParentType { get; private set; }
        public IReadOnlyList<ParentCandidate> Candidates { get; private set; }
    }

    /// <summary>
    /// Evidence-ranked parent candidates for items that lack a resolved parent. Deterministic: scores come
    /// from shared title keywords, matching theme/area and label overlap. It never assigns a parent; the
    /// candidates are proposals for a human (or an LLM pass) to confirm.
    /// </summary>
    public static class RelationshipReview
    {
        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","the","to","for","of","in","on","and","or","by","with","as","from","is","are","be","we","want","i",
            "user","users","able","feature","story","task","epic","theme","work","item","support","create","new","update",
            "fix","implement","enable","build","add","improve","system","service","data","test","tests","sample","example",
        };

        public static IReadOnlyList<string> Keywords(string? title)
        {
            var words = new List<string>();
            if (string.IsNullOrWhiteSpace(title)) return words;
            foreach (Match m in Regex.Matches(title!.ToLowerInvariant(), "[a-z0-9]{3,}"))
            {
                var w = m.Value;
                if (w.Length > 5 && w.EndsWith("s")) w = w.Substring(0, w.Length - 1);
                if (Stop.Contains(w) || words.Contains(w)) continue;
                words.Add(w);
            }
            return words;
        }

        public static IReadOnlyList<ParentReview> Run(IEnumerable<WorkItem> items, IReadOnlyDictionary<string, string> resolvedEdges, int maxCandidates = 5)
        {
            var list = items.Where(i => i != null).ToList();
            var reviews = new List<ParentReview>();
            foreach (var item in list)
            {
                var expected = WorkItemTypes.ExpectedParent(item.Type);
                if (expected == null) continue;
                if (resolvedEdges.ContainsKey(item.Key)) continue;
                var keywords = Keywords(Descriptions_ShortName(item.Title));
                var candidates = new List<ParentCandidate>();
                foreach (var parent in list)
                {
                    if (parent == item || parent.Type != expected.Value) continue;
                    var evidence = new List<string>();
                    double score = 0;
                    var shared = Keywords(Descriptions_ShortName(parent.Title)).Intersect(keywords, StringComparer.OrdinalIgnoreCase).ToList();
                    if (shared.Count > 0)
                    {
                        score += shared.Count * 2.0;
                        evidence.Add("shared title words: " + string.Join(", ", shared));
                    }
                    if (!string.IsNullOrWhiteSpace(item.ThemeId) && string.Equals(item.ThemeId, parent.ThemeId ?? parent.CanonicalId, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1.5;
                        evidence.Add("same theme " + item.ThemeId);
                    }
                    var itemArea = item.Meta == null ? null : item.Meta.Area;
                    var parentArea = parent.Meta == null ? null : parent.Meta.Area;
                    if (!string.IsNullOrWhiteSpace(itemArea) && string.Equals(itemArea, parentArea, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1.0;
                        evidence.Add("same area " + itemArea);
                    }
                    var sharedLabels = item.Labels.Intersect(parent.Labels, StringComparer.OrdinalIgnoreCase)
                        .Where(l => !l.StartsWith("type::", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sharedLabels.Count > 0)
                    {
                        score += Math.Min(sharedLabels.Count, 3) * 0.5;
                        evidence.Add("shared labels: " + string.Join(", ", sharedLabels.Take(3)));
                    }
                    if (!string.IsNullOrWhiteSpace(item.ParentId) && string.Equals(item.ParentId, parent.CanonicalId, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5.0;
                        evidence.Add("declared parent id matches");
                    }
                    var narrative = Schema.Descriptions.Narrative(item.Body);
                    if (!string.IsNullOrWhiteSpace(parent.ExplicitId) && narrative.IndexOf(parent.ExplicitId!, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 4.0;
                        evidence.Add("body references " + parent.ExplicitId);
                    }
                    else
                    {
                        var parentName = Schema.Descriptions.ShortName(parent.Title);
                        if (parentName.Length >= 8 && narrative.IndexOf(parentName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 2.5;
                            evidence.Add("body mentions \"" + parentName + "\"");
                        }
                    }
                    if (score <= 0) continue;
                    candidates.Add(new ParentCandidate(parent, Math.Round(score, 2), evidence));
                }
                candidates = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).Take(maxCandidates).ToList();
                reviews.Add(new ParentReview(item, expected, candidates));
            }
            return reviews;
        }

        private static string Descriptions_ShortName(string? title)
        {
            return Schema.Descriptions.ShortName(title);
        }
    }
}
