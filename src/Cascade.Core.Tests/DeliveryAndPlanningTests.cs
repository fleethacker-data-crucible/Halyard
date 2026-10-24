using System;
using System.IO;
using System.Linq;
using Cascade.Core.Analysis;
using Cascade.Core.Model;
using Cascade.Core.Planning;
using Cascade.Core.Synthetic;
using Xunit;

namespace Cascade.Core.Tests
{
    public static class TeamFixture
    {
        public static string ConfigDir()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "config", "team.json");
                    if (File.Exists(candidate)) return Path.Combine(dir.FullName, "config");
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("config/team.json not found above " + AppContext.BaseDirectory);
        }

        public static TeamConfig Load()
        {
            var dir = ConfigDir();
            return TeamConfig.Load(Path.Combine(dir, "team.json"), Path.Combine(dir, "synapse"));
        }

        /// <summary>A date inside the synthetic catalogue's current sprint (S23 = 2026-10-19..11-01).</summary>
        public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero);
    }

    public class TeamConfigTests
    {
        [Fact]
        public void LoadsTeamsPeopleAndSynapseCards()
        {
            var team = TeamFixture.Load();
            Assert.Single(team.Teams);
            Assert.Equal(22, team.TeamCapacity(team.Teams[0]));
            Assert.Equal(4, team.People.Count);
            Assert.Equal(4, team.CardsImported);
            var jordan = team.FindPerson("jordan-brooks")!;
            Assert.True(jordan.FromSynapse);
            Assert.True(jordan.Has("sql-pipelines"));
            Assert.InRange(jordan.Confidence("sql-pipelines"), 0, 4);
            Assert.Equal(8, team.PersonCapacity(jordan));
            Assert.Equal("2026-S23", team.Sprint.Name(team.Sprint.Current(TeamFixture.Now)));
            Assert.Equal(23, team.Sprint.NumberOf("2026-S23"));
            Assert.Null(team.Sprint.NumberOf("FY26-Q1"));
            Assert.Empty(team.Warnings);
        }

        [Fact]
        public void SkillsTaxonomyMatchesSynapseIds()
        {
            var skills = SkillsTaxonomy.Load(Path.Combine(TeamFixture.ConfigDir(), "skills.json"));
            Assert.Equal(104, skills.Skills.Count);
            Assert.Equal(13, skills.Domains.Count);
            Assert.True(skills.Contains("sql-pipelines"));
            Assert.Equal("ci-cd-pipelines", SkillsTaxonomy.Slug("CI/CD Pipelines"));
            var team = TeamFixture.Load();
            Assert.All(team.People.SelectMany(p => p.Skills.Keys), id => Assert.True(skills.Contains(id), id + " is not in the taxonomy"));
        }
    }

    public class DeliveryTests
    {
        private static (DeliveryReport report, TeamConfig team) Build(int total = 100)
        {
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(total));
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            var team = TeamFixture.Load();
            return (Delivery.Build(items, team, TeamFixture.Now, "test"), team);
        }

        [Fact]
        public void VelocityComesFromClosedPointsInPastSprints()
        {
            var (r, _) = Build();
            Assert.Equal("2026-S23", r.CurrentSprint);
            var past = r.Velocity.Where(v => v.IsPast).ToList();
            Assert.Equal(2, past.Count); // S21, S22
            Assert.All(past, v => Assert.True(v.PointsDone > 0));
            Assert.True(r.AverageVelocity > 0);
            Assert.Contains(r.Velocity, v => v.IsCurrent && v.PointsPlanned >= v.PointsDone);
        }

        [Fact]
        public void FeaturesHaveStatusesWithReasons()
        {
            var (r, _) = Build();
            Assert.Equal(18, r.Features.Count);
            Assert.All(r.Features, f => Assert.False(string.IsNullOrEmpty(f.Reason)));
            Assert.All(r.Features, f => Assert.InRange(f.Percent, 0, 100));
            Assert.Contains(r.Features, f => f.Status == FeatureStatus.AtRisk && f.Blocked > 0);
            Assert.Contains(r.Features, f => f.Status == FeatureStatus.OnTrack);
            Assert.All(r.Features, f => Assert.NotNull(f.Epic));
            Assert.All(r.Features, f => Assert.NotNull(f.Target));
        }

        [Fact]
        public void BlockersCarryAgeAndEscalation()
        {
            var (r, _) = Build();
            Assert.InRange(r.Blockers.Count, 2, 4);
            Assert.All(r.Blockers, b => Assert.True(b.Type == "ops" || b.Type == "dependency"));
            Assert.All(r.Blockers, b => Assert.NotNull(b.Impact));
            Assert.Contains(r.Blockers, b => b.Escalated);
            Assert.Equal(r.Blockers.OrderByDescending(b => b.AgeBusinessDays).Select(b => b.Key), r.Blockers.Select(b => b.Key));
        }

        [Fact]
        public void CapacityAndReadyBacklogUseTheTeamConfig()
        {
            var (r, team) = Build();
            Assert.True(r.Capacity.Configured);
            Assert.Equal(22, r.Capacity.TeamCapacity);
            Assert.True(r.Capacity.PlannedPoints > 0);
            Assert.Equal(0.75, r.Capacity.Committed);
            Assert.True(r.Ready.Stories > 0);
            Assert.True(r.Ready.Points > 0);
            Assert.Equal(2, r.Themes.Count);
            Assert.All(r.Themes, t => Assert.True(t.Points >= t.PointsDone));
        }

        [Fact]
        public void BusinessDaysSkipWeekends()
        {
            var fri = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(5, Delivery.BusinessDays(fri, fri.AddDays(7)));
            Assert.Equal(0, Delivery.BusinessDays(fri, fri));
            DateTimeOffset end;
            Assert.True(Delivery.TryParseMonth("2026-11", out end));
            Assert.Equal(30, end.Day);
        }

        [Fact]
        public void HtmlReportIsSelfContainedAndEscaped()
        {
            var (r, _) = Build();
            r.Features[0].Title = "<script>alert(1)</script>";
            var html = DeliveryHtml.Render(r, "Test <Org>");
            Assert.StartsWith("<!doctype html>", html);
            Assert.DoesNotContain("<script>alert", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("Test &lt;Org&gt;", html);
            Assert.Contains("Feature % complete", html);
            Assert.Contains("Open blockers", html);
            Assert.DoesNotContain("<script", html.Substring(html.IndexOf("<body", StringComparison.Ordinal)));
        }

        [Fact]
        public void WithoutTeamConfigCapacityIsNotAssessed()
        {
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(40));
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            var r = Delivery.Build(items, TeamConfig.Default(), TeamFixture.Now);
            Assert.False(r.Capacity.Configured);
            Assert.Contains(r.Notes, n => n.Contains("team.json"));
        }
    }
}
