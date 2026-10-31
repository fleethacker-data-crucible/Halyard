using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Analysis;
using Halyard.Core.Model;
using Halyard.Core.Planning;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    public class SprintReviewTests
    {
        private static readonly RepositoryRef Repo = new RepositoryRef(ProviderKind.GitHub, "example-org/synthetic");
        // Sprints are 14 days from 2026-09-21: S21 = Sep 21, S22 = Oct 5, S23 = Oct 19.
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        private static TeamConfig Team(int capacity = 20, double committed = 0.75)
        {
            var t = new TeamConfig();
            t.Sprint.Prefix = "2026-S";
            t.Sprint.LengthDays = 14;
            t.Sprint.FirstNumber = 21;
            t.Sprint.FirstStart = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
            t.Teams.Add(new Team { Id = "t", Name = "T", PointsPerSprint = capacity, Committed = committed });
            return t;
        }

        private static long _n;

        private static WorkItem Story(string sprint, int points, bool closed)
        {
            var w = new WorkItem(Repo, ++_n) { Title = "STORY :: s" + _n, State = closed ? WorkItemState.Closed : WorkItemState.Open };
            var meta = new HalyardMeta();
            meta.Type = WorkItemType.Story;
            meta.Points = points.ToString();
            if (sprint != null) meta.Sprint = sprint;
            w.Body = meta.ToFence();
            return w;
        }

        [Fact]
        public void ComparesPlannedWithDeliveredPerFinishedSprint()
        {
            var items = new List<WorkItem>
            {
                Story("2026-S21", 5, true), Story("2026-S21", 5, true), Story("2026-S21", 3, false), // 13 planned, 10 delivered, 3 slipped
                Story("2026-S22", 8, true), Story("2026-S22", 2, false),                              // 10 planned, 8 delivered, 2 slipped
                Story("2026-S23", 5, true),                                                           // current sprint is not reviewed
                Story(null, 5, false),
            };
            var review = SprintReview.Build(items, Team(), Now, 6);

            Assert.Equal("2026-S23", review.CurrentSprint);
            Assert.Equal(new[] { "2026-S21", "2026-S22" }, review.Sprints.Select(s => s.Sprint).ToArray());
            var s21 = review.Sprints[0];
            Assert.Equal(13, s21.PointsPlanned);
            Assert.Equal(10, s21.PointsDelivered);
            Assert.Equal(3, s21.PointsSlipped);
            Assert.Equal(0.77, s21.DeliveryRate);
            Assert.Equal(3, s21.StoriesPlanned);
            Assert.Equal(2, s21.StoriesDelivered);
            Assert.Equal(2, review.CarryOverKeys.Count);
            Assert.Equal(9.0, review.AverageDelivered);
        }

        [Fact]
        public void RecommendsWhatTheTeamDeliveredNeverMoreThanItsCommitment()
        {
            // capacity 20 at 75% = 15 committed; delivered 8 and 8 -> average 8
            var items = new List<WorkItem> { Story("2026-S21", 8, true), Story("2026-S22", 8, true) };
            var review = SprintReview.Build(items, Team(), Now, 6);
            Assert.Equal(8, review.RecommendedCommitPoints);
            Assert.Equal(0.4, review.RecommendedShare);
            Assert.Contains(review.Notes, n => n.Contains("commits 8"));

            // delivering more than the commitment does not raise it
            var strong = new List<WorkItem> { Story("2026-S21", 8, true), Story("2026-S21", 8, true), Story("2026-S22", 8, true), Story("2026-S22", 8, true) };
            var capped = SprintReview.Build(strong, Team(), Now, 6);
            Assert.Equal(15, capped.RecommendedCommitPoints);
            Assert.Equal(0.75, capped.RecommendedShare);
        }

        [Fact]
        public void OnlyTheLastThreeReviewedSprintsCount()
        {
            var team = Team();
            var items = new List<WorkItem>();
            // current sprint S25 at 2026-11-17: S21 delivered 8 (old), S22..S24 delivered 5 each
            items.Add(Story("2026-S21", 8, true));
            foreach (var s in new[] { "2026-S22", "2026-S23", "2026-S24" }) items.Add(Story(s, 5, true));
            var review = SprintReview.Build(items, team, new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), 6);
            Assert.Equal("2026-S25", review.CurrentSprint);
            Assert.Equal(4, review.Sprints.Count);
            Assert.Equal(5.0, review.AverageDelivered);
        }

        [Fact]
        public void WithNoFinishedSprintItSaysSoAndRecommendsNothing()
        {
            var review = SprintReview.Build(new List<WorkItem> { Story("2026-S23", 5, false) }, Team(), Now, 6);
            Assert.Empty(review.Sprints);
            Assert.Null(review.RecommendedShare);
            Assert.Equal(0, review.RecommendedCommitPoints);
            Assert.Contains("nothing to compare", Assert.Single(review.Notes));
        }

        [Fact]
        public void SprintsWithNothingPlannedAreLeftOut()
        {
            var review = SprintReview.Build(new List<WorkItem> { Story("2026-S22", 3, true) }, Team(), Now, 6);
            Assert.Equal("2026-S22", Assert.Single(review.Sprints).Sprint);
        }

        [Fact]
        public void WithoutATeamThereIsNoRecommendation()
        {
            var team = Team();
            team.Teams.Clear();
            var review = SprintReview.Build(new List<WorkItem> { Story("2026-S22", 3, true) }, team, Now, 6);
            Assert.Null(review.RecommendedShare);
            Assert.Equal(3.0, review.AverageDelivered);
        }

        // ---- feeding the next plan ----

        private static (List<WorkItem> items, ReviewReport report, TeamConfig team) Scenario()
        {
            var items = SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(100)), Items.Repo);
            return (items, Review.Run(items, Schema.Taxonomy.Default()), TeamFixture.Load());
        }

        [Fact]
        public void TheSyntheticHistoryProducesAConsistentReview()
        {
            var (items, _, team) = Scenario();
            var review = SprintReview.Build(items, team, TeamFixture.Now, 6);
            Assert.NotEmpty(review.Sprints);
            Assert.All(review.Sprints, s =>
            {
                Assert.InRange(s.PointsDelivered, 0, s.PointsPlanned);
                Assert.Equal(s.PointsPlanned - s.PointsDelivered, s.PointsSlipped);
            });
            var byKey = items.ToDictionary(i => i.Key);
            Assert.All(review.CarryOverKeys, k => Assert.Equal(WorkItemState.Open, byKey[k].State));
        }

        [Fact]
        public void TheNextPlanCommitsTheReviewedShareAndPlacesSlippedStoriesFirst()
        {
            var (items, report, team) = Scenario();
            var review = SprintReview.Build(items, team, TeamFixture.Now, 6);
            var plain = SprintPlanner.Plan(items, report, team, TeamFixture.Now, new PlanOptions { SprintsAhead = 2 });
            var informed = SprintPlanner.Plan(items, report, team, TeamFixture.Now, new PlanOptions { SprintsAhead = 2, Review = review });

            if (review.RecommendedShare != null)
                Assert.Equal((int)Math.Round(team.TeamCapacity(team.Teams[0]) * review.RecommendedShare.Value), informed.Sprints[0].CommitCapacity);
            Assert.NotNull(plain.Notes);

            var slippedPlaced = informed.Sprints.SelectMany(s => s.Stories).Where(s => !s.AlreadyPlanned && review.CarryOverKeys.Contains(s.Key)).ToList();
            if (slippedPlaced.Count > 0)
            {
                Assert.All(slippedPlaced, s => Assert.Contains("slipped out of a finished sprint", s.Reason));
                var firstRanks = informed.Sprints.SelectMany(s => s.Stories).Where(s => !s.AlreadyPlanned).OrderBy(s => s.Priority).Take(slippedPlaced.Count).Select(s => s.Key).ToHashSet();
                Assert.Subset(review.CarryOverKeys, firstRanks);
            }
        }

        [Fact]
        public void AnExplicitCommitShareBeatsTheReview()
        {
            var (items, report, team) = Scenario();
            var review = SprintReview.Build(items, team, TeamFixture.Now, 6);
            var plan = SprintPlanner.Plan(items, report, team, TeamFixture.Now, new PlanOptions { SprintsAhead = 1, CommitShare = 0.5, Review = review });
            Assert.Equal((int)Math.Round(team.TeamCapacity(team.Teams[0]) * 0.5), plan.Sprints[0].CommitCapacity);
            Assert.DoesNotContain(plan.Notes, n => n.StartsWith("Commitment set to"));
        }

        [Fact]
        public void AHandBuiltReviewLowersTheCommitmentAndSaysWhy()
        {
            var (items, report, team) = Scenario();
            var review = new SprintReviewReport { RecommendedShare = 0.3 };
            var plan = SprintPlanner.Plan(items, report, team, TeamFixture.Now, new PlanOptions { SprintsAhead = 1, Review = review });
            Assert.Equal((int)Math.Round(team.TeamCapacity(team.Teams[0]) * 0.3), plan.Sprints[0].CommitCapacity);
            Assert.Contains(plan.Notes, n => n.Contains("Commitment set to 30% from the sprint review"));
        }
    }
}
