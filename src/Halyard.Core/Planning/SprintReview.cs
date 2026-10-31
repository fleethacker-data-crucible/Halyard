using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Model;
using Halyard.Core.Schema;

namespace Halyard.Core.Planning
{
    public sealed class SprintReviewRow
    {
        public string Sprint { get; set; }
        public int Number { get; set; }
        public int StoriesPlanned { get; set; }
        public int StoriesDelivered { get; set; }
        public int PointsPlanned { get; set; }
        public int PointsDelivered { get; set; }
        /// <summary>Points planned into the sprint that are still open (slipped).</summary>
        public int PointsSlipped { get; set; }
        /// <summary>Delivered / planned points, 0..1 (null when nothing was planned).</summary>
        public double? DeliveryRate { get; set; }
        public int TeamCapacity { get; set; }
        public List<string> SlippedKeys { get; set; }

        public SprintReviewRow()
        {
            Sprint = string.Empty;
            SlippedKeys = new List<string>();
        }
    }

    public sealed class SprintReviewReport
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public string CurrentSprint { get; set; }
        /// <summary>Closed sprints, oldest first.</summary>
        public List<SprintReviewRow> Sprints { get; set; }
        /// <summary>Average delivered points over the last three reviewed sprints.</summary>
        public double AverageDelivered { get; set; }
        /// <summary>Average delivery rate over the last three reviewed sprints that had a plan.</summary>
        public double? AverageDeliveryRate { get; set; }
        public int TeamCapacity { get; set; }
        /// <summary>Points the next plan should commit: what the team has actually delivered, never more than its configured commitment.</summary>
        public int RecommendedCommitPoints { get; set; }
        /// <summary>RecommendedCommitPoints as a share of team capacity, clamped to 0.1..1; null when there is nothing to base it on.</summary>
        public double? RecommendedShare { get; set; }
        public double ConfiguredShare { get; set; }
        /// <summary>Open stories planned into a closed sprint, keyed; the next plan picks them up first.</summary>
        public HashSet<string> CarryOverKeys { get; set; }
        public List<string> Notes { get; set; }

        public SprintReviewReport()
        {
            CurrentSprint = string.Empty;
            Sprints = new List<SprintReviewRow>();
            CarryOverKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Notes = new List<string>();
        }
    }

    /// <summary>
    /// Compares what each finished sprint planned with what it delivered and turns the gap into a number the next plan
    /// can use: commit what the team really delivers, and pick up what slipped first. Pure arithmetic over the
    /// snapshot; nothing is written.
    /// </summary>
    public static class SprintReview
    {
        public static SprintReviewReport Build(IReadOnlyList<WorkItem> items, TeamConfig team, DateTimeOffset now, int sprints)
        {
            var calendar = team.Sprint;
            var current = calendar.Current(now);
            var report = new SprintReviewReport { GeneratedAt = now, CurrentSprint = calendar.Name(current) };
            Func<WorkItem, int> pointsOf = s => { int p; return s.Meta != null && Sizing.IsValidPoints(s.Meta.Points) && int.TryParse(s.Meta.Points, out p) ? p : 0; };
            var stories = items.Where(i => i.Type == WorkItemType.Story && i.Meta != null).ToList();
            var theTeam = team.Teams.FirstOrDefault();
            report.TeamCapacity = theTeam == null ? 0 : team.TeamCapacity(theTeam);
            report.ConfiguredShare = theTeam == null ? 0 : theTeam.Committed;

            var first = Math.Max(calendar.FirstNumber, current - Math.Max(1, sprints));
            for (var n = first; n < current; n++)
            {
                var planned = stories.Where(s => calendar.NumberOf(s.Meta.Sprint) == n).ToList();
                if (planned.Count == 0) continue;
                var row = new SprintReviewRow
                {
                    Sprint = calendar.Name(n), Number = n, StoriesPlanned = planned.Count, PointsPlanned = planned.Sum(pointsOf), TeamCapacity = report.TeamCapacity,
                    StoriesDelivered = planned.Count(s => s.State == WorkItemState.Closed), PointsDelivered = planned.Where(s => s.State == WorkItemState.Closed).Sum(pointsOf),
                };
                var slipped = planned.Where(s => s.State == WorkItemState.Open).ToList();
                row.PointsSlipped = slipped.Sum(pointsOf);
                row.SlippedKeys.AddRange(slipped.Select(s => s.Key));
                foreach (var s in slipped) report.CarryOverKeys.Add(s.Key);
                row.DeliveryRate = row.PointsPlanned > 0 ? Math.Round((double)row.PointsDelivered / row.PointsPlanned, 2) : (double?)null;
                report.Sprints.Add(row);
            }

            var lastThree = report.Sprints.Skip(Math.Max(0, report.Sprints.Count - 3)).ToList();
            if (lastThree.Count == 0)
            {
                report.Notes.Add("No finished sprint has stories planned into it yet, so there is nothing to compare.");
                return report;
            }
            report.AverageDelivered = Math.Round(lastThree.Average(r => r.PointsDelivered), 1);
            var rated = lastThree.Where(r => r.DeliveryRate != null).ToList();
            report.AverageDeliveryRate = rated.Count == 0 ? (double?)null : Math.Round(rated.Average(r => r.DeliveryRate.Value), 2);
            if (report.TeamCapacity > 0)
            {
                var committed = (int)Math.Round(report.TeamCapacity * report.ConfiguredShare);
                var recommended = (int)Math.Round(report.AverageDelivered);
                report.RecommendedCommitPoints = committed > 0 ? Math.Min(recommended, committed) : recommended;
                report.RecommendedShare = Math.Round(Math.Max(0.1, Math.Min(1.0, (double)report.RecommendedCommitPoints / report.TeamCapacity)), 2);
                if (report.RecommendedCommitPoints < committed)
                    report.Notes.Add("The last " + lastThree.Count + " sprint(s) delivered " + report.AverageDelivered + " points on average against a commitment of " + committed + "; the next plan commits " + report.RecommendedCommitPoints + ".");
                else report.Notes.Add("Delivery matches the commitment; the next plan keeps " + committed + " points.");
            }
            if (report.CarryOverKeys.Count > 0) report.Notes.Add(report.CarryOverKeys.Count + " story(ies) slipped out of finished sprints; the next plan places them first.");
            return report;
        }
    }
}
