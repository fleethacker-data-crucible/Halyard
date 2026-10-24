using System.Collections.Generic;
using System.Linq;
using Cascade.Core.Analysis;
using Cascade.Core.Model;
using Cascade.Core.Schema;
using Cascade.Core.Synthetic;
using Xunit;

namespace Cascade.Core.Tests
{
    public static class Items
    {
        public static readonly RepositoryRef Repo = new RepositoryRef(ProviderKind.GitHub, "sun-of-anton/cascade-sandbox");

        public static WorkItem Make(long number, WorkItemType type, string id, string? parent, string? extraMeta = null, string body = "", params string[] labels)
        {
            var item = new WorkItem(Repo, number) { Title = WorkItemTypes.ToToken(type) + " :: " + id };
            var meta = "~~~cascade-meta\ntype: " + WorkItemTypes.ToToken(type) + "\nid: " + id + "\n" + (parent == null ? string.Empty : "parent: " + parent + "\n") + (extraMeta ?? string.Empty) + "~~~\n\n";
            item.Body = meta + body;
            item.Labels.AddRange(labels);
            return item;
        }
    }

    public class TopologyAuditTests
    {
        [Fact]
        public void CleanChainHasNoFindingsAndFullEdges()
        {
            var items = new[]
            {
                Items.Make(1, WorkItemType.Theme, "THEME-t", null),
                Items.Make(2, WorkItemType.Epic, "EPIC-e", "THEME-t"),
                Items.Make(3, WorkItemType.Feature, "FEATURE-f", "EPIC-e"),
                Items.Make(4, WorkItemType.Story, "STORY-s", "FEATURE-f"),
            };
            var audit = TopologyAudit.Run(items);
            Assert.Empty(audit.Findings);
            Assert.Equal(3, audit.ParentEdges.Count);
            Assert.Equal(items[2].Key, audit.ParentEdges[items[3].Key]);
        }

        [Fact]
        public void DetectsEachCycleMemberOnce()
        {
            var items = new[]
            {
                Items.Make(1, WorkItemType.Feature, "FEATURE-a", "FEATURE-b"),
                Items.Make(2, WorkItemType.Feature, "FEATURE-b", "FEATURE-a"),
                Items.Make(3, WorkItemType.Story, "STORY-c", "FEATURE-a"),
            };
            var audit = TopologyAudit.Run(items);
            var cycles = audit.Findings.Where(f => f.Code == TopologyAudit.ParentCycle).Select(f => f.CanonicalId).ToList();
            Assert.Equal(new[] { "FEATURE-a", "FEATURE-b" }, cycles.OrderBy(c => c).ToArray());
            Assert.DoesNotContain("STORY-c", cycles);
        }

        [Fact]
        public void ReportsUnresolvedAmbiguousAndSelfParents()
        {
            var items = new[]
            {
                Items.Make(1, WorkItemType.Story, "STORY-a", "FEATURE-missing"),
                Items.Make(2, WorkItemType.Feature, "FEATURE-dup", "EPIC-x"),
                Items.Make(3, WorkItemType.Feature, "FEATURE-dup", "EPIC-x"),
                Items.Make(4, WorkItemType.Story, "STORY-b", "FEATURE-dup"),
                Items.Make(5, WorkItemType.Story, "STORY-self", "STORY-self"),
            };
            var codes = TopologyAudit.Run(items).Findings.ToLookup(f => f.CanonicalId, f => f.Code);
            Assert.Contains(TopologyAudit.ParentUnresolved, codes["STORY-a"]);
            Assert.Contains(TopologyAudit.DuplicateCanonicalId, codes["FEATURE-dup"]);
            Assert.Contains(TopologyAudit.AmbiguousParentId, codes["STORY-b"]);
            Assert.Contains(TopologyAudit.SelfParent, codes["STORY-self"]);
        }

        [Fact]
        public void ParentOutsideFilterIsDisclosedWithoutLinking()
        {
            var feature = Items.Make(1, WorkItemType.Feature, "FEATURE-f", "EPIC-e", null, string.Empty, "team::b");
            var story = Items.Make(2, WorkItemType.Story, "STORY-s", "FEATURE-f", null, string.Empty, "team::a");
            var audit = TopologyAudit.Run(new[] { story }, new[] { feature, story }, "team::a");
            Assert.Single(audit.Findings, f => f.Code == TopologyAudit.ParentOutsideFilter);
            Assert.Empty(audit.ParentEdges);
        }

        [Fact]
        public void TypeFromLabelsAndConflicts()
        {
            var plain = new WorkItem(Items.Repo, 9) { Title = "x", Body = "no meta" };
            plain.Labels.Add("type::story");
            Assert.Equal(WorkItemType.Story, plain.Type);
            plain.Labels.Add("type::feature");
            Assert.Equal(WorkItemType.Unclassified, plain.Type);
            var audit = TopologyAudit.Run(new[] { plain });
            Assert.Contains(audit.Findings, f => f.Code == TopologyAudit.ConflictingTypeLabels);
            Assert.Contains(audit.Findings, f => f.Code == TopologyAudit.UnclassifiedType);
        }

        [Fact]
        public void TypeMismatchIsAWarningNotAnError()
        {
            var items = new[]
            {
                Items.Make(1, WorkItemType.Epic, "EPIC-e", null),
                Items.Make(2, WorkItemType.Story, "STORY-s", "EPIC-e"),
            };
            var f = TopologyAudit.Run(items).Findings.Single(x => x.Code == TopologyAudit.ParentTypeMismatch);
            Assert.Equal(Severity.Warning, f.Severity);
        }
    }

    public class HierarchyAndReviewTests
    {
        [Fact]
        public void BuildsForestWithRollups()
        {
            var items = new[]
            {
                Items.Make(1, WorkItemType.Theme, "THEME-t", null),
                Items.Make(2, WorkItemType.Epic, "EPIC-e", "THEME-t"),
                Items.Make(3, WorkItemType.Feature, "FEATURE-f", "EPIC-e"),
                Items.Make(4, WorkItemType.Story, "STORY-a", "FEATURE-f", "points: 5\n"),
                Items.Make(5, WorkItemType.Story, "STORY-b", "FEATURE-f", "points: 3\n"),
                Items.Make(6, WorkItemType.Story, "STORY-orphan", null, "points: 8\n"),
            };
            var audit = TopologyAudit.Run(items);
            var forest = HierarchyTree.Build(items, audit.ParentEdges);
            Assert.Single(forest.Roots);
            Assert.Single(forest.Orphans);
            Assert.Equal(8, forest.Roots[0].RolledUpPoints());
            Assert.Equal(4, forest.Roots[0].Descendants().Count());
        }

        [Fact]
        public void ReviewSummarisesSyntheticCatalogueCleanly()
        {
            var options = SyntheticOptions.ForTotal(100);
            var catalog = SyntheticCatalog.Generate(options);
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            var report = Review.Run(items, Taxonomy.Default());
            Assert.Equal(100, report.Summary.Total);
            Assert.Equal(0, report.Summary.NeedsReview);
            Assert.Equal(0, report.Summary.MissingParents);
            Assert.Empty(report.Audit.Findings);
            Assert.Equal(50, report.Summary.Stories);
            Assert.Equal(50, report.Summary.StoriesWithPoints);
            Assert.Equal(18, report.Summary.FeaturesWithEffort);
        }

        [Fact]
        public void InjectedDefectsAreFoundAndParentIsProposed()
        {
            var options = SyntheticOptions.ForTotal(100);
            options.IncludeDefects = true;
            var catalog = SyntheticCatalog.Generate(options);
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            var report = Review.Run(items, Taxonomy.Default());
            Assert.True(report.Summary.NeedsReview >= 3);
            var orphan = catalog.Single(c => c.Defects.Contains(Deficiencies.StoryMissingParent));
            var review = report.ParentReviews.Single(p => p.CanonicalId == orphan.Id);
            Assert.NotEmpty(review.Candidates);
            // The generator removed the parent from meta but the body still names the feature, so evidence should rank it first.
            Assert.Equal(orphan.ParentId, review.Candidates[0].CanonicalId);
        }

        [Fact]
        public void KeywordsDropStopWordsAndPlurals()
        {
            var words = RelationshipReview.Keywords("Validate Required Source Fields for the users");
            Assert.Contains("validate", words);
            Assert.Contains("field", words);
            Assert.DoesNotContain("the", words);
            Assert.DoesNotContain("users", words);
        }
    }
}
