using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Analysis;
using Halyard.Core.Model;
using Halyard.Core.Schema;

namespace Halyard.Core.Planning
{
    /// <summary>One item the scrum master wants a person to look at, with the reason in words.</summary>
    public sealed class ScrumItem
    {
        public string Key { get; set; }
        public long Number { get; set; }
        public string Title { get; set; }
        public string Owner { get; set; }
        public string Reason { get; set; }
        public int AgeDays { get; set; }
        public string Url { get; set; }

        public ScrumItem()
        {
            Key = string.Empty;
            Title = string.Empty;
            Reason = string.Empty;
        }
    }

    /// <summary>A ceremony on the sprint calendar and what Halyard brings to it.</summary>
    public sealed class Ceremony
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTimeOffset At { get; set; }
        public string Agenda { get; set; }
        /// <summary>The Halyard command that prepares it.</summary>
        public string Command { get; set; }

        public Ceremony()
        {
            Id = string.Empty;
            Name = string.Empty;
            Agenda = string.Empty;
            Command = string.Empty;
        }
    }

    /// <summary>A next best action: what a product owner would do now, and the command that starts it.</summary>
    public sealed class NextAction
    {
        public string Title { get; set; }
        public string Why { get; set; }
        public string Command { get; set; }
        /// <summary>1 is most urgent.</summary>
        public int Priority { get; set; }

        public NextAction()
        {
            Title = string.Empty;
            Why = string.Empty;
            Command = string.Empty;
        }
    }

    /// <summary>
    /// The scrum master's view of the current sprint: burn against the calendar, the standup list (blockers, stale work, work
    /// nobody owns), risks, the ceremonies coming up with an agenda each, retro prompts from the last sprints, the refinement
    /// agenda and the next best actions. Deterministic: no model, nothing written.
    /// </summary>
    public sealed class ScrumBrief
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public string CurrentSprint { get; set; }
        public DateTimeOffset SprintStart { get; set; }
        public DateTimeOffset SprintEnd { get; set; }
        public int DaysElapsed { get; set; }
        public int DaysLeft { get; set; }
        public int PointsPlanned { get; set; }
        public int PointsDone { get; set; }
        /// <summary>Points that a straight-line burn would have closed by now.</summary>
        public int PointsExpected { get; set; }
        public bool OnPace { get; set; }
        public List<ScrumItem> Blockers { get; set; }
        public List<ScrumItem> Stale { get; set; }
        public List<ScrumItem> Unowned { get; set; }
        public List<ScrumItem> Refinement { get; set; }
        public List<string> Risks { get; set; }
        public List<string> RetroPrompts { get; set; }
        public List<Ceremony> Ceremonies { get; set; }
        public List<NextAction> Actions { get; set; }
        /// <summary>The brief as plain sentences, for a voice or a chat message.</summary>
        public string Spoken { get; set; }

        public ScrumBrief()
        {
            CurrentSprint = string.Empty;
            Blockers = new List<ScrumItem>();
            Stale = new List<ScrumItem>();
            Unowned = new List<ScrumItem>();
            Refinement = new List<ScrumItem>();
            Risks = new List<string>();
            RetroPrompts = new List<string>();
            Ceremonies = new List<Ceremony>();
            Actions = new List<NextAction>();
            Spoken = string.Empty;
        }
    }

    public static class ScrumMaster
    {
        /// <summary>An open story in the sprint that has not changed for this many days is raised at standup.</summary>
        public const int StaleDays = 3;
        public const int MaxListed = 10;

        public static ScrumBrief Build(IReadOnlyList<WorkItem> items, ReviewReport review, TeamConfig team, DateTimeOffset now)
        {
            if (items == null) throw new ArgumentNullException("items");
            if (team == null) throw new ArgumentNullException("team");
            var delivery = Delivery.Build(items, team, now);
            var calendar = team.Sprint;
            var current = delivery.CurrentSprintNumber;
            var brief = new ScrumBrief
            {
                GeneratedAt = now,
                CurrentSprint = delivery.CurrentSprint,
                SprintStart = delivery.SprintStart,
                SprintEnd = delivery.SprintEnd,
            };
            var length = Math.Max(1, (int)Math.Round((delivery.SprintEnd - delivery.SprintStart).TotalDays));
            brief.DaysElapsed = Math.Max(0, Math.Min(length, (int)Math.Floor((now - delivery.SprintStart).TotalDays)));
            brief.DaysLeft = Math.Max(0, length - brief.DaysElapsed);

            var sprintStories = items.Where(i => i.Type == WorkItemType.Story && i.Meta != null && calendar.NumberOf(i.Meta.Sprint) == current).ToList();
            Func<WorkItem, int> points = s => { int p; return s.Meta != null && int.TryParse(s.Meta.Points, out p) && p > 0 ? p : 0; };
            brief.PointsPlanned = sprintStories.Sum(points);
            brief.PointsDone = sprintStories.Where(s => s.State == WorkItemState.Closed).Sum(points);
            brief.PointsExpected = (int)Math.Round(brief.PointsPlanned * (double)brief.DaysElapsed / length);
            // A little slack: a sprint is on pace while it is within 15% (or two points) of the straight line.
            brief.OnPace = brief.PointsPlanned == 0 || brief.PointsDone + Math.Max(2, (int)Math.Round(0.15 * brief.PointsPlanned)) >= brief.PointsExpected;

            foreach (var b in delivery.Blockers.OrderByDescending(x => x.Escalated).ThenByDescending(x => x.AgeDays).Take(MaxListed))
            {
                brief.Blockers.Add(new ScrumItem
                {
                    Key = b.Key, Number = b.Number, Title = b.Title, Owner = b.Owner, AgeDays = b.AgeDays, Url = b.Url,
                    Reason = (b.Escalated ? "Escalate: " : string.Empty) + (string.IsNullOrWhiteSpace(b.Reason) ? "blocked (" + b.Type + ")" : b.Reason),
                });
            }

            var blockedKeys = new HashSet<string>(delivery.Blockers.Select(b => b.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var s in sprintStories.Where(x => x.State == WorkItemState.Open && !blockedKeys.Contains(x.Key) && x.UpdatedAt != null)
                .Select(x => new { Item = x, Age = (int)Math.Floor((now - x.UpdatedAt.Value).TotalDays) })
                .Where(x => x.Age >= StaleDays).OrderByDescending(x => x.Age).Take(MaxListed))
            {
                brief.Stale.Add(Row(s.Item, "No change for " + s.Age + " days.", s.Age));
            }
            foreach (var s in sprintStories.Where(x => x.State == WorkItemState.Open && string.IsNullOrWhiteSpace(x.Meta.Get("owner"))).Take(MaxListed))
                brief.Unowned.Add(Row(s, "In the sprint with no owner.", 0));

            if (review != null)
            {
                foreach (var r in review.Items.Where(x => x.State == WorkItemState.Open && (x.Type == WorkItemType.Story || x.Type == WorkItemType.Feature) && x.NeedsReview)
                    .OrderBy(x => x.Sprint == null ? 1 : 0).ThenBy(x => x.Type == WorkItemType.Feature ? 0 : 1).ThenBy(x => x.Number).Take(MaxListed))
                {
                    var why = r.Deficiencies.Concat(r.MissingMeta.Select(m => "missing " + m)).Take(3).ToList();
                    brief.Refinement.Add(new ScrumItem { Key = r.Key, Number = r.Number, Title = Descriptions.ShortName(r.Title), Url = r.Url, Reason = why.Count == 0 ? "Has review findings." : string.Join("; ", why) });
                }
            }

            // ---- risks ----
            if (!brief.OnPace) brief.Risks.Add("Burn is behind: " + brief.PointsDone + " of " + brief.PointsPlanned + " points closed, " + brief.PointsExpected + " expected by today.");
            if (delivery.Capacity.Configured && delivery.Capacity.TeamCapacity > 0 && delivery.Capacity.PlannedPoints > delivery.Capacity.TeamCapacity)
                brief.Risks.Add("The sprint is over capacity: " + delivery.Capacity.PlannedPoints + " points planned against " + delivery.Capacity.TeamCapacity + ".");
            if (!delivery.Ready.Healthy) brief.Risks.Add("The ready backlog is thin: " + delivery.Ready.Stories + " ready stories, " + delivery.Ready.Points + " points, against a velocity of " + delivery.AverageVelocity + ".");
            var escalated = delivery.Blockers.Count(b => b.Escalated);
            if (escalated > 0) brief.Risks.Add(escalated + " blocker" + (escalated == 1 ? " has" : "s have") + " been open more than " + Delivery.EscalationBusinessDays + " business days.");
            var atRisk = delivery.Features.Where(f => f.Status == FeatureStatus.AtRisk).ToList();
            if (atRisk.Count > 0) brief.Risks.Add(atRisk.Count + " feature" + (atRisk.Count == 1 ? " is" : "s are") + " at risk: " + string.Join(", ", atRisk.Take(3).Select(f => Descriptions.ShortName(f.Title))) + ".");

            // ---- retro prompts from the finished sprints ----
            var history = SprintReview.Build(items, team, now, 3);
            var last = history.Sprints.LastOrDefault();
            if (last != null)
            {
                if (last.DeliveryRate.HasValue) brief.RetroPrompts.Add(last.Sprint + " delivered " + (int)Math.Round(100 * last.DeliveryRate.Value) + "% of what was planned (" + last.PointsDelivered + " of " + last.PointsPlanned + " points). What got in the way?");
                if (last.SlippedKeys.Count > 0) brief.RetroPrompts.Add(last.SlippedKeys.Count + " stor" + (last.SlippedKeys.Count == 1 ? "y" : "ies") + " slipped out of " + last.Sprint + ". Were they too big, blocked, or started late?");
            }
            if (history.RecommendedCommitPoints > 0) brief.RetroPrompts.Add("History suggests committing about " + history.RecommendedCommitPoints + " points next sprint.");
            if (brief.Blockers.Count > 0) brief.RetroPrompts.Add("Which of the " + brief.Blockers.Count + " blockers could the team have removed itself?");

            // ---- ceremonies ----
            var start = delivery.SprintStart;
            var end = delivery.SprintEnd;
            var nextDay = now.Date.AddDays(1);
            while (nextDay.DayOfWeek == DayOfWeek.Saturday || nextDay.DayOfWeek == DayOfWeek.Sunday) nextDay = nextDay.AddDays(1);
            brief.Ceremonies.Add(new Ceremony { Id = "standup", Name = "Daily standup", At = new DateTimeOffset(nextDay, now.Offset).AddHours(9), Agenda = brief.Blockers.Count + " blockers, " + brief.Stale.Count + " stale, " + brief.Unowned.Count + " unowned.", Command = "halyard scrum brief" });
            var refinement = start.AddDays(Math.Max(1, length / 2));
            if (refinement > now) brief.Ceremonies.Add(new Ceremony { Id = "refinement", Name = "Backlog refinement", At = refinement, Agenda = brief.Refinement.Count + " items need work before they are ready.", Command = "halyard goal ready" });
            brief.Ceremonies.Add(new Ceremony { Id = "review", Name = "Sprint review", At = end.AddDays(-1), Agenda = "Planned " + brief.PointsPlanned + ", done " + brief.PointsDone + " so far; feature progress for stakeholders.", Command = "halyard leadership report" });
            brief.Ceremonies.Add(new Ceremony { Id = "retro", Name = "Retrospective", At = end.AddDays(-1).AddHours(2), Agenda = brief.RetroPrompts.Count + " prompts from the last sprints.", Command = "halyard sprint review" });
            brief.Ceremonies.Add(new Ceremony { Id = "planning", Name = "Sprint planning", At = end, Agenda = "Fill " + calendar.Name(current + 1) + " from ready stories within capacity.", Command = "halyard plan preview" });
            brief.Ceremonies = brief.Ceremonies.OrderBy(c => c.At).ToList();

            // ---- next best actions ----
            var actions = brief.Actions;
            if (escalated > 0) actions.Add(new NextAction { Priority = 1, Title = "Escalate the oldest blockers", Why = escalated + " blockers are past the escalation line.", Command = "halyard leadership show" });
            if (review != null && review.Summary.NeedsReview > 0) actions.Add(new NextAction { Priority = 2, Title = "Get the backlog ready", Why = review.Summary.NeedsReview + " items fail their definition of ready.", Command = "halyard goal ready" });
            if (!delivery.Ready.Healthy) actions.Add(new NextAction { Priority = 2, Title = "Refine more stories", Why = "The ready backlog will not fill the next sprint.", Command = "halyard refine run" });
            if (brief.Unowned.Count > 0) actions.Add(new NextAction { Priority = 3, Title = "Assign owners to sprint work", Why = brief.Unowned.Count + " sprint stories have no owner.", Command = "halyard plan preview" });
            if (brief.DaysLeft <= 2) actions.Add(new NextAction { Priority = 3, Title = "Plan the next sprint", Why = "The sprint ends in " + brief.DaysLeft + " days.", Command = "halyard plan propose" });
            if (actions.Count == 0) actions.Add(new NextAction { Priority = 4, Title = "Share the leadership summary", Why = "The backlog is in order.", Command = "halyard leadership notify" });
            brief.Actions = actions.OrderBy(a => a.Priority).ToList();

            brief.Spoken = Speak(brief);
            return brief;
        }

        private static ScrumItem Row(WorkItem w, string reason, int age)
        {
            return new ScrumItem { Key = w.Key, Number = w.Number, Title = Descriptions.ShortName(w.Title), Owner = w.Meta == null ? null : w.Meta.Get("owner"), Reason = reason, AgeDays = age, Url = w.Url };
        }

        /// <summary>The brief as a few plain sentences: no symbols, no tables.</summary>
        public static string Speak(ScrumBrief b)
        {
            var parts = new List<string>();
            parts.Add(b.CurrentSprint + ", day " + b.DaysElapsed + ", " + b.DaysLeft + " days left.");
            parts.Add(b.PointsDone + " of " + b.PointsPlanned + " points are done" + (b.OnPace ? ", which is on pace." : ", behind the " + b.PointsExpected + " expected by now."));
            if (b.Blockers.Count == 0) parts.Add("No blockers.");
            else parts.Add(b.Blockers.Count + " blocker" + (b.Blockers.Count == 1 ? "" : "s") + ", oldest " + b.Blockers.Max(x => x.AgeDays) + " days: " + b.Blockers[0].Title + ".");
            if (b.Stale.Count > 0) parts.Add(b.Stale.Count + " sprint stor" + (b.Stale.Count == 1 ? "y has" : "ies have") + " not moved in " + StaleDays + " days or more.");
            if (b.Unowned.Count > 0) parts.Add(b.Unowned.Count + " sprint stor" + (b.Unowned.Count == 1 ? "y has" : "ies have") + " no owner.");
            if (b.Actions.Count > 0) parts.Add("Next: " + b.Actions[0].Title.ToLowerInvariant() + ".");
            return string.Join(" ", parts);
        }
    }
}
