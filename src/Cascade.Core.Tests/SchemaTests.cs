using System.Linq;
using Cascade.Core.Model;
using Cascade.Core.Schema;
using Xunit;

namespace Cascade.Core.Tests
{
    public class CascadeMetaTests
    {
        [Fact]
        public void ParsesFencedBlockAndKeepsOrder()
        {
            var body = "~~~cascade-meta\ntype: STORY\nid: STORY-a\nparent: FEATURE-b\npoints: 5\n~~~\n\n## User need\ntext";
            var meta = CascadeMeta.Parse(body);
            Assert.NotNull(meta);
            Assert.Equal(WorkItemType.Story, meta!.Type);
            Assert.Equal("STORY-a", meta.Id);
            Assert.Equal("FEATURE-b", meta.Parent);
            Assert.Equal("5", meta.Points);
            Assert.Equal(new[] { "type", "id", "parent", "points" }, meta.Entries.Select(e => e.Key).ToArray());
        }

        [Fact]
        public void ParsesLegacyHtmlComment()
        {
            var meta = CascadeMeta.Parse("<!--\ntype: FEATURE\nid: FEATURE-x\neffort: Medium\n-->\nbody");
            Assert.NotNull(meta);
            Assert.Equal(WorkItemType.Feature, meta!.Type);
            Assert.Equal("Medium", meta.Effort);
        }

        [Fact]
        public void ReturnsNullWithoutTypeOrId()
        {
            Assert.Null(CascadeMeta.Parse("no block here"));
            Assert.Null(CascadeMeta.Parse("~~~cascade-meta\nfoo: bar\n~~~"));
            Assert.Null(CascadeMeta.Parse(null));
        }

        [Fact]
        public void KeysAreCaseInsensitiveAndValuesTrimmed()
        {
            var meta = new CascadeMeta();
            meta.Set("Points", "  5 ");
            Assert.Equal("5", meta.Get("points"));
            meta.Set("points", "8");
            Assert.Equal(1, meta.Count);
            meta.Set("points", null);
            Assert.False(meta.Has("points"));
        }

        [Fact]
        public void RoundTripsThroughFence()
        {
            var meta = new CascadeMeta().Set("type", "EPIC").Set("id", "EPIC-x").Set("theme", "THEME-y");
            var parsed = CascadeMeta.Parse(meta.ToFence() + "\n\nrest");
            Assert.Equal(meta.ToFence(), parsed!.ToFence());
        }

        [Fact]
        public void ApplyToBodyReplacesExistingFence()
        {
            var original = "~~~cascade-meta\ntype: STORY\nid: STORY-a\npoints: TBD\n~~~\n\n## Text\nkeep me";
            var meta = CascadeMeta.Parse(original)!;
            meta.Points = "3";
            var updated = CascadeMeta.ApplyToBody(original, meta);
            Assert.Contains("points: 3", updated);
            Assert.DoesNotContain("TBD", updated);
            Assert.Contains("keep me", updated);
            Assert.Equal(1, updated.Split(new[] { "~~~cascade-meta" }, System.StringSplitOptions.None).Length - 1);
        }
    }

    public class SizingTests
    {
        [Theory]
        [InlineData("5", "5")]
        [InlineData("13", "8")]
        [InlineData("21", "8")]
        [InlineData("4", "5")]
        [InlineData("0", "1")]
        [InlineData("3 story points", "3")]
        [InlineData("**2 pts**", "2")]
        [InlineData("TBD", "TBD")]
        [InlineData("", "TBD")]
        [InlineData("huge", "TBD")]
        public void ClampsPointsToPolicy(string raw, string expected)
        {
            Assert.Equal(expected, Sizing.ClampPoints(raw));
        }

        [Theory]
        [InlineData("S", "Small")]
        [InlineData("XS", "Small")]
        [InlineData("medium", "Medium")]
        [InlineData("XL", "Large")]
        [InlineData("Large", "Large")]
        [InlineData("tbd", "TBD")]
        [InlineData("?", "TBD")]
        public void ClampsEffortToThreeBands(string raw, string expected)
        {
            Assert.Equal(expected, Sizing.ClampEffort(raw));
        }

        [Fact]
        public void ValidatesPolicyValues()
        {
            Assert.True(Sizing.IsValidPoints("8"));
            Assert.False(Sizing.IsValidPoints("13"));
            Assert.True(Sizing.IsValidPoints("TBD"));
            Assert.True(Sizing.IsValidEffort("Small"));
            Assert.False(Sizing.IsValidEffort("XL"));
        }
    }

    public class DeficiencyTests
    {
        private static WorkItem Item(WorkItemType type, string metaExtra, string body)
        {
            var repo = new RepositoryRef(ProviderKind.GitHub, "o/r");
            var item = new WorkItem(repo, 1) { Title = WorkItemTypes.ToToken(type) + " :: x" };
            item.Body = "~~~cascade-meta\ntype: " + WorkItemTypes.ToToken(type) + "\nid: " + WorkItemTypes.ToToken(type) + "-x\n" + metaExtra + "~~~\n\n" + body;
            return item;
        }

        [Fact]
        public void CompleteStoryHasNoDeficiencies()
        {
            var item = Item(WorkItemType.Story, "parent: FEATURE-a\npoints: 3\nsprint: 2026-S21\n", "## User need\nAs a dev, I want x so that y.\n\n## Acceptance criteria\n- [ ] one\n\n## Tasks\n- [ ] do");
            Assert.Empty(Deficiencies.Evaluate(item));
        }

        [Fact]
        public void StoryGapsAreReported()
        {
            var item = Item(WorkItemType.Story, "points: 13\n", "just a line");
            var codes = Deficiencies.Evaluate(item);
            Assert.Contains(Deficiencies.StoryMissingParent, codes);
            Assert.Contains(Deficiencies.StoryMissingAcceptanceCriteria, codes);
            Assert.Contains(Deficiencies.StoryMissingUserNeed, codes);
            Assert.Contains(Deficiencies.StoryMissingTasks, codes);
            Assert.Contains(Deficiencies.StoryInvalidPoints, codes);
            Assert.Contains(Deficiencies.StoryMissingSprint, codes);
        }

        [Fact]
        public void FeatureWithPointsIsFlagged()
        {
            var item = Item(WorkItemType.Feature, "parent: EPIC-a\neffort: Medium\npoints: 5\n", "## Description\nA capability that does something useful for people.\n\n## Definition of done\n- [ ] shipped");
            var codes = Deficiencies.Evaluate(item);
            Assert.Contains(Deficiencies.FeatureHasPoints, codes);
            Assert.DoesNotContain(Deficiencies.FeatureMissingDoneWhen, codes);
        }

        [Fact]
        public void MissingMetaIsTheOnlyCodeForUnclassifiedItems()
        {
            var repo = new RepositoryRef(ProviderKind.GitLab, "123");
            var item = new WorkItem(repo, 7) { Title = "plain ticket", Body = "no meta" };
            Assert.Equal(new[] { Deficiencies.MissingMeta }, Deficiencies.Evaluate(item).ToArray());
        }

        [Fact]
        public void EveryRuleCodeIsUnique()
        {
            Assert.Equal(Deficiencies.Rules.Length, Deficiencies.Rules.Select(r => r.Code).Distinct().Count());
        }
    }

    public class DescriptionTests
    {
        [Fact]
        public void ComposeProducesFenceMirrorNarrativeOnce()
        {
            var meta = new CascadeMeta().Set("type", "STORY").Set("id", "STORY-a").Set("points", "5");
            var body = Descriptions.Compose(meta, "## Text\nhello");
            var again = Descriptions.Compose(meta, body);
            Assert.Equal(body, again);
            Assert.StartsWith("~~~cascade-meta", body);
            Assert.Contains("### Cascade", body);
            Assert.Contains("- **Story points:** 5", body);
            Assert.EndsWith("hello", body);
        }

        [Fact]
        public void RewriteSyncsSizingSection()
        {
            var meta = new CascadeMeta().Set("type", "STORY").Set("id", "STORY-a").Set("points", "TBD");
            var body = Descriptions.Compose(meta, "## Sizing\n- **Story points:** TBD\n\n## Notes\nn");
            meta.Points = "5";
            var updated = Descriptions.Rewrite(body, meta);
            Assert.Contains("points: 5", updated);
            Assert.Contains("## Sizing\n- **Story points:** 5", updated);
            Assert.DoesNotContain("TBD", updated);
            Assert.Contains("## Notes\nn", updated);
        }

        [Theory]
        [InlineData("STORY :: Map IAM roles :: HUMAN APPROVAL REQUIRED", "Map IAM roles")]
        [InlineData("feature :: Thing", "Thing")]
        [InlineData("No prefix", "No prefix")]
        public void ShortNameStripsConventions(string title, string expected)
        {
            Assert.Equal(expected, Descriptions.ShortName(title));
        }

        [Fact]
        public void MakeIdSlugifies()
        {
            Assert.Equal("STORY-map-iam-roles-to-postgres", Descriptions.MakeId(WorkItemType.Story, "Map IAM roles to Postgres!"));
        }
    }
}
