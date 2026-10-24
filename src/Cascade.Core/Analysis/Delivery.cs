using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cascade.Core.Model;
using Cascade.Core.Planning;
using Cascade.Core.Schema;

namespace Cascade.Core.Analysis
{
    public enum FeatureStatus
    {
        OnTrack = 1,
        AtRisk = 2,
        Parked = 3,
        Done = 4,
    }

    public sealed class FeatureProgress
    {
        public string Key { get; set; } = string.Empty;
        public long Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string CanonicalId { get; set; } = string.Empty;
        public string? Epic { get; set; }
        public string? Theme { get; set; }
        public string? Owner { get; set; }
        public string? Target { get; set; }
        public int Stories { get; set; }
        public int StoriesDone { get; set; }
        public int Points { get; set; }
        public int PointsDone { get; set; }
        public int Blocked { get; set; }
        public int Percent { get { return Points > 0 ? (int)Math.Round(100.0 * PointsDone / Points) : Stories > 0 ? (int)Math.Round(100.0 * StoriesDone / Stories) : 0; } }
        public FeatureStatus Status { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? Url { get; set; }
    }

    public sealed class SprintVelocity
    {
        public string Sprint { get; set; } = string.Empty;
        public int Number { get; set; }
        public int PointsDone { get; set; }
        public int PointsPlanned { get; set; }
        public int StoriesDone { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsPast { get; set; }
    }

    public sealed class Blocker
    {
        public string Key { get; set; } = string.Empty;
        public long Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = "other";
        public string? Reason { get; set; }
        public string? Owner { get; set; }
        public DateTimeOffset? Since { get; set; }
        public int AgeDays { get; set; }
        public int AgeBusinessDays { get; set; }
        public bool Escalated { get; set; }
        public string? Impact { get; set; }
        public string? Url { get; set; }
    }

    public sealed class ThemeProgress
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Points { get; set; }
        public int PointsDone { get; set; }
        public int Features { get; set; }
        public int FeaturesDone { get; set; }
        public int Percent { get { return Points > 0 ? (int)Math.Round(100.0 * PointsDone / Points) : 0; } }
    }

    public sealed class CapacityView
    {
        public int TeamCapacity { get; set; }
        public int PlannedPoints { get; set; }
        public int DonePoints { get; set; }
        public int UsedPercent { get { return TeamCapacity > 0 ? (int)Math.Round(100.0 * PlannedPoints / TeamCapacity) : 0; } }
        public double Committed { get; set; }
        public double Allocated { get; set; }
        public double Experiment { get; set; }
        public int Teams { get; set; }
        public int People { get; set; }
        public bool Configured { get; set; }
    }

    public sealed class ReadyBacklog
    {
        public int Stories { get; set; }
        public int Points { get; set; }
        public int NotReady { get; set; }
        public double AverageVelocity { get; set; }
        public bool Healthy { get { return AverageVelocity <= 0 ? Stories > 0 : Points >= 1.5 * AverageVelocity; } }
    }

    /// <summary>The leadership view: progress, velocity, readiness, capacity and blockers, from one snapshot.</summary>
    public sealed class DeliveryReport
    {
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? Repository { get; set; }
        public string CurrentSprint { get; set; } = string.Empty;
        public int CurrentSprintNumber { get; set; }
        public DateTimeOffset SprintStart { get; set; }
        public DateTimeOffset SprintEnd { get; set; }
        public List<FeatureProgress> Features { get; set; } = new List<FeatureProgress>();
        public List<ThemeProgress> Themes { get; set; } = new List<ThemeProgress>();
        public List<SprintVelocity> Velocity { get; set; } = new List<SprintVelocity>();
        public double AverageVelocity { get; set; }
        public List<Blocker> Blockers { get; set; } = new List<Blocker>();
        public ReadyBacklog Ready { get; set; } = new ReadyBacklog();
        public CapacityView Capacity { get; set; } = new CapacityView();
        public int FeaturesOnTrack { get { return Features.Count(f => f.Status == FeatureStatus.OnTrack || f.Status == FeatureStatus.Done); } }
        public int FeaturesActive { get { return Features.Count(f => f.Status != FeatureStatus.Done); } }
        public List<string> Notes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Builds the leadership view from a snapshot and the team configuration. Rules are deliberately simple and
    /// stated in each item's <c>Reason</c>, so a reader can check them against the backlog.
    /// </summary>
    public static class Delivery
    {
        public const int EscalationBusinessDays = 5;

        public static DeliveryReport Build(IReadOnlyList<WorkItem> items, TeamConfig team, DateTimeOffset now, string? repository = null)
        {
            var report = new DeliveryReport { GeneratedAt = now, Repository = repository };
            var calendar = team.Sprint;
            var current = calendar.Current(now);
            report.CurrentSprintNumber = current;
            report.CurrentSprint = calendar.Name(current);
            report.SprintStart = calendar.StartOf(current);
            report.SprintEnd = calendar.EndOf(current);

            var audit = TopologyAudit.Run(items, items, null);
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            var parentOf = audit.ParentEdges;
            Func<WorkItem, WorkItem?> parent = w => { string? pk; WorkItem? p; return parentOf.TryGetValue(w.Key, out pk) && pk != null && byKey.TryGetValue(pk, out p) ? p : null; };
            Func<WorkItem, WorkItemType, WorkItem?> ancestor = (w, t) =>
            {
                var cursor = parent(w);
                var guard = 0;
                while (cursor != null && guard++ < 10) { if (cursor.Type == t) return cursor; cursor = parent(cursor); }
                return null;
            };

            var stories = items.Where(i => i.Type == WorkItemType.Story).ToList();
            Func<WorkItem, int> pointsOf = s => { int p; return s.Meta != null && Sizing.IsValidPoints(s.Meta.Points) && int.TryParse(s.Meta.Points, out p) ? p : 0; };
            Func<WorkItem, int?> sprintOf = s => s.Meta == null ? null : calendar.NumberOf(s.Meta.Sprint);
            Func<WorkItem, bool> done = s => s.State == WorkItemState.Closed;

            // ---- velocity: closed points by sprint, last six sprints up to the current one ----
            var firstShown = Math.Max(calendar.FirstNumber, current - 5);
            for (var n = firstShown; n <= current; n++)
            {
                var inSprint = stories.Where(s => sprintOf(s) == n).ToList();
                report.Velocity.Add(new SprintVelocity
                {
                    Sprint = calendar.Name(n), Number = n,
                    PointsDone = inSprint.Where(done).Sum(pointsOf), PointsPlanned = inSprint.Sum(pointsOf), StoriesDone = inSprint.Count(done),
                    IsCurrent = n == current, IsPast = n < current,
                });
            }
            var completed = report.Velocity.Where(v => v.IsPast).ToList();
            var lastThree = completed.Skip(Math.Max(0, completed.Count - 3)).ToList();
            report.AverageVelocity = lastThree.Count == 0 ? 0 : Math.Round(lastThree.Average(v => v.PointsDone), 1);

            // ---- blockers ----
            foreach (var s in items.Where(i => i.State == WorkItemState.Open && i.Labels.Any(l => l.StartsWith("blocked", StringComparison.OrdinalIgnoreCase))))
            {
                var label = s.Labels.First(l => l.StartsWith("blocked", StringComparison.OrdinalIgnoreCase));
                var type = label.Contains("::") ? label.Substring(label.IndexOf("::", StringComparison.Ordinal) + 2) : "other";
                DateTimeOffset since;
                var sinceText = s.Meta == null ? null : s.Meta.Get("blocked_since");
                var hasSince = DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out since);
                if (!hasSince && s.UpdatedAt != null) { since = s.UpdatedAt.Value; hasSince = true; }
                var age = hasSince ? Math.Max(0, (int)Math.Floor((now - since).TotalDays)) : 0;
                var business = hasSince ? BusinessDays(since, now) : 0;
                var feature = s.Type == WorkItemType.Feature ? s : ancestor(s, WorkItemType.Feature);
                report.Blockers.Add(new Blocker
                {
                    Key = s.Key, Number = s.Number, Title = Descriptions.ShortName(s.Title), Type = type,
                    Reason = s.Meta == null ? null : s.Meta.Get("blocked_reason"), Owner = s.Meta == null ? null : s.Meta.Get("owner"),
                    Since = hasSince ? since : (DateTimeOffset?)null, AgeDays = age, AgeBusinessDays = business, Escalated = business > EscalationBusinessDays,
                    Impact = feature == null ? null : Descriptions.ShortName(feature.Title), Url = s.Url,
                });
            }
            report.Blockers = report.Blockers.OrderByDescending(b => b.AgeBusinessDays).ToList();
            var blockedKeys = new HashSet<string>(report.Blockers.Select(b => b.Key), StringComparer.OrdinalIgnoreCase);

            // ---- features ----
            foreach (var f in items.Where(i => i.Type == WorkItemType.Feature))
            {
                var children = stories.Where(s => { var p = parent(s); return p != null && p.Key == f.Key; }).ToList();
                var epic = ancestor(f, WorkItemType.Epic);
                var theme = ancestor(f, WorkItemType.Theme);
                var fp = new FeatureProgress
                {
                    Key = f.Key, Number = f.Number, Title = Descriptions.ShortName(f.Title), CanonicalId = f.CanonicalId, Url = f.Url,
                    Epic = epic == null ? null : Descriptions.ShortName(epic.Title), Theme = theme == null ? null : Descriptions.ShortName(theme.Title),
                    Owner = f.Meta == null ? null : f.Meta.Get("owner"), Target = f.Meta == null ? null : f.Meta.Get("target"),
                    Stories = children.Count, StoriesDone = children.Count(done), Points = children.Sum(pointsOf), PointsDone = children.Where(done).Sum(pointsOf),
                    Blocked = children.Count(c => blockedKeys.Contains(c.Key)),
                };
                var hasFutureWork = children.Any(c => !done(c) && (sprintOf(c) == null || sprintOf(c) >= current));
                DateTimeOffset targetDate;
                var hasTarget = TryParseMonth(fp.Target, out targetDate);
                if (f.State == WorkItemState.Closed || (children.Count > 0 && children.All(done))) { fp.Status = FeatureStatus.Done; fp.Reason = "every story is closed"; }
                else if (children.Count == 0) { fp.Status = FeatureStatus.Parked; fp.Reason = "no stories yet"; }
                else if (fp.Blocked > 0) { fp.Status = FeatureStatus.AtRisk; fp.Reason = fp.Blocked + " blocked stor" + (fp.Blocked == 1 ? "y" : "ies"); }
                else if (hasTarget && targetDate < report.SprintEnd) { fp.Status = FeatureStatus.AtRisk; fp.Reason = "target " + fp.Target + " is inside or before the current sprint at " + fp.Percent + "%"; }
                else if (!hasFutureWork) { fp.Status = FeatureStatus.Parked; fp.Reason = "open stories are not planned into any sprint"; }
                else { fp.Status = FeatureStatus.OnTrack; fp.Reason = "work planned, nothing blocked"; }
                report.Features.Add(fp);
            }
            report.Features = report.Features.OrderBy(f => f.Status == FeatureStatus.Done ? 1 : 0).ThenBy(f => f.Target ?? "9999").ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToList();

            // ---- themes ----
            foreach (var t in items.Where(i => i.Type == WorkItemType.Theme))
            {
                var tp = new ThemeProgress { Key = t.Key, Title = Descriptions.ShortName(t.Title) };
                foreach (var s in stories) { var th = ancestor(s, WorkItemType.Theme); if (th != null && th.Key == t.Key) { tp.Points += pointsOf(s); if (done(s)) tp.PointsDone += pointsOf(s); } }
                foreach (var f in report.Features) { var fw = byKey[f.Key]; var th = ancestor(fw, WorkItemType.Theme); if (th != null && th.Key == t.Key) { tp.Features++; if (f.Status == FeatureStatus.Done) tp.FeaturesDone++; } }
                report.Themes.Add(tp);
            }

            // ---- ready backlog: open stories beyond the current sprint that meet the definition of ready ----
            var upcoming = stories.Where(s => !done(s) && (sprintOf(s) == null || sprintOf(s) > current)).ToList();
            foreach (var s in upcoming)
            {
                var ready = pointsOf(s) > 0 && Deficiencies.Evaluate(s).Count == 0;
                if (ready) { report.Ready.Stories++; report.Ready.Points += pointsOf(s); } else report.Ready.NotReady++;
            }
            report.Ready.AverageVelocity = report.AverageVelocity;

            // ---- capacity: this sprint's planned points against the configured teams ----
            var cap = report.Capacity;
            cap.Configured = team.Teams.Count > 0;
            cap.Teams = team.Teams.Count;
            cap.People = team.People.Count;
            cap.TeamCapacity = team.Teams.Sum(t => team.TeamCapacity(t));
            var inCurrent = stories.Where(s => sprintOf(s) == current).ToList();
            cap.PlannedPoints = inCurrent.Sum(pointsOf);
            cap.DonePoints = inCurrent.Where(done).Sum(pointsOf);
            if (team.Teams.Count > 0)
            {
                var weight = team.Teams.Sum(t => Math.Max(1, team.TeamCapacity(t)));
                cap.Committed = Math.Round(team.Teams.Sum(t => t.Committed * Math.Max(1, team.TeamCapacity(t))) / weight, 2);
                cap.Allocated = Math.Round(team.Teams.Sum(t => t.Allocated * Math.Max(1, team.TeamCapacity(t))) / weight, 2);
                cap.Experiment = Math.Round(team.Teams.Sum(t => t.Experiment * Math.Max(1, team.TeamCapacity(t))) / weight, 2);
            }
            if (!cap.Configured) report.Notes.Add("No teams in config/team.json; capacity is not assessed.");
            if (completed.Count == 0) report.Notes.Add("No completed sprint in the snapshot yet; velocity is the current sprint only.");
            return report;
        }

        public static bool TryParseMonth(string? text, out DateTimeOffset end)
        {
            end = default(DateTimeOffset);
            if (string.IsNullOrWhiteSpace(text)) return false;
            DateTimeOffset d;
            if (DateTimeOffset.TryParseExact(text!.Trim(), new[] { "yyyy-MM", "yyyy-MM-dd", "MMM yyyy", "MMMM yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d))
            {
                end = text.Length <= 7 || !text.Contains("-") || text.Count(c => c == '-') == 1 ? new DateTimeOffset(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month), 23, 59, 59, TimeSpan.Zero) : d;
                return true;
            }
            return false;
        }

        public static int BusinessDays(DateTimeOffset from, DateTimeOffset to)
        {
            if (to <= from) return 0;
            var days = 0;
            var cursor = from.Date;
            while (cursor < to.Date)
            {
                cursor = cursor.AddDays(1);
                if (cursor.DayOfWeek != DayOfWeek.Saturday && cursor.DayOfWeek != DayOfWeek.Sunday) days++;
            }
            return days;
        }
    }
}
