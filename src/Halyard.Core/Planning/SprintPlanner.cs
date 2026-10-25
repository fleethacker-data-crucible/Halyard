using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Analysis;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Schema;

namespace Halyard.Core.Planning
{
    public sealed class PlanOptions
    {
        /// <summary>How many sprints after the current one to plan into.</summary>
        public int SprintsAhead { get; set; } = 2;
        /// <summary>Top up the current sprint's remaining capacity before planning the next ones.</summary>
        public bool IncludeCurrent { get; set; } = true;
        /// <summary>Share of team capacity that may be planned (the rest is allocated/experiment time). Null = the team's committed share.</summary>
        public double? CommitShare { get; set; }
        public int MaxStories { get; set; } = 200;
        /// <summary>Stories not meeting the definition of ready are skipped unless this is set.</summary>
        public bool RequireReady { get; set; } = true;
        public HashSet<long>? OnlyNumbers { get; set; }
        public HashSet<string>? OnlyKeys { get; set; }
    }

    public sealed class PlannedStory
    {
        public string Key { get; set; } = string.Empty;
        public long Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string CanonicalId { get; set; } = string.Empty;
        public int Points { get; set; }
        public string Sprint { get; set; } = string.Empty;
        public string? Owner { get; set; }
        public string? Feature { get; set; }
        public string? Target { get; set; }
        public List<string> Skills { get; set; } = new List<string>();
        public List<string> MissingSkills { get; set; } = new List<string>();
        public double MatchScore { get; set; }
        public int Priority { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool AlreadyPlanned { get; set; }
    }

    public sealed class PersonLoad
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Capacity { get; set; }
        public int Existing { get; set; }
        public int Proposed { get; set; }
        public int Remaining { get { return Capacity - Existing - Proposed; } }
        public string Availability { get; set; } = "unknown";
    }

    public sealed class SprintPlan
    {
        public string Sprint { get; set; } = string.Empty;
        public int Number { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
        public bool IsCurrent { get; set; }
        public int Capacity { get; set; }
        public int CommitCapacity { get; set; }
        public int Existing { get; set; }
        public int Proposed { get; set; }
        public int Remaining { get { return CommitCapacity - Existing - Proposed; } }
        public List<PlannedStory> Stories { get; set; } = new List<PlannedStory>();
        public List<PersonLoad> People { get; set; } = new List<PersonLoad>();
    }

    public sealed class PlanResult
    {
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<SprintPlan> Sprints { get; set; } = new List<SprintPlan>();
        public List<PlannedStory> Unplaced { get; set; } = new List<PlannedStory>();
        public List<string> SkipReasons { get; set; } = new List<string>();
        /// <summary>Skills needed by planned work that nobody on the team has: the link back to Shellback growth plans.</summary>
        public Dictionary<string, int> SkillGaps { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Candidates { get; set; }
        public int Placed { get { return Sprints.Sum(s => s.Stories.Count(x => !x.AlreadyPlanned)); } }
        public List<string> Notes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Deterministic sprint planner. Orders ready stories by feature target and progress, fills the current
    /// and next sprints up to the committed share of team capacity, and picks the owner with the best skill
    /// match and remaining capacity. Every decision carries its reason; the result is a proposal batch a
    /// human approves, never a write.
    /// </summary>
    public static class SprintPlanner
    {
        public static PlanResult Plan(IReadOnlyList<WorkItem> items, ReviewReport report, TeamConfig team, DateTimeOffset now, PlanOptions options)
        {
            options = options ?? new PlanOptions();
            var result = new PlanResult { GeneratedAt = now };
            var calendar = team.Sprint;
            var current = calendar.Current(now);
            var byKey = items.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
            var parentOf = report.Items.ToDictionary(i => i.Key, i => i.ResolvedParentKey, StringComparer.OrdinalIgnoreCase);
            Func<WorkItem, WorkItem?> parent = w => { string? pk; WorkItem? p; return parentOf.TryGetValue(w.Key, out pk) && pk != null && byKey.TryGetValue(pk, out p) ? p : null; };
            Func<WorkItem, int> pointsOf = s => { int p; return s.Meta != null && Sizing.IsValidPoints(s.Meta.Points) && int.TryParse(s.Meta.Points, out p) ? p : 0; };
            Func<WorkItem, int?> sprintOf = s => s.Meta == null ? null : calendar.NumberOf(s.Meta.Sprint);
            var stories = items.Where(i => i.Type == WorkItemType.Story).ToList();
            var deficiencies = report.Items.ToDictionary(i => i.Key, i => i.Deficiencies, StringComparer.OrdinalIgnoreCase);

            if (team.Teams.Count == 0) { result.Notes.Add("No teams in config/team.json; add a team with pointsPerSprint to plan."); return result; }
            var theTeam = team.Teams[0];
            var members = team.People.Where(p => string.Equals(p.Team, theTeam.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var share = options.CommitShare ?? theTeam.Committed;

            // Sprint frames with what is already planned into them.
            var first = options.IncludeCurrent ? current : current + 1;
            for (var n = first; n <= current + options.SprintsAhead; n++)
            {
                var frame = new SprintPlan { Sprint = calendar.Name(n), Number = n, Start = calendar.StartOf(n), End = calendar.EndOf(n), IsCurrent = n == current, Capacity = team.TeamCapacity(theTeam) };
                frame.CommitCapacity = (int)Math.Round(frame.Capacity * share);
                var existing = stories.Where(s => sprintOf(s) == n && s.State == WorkItemState.Open).ToList();
                frame.Existing = existing.Sum(pointsOf);
                foreach (var m in members)
                {
                    var load = new PersonLoad { Id = m.Id, Name = m.Name, Capacity = (int)Math.Round(team.PersonCapacity(m) * share * (m.Availability == "limited" ? 0.5 : 1.0)), Availability = m.Availability };
                    load.Existing = existing.Where(s => s.Meta != null && string.Equals(s.Meta.Get("owner"), m.Id, StringComparison.OrdinalIgnoreCase)).Sum(pointsOf);
                    frame.People.Add(load);
                }
                foreach (var s in existing)
                    frame.Stories.Add(new PlannedStory { Key = s.Key, Number = s.Number, Title = Descriptions.ShortName(s.Title), CanonicalId = s.CanonicalId, Points = pointsOf(s), Sprint = frame.Sprint, Owner = s.Meta == null ? null : s.Meta.Get("owner"), AlreadyPlanned = true, Skills = SkillTagger.CurrentSkills(s).ToList(), Reason = "already planned" });
                result.Sprints.Add(frame);
            }

            // Candidates: open, unplanned (or sprint TBD / past-and-not-done), sized, ready, not blocked.
            var candidates = new List<KeyValuePair<WorkItem, int>>();
            foreach (var s in stories)
            {
                if (s.State != WorkItemState.Open) continue;
                if (options.OnlyNumbers != null && !options.OnlyNumbers.Contains(s.Number)) continue;
                if (options.OnlyKeys != null && !options.OnlyKeys.Contains(s.Key)) continue;
                var sprint = sprintOf(s);
                if (sprint != null && sprint >= current) continue; // already in the plan window or later
                var pts = pointsOf(s);
                if (pts <= 0) { result.SkipReasons.Add(s.CanonicalId + ": unsized"); continue; }
                if (s.Labels.Any(l => l.StartsWith("blocked", StringComparison.OrdinalIgnoreCase))) { result.SkipReasons.Add(s.CanonicalId + ": blocked"); continue; }
                List<string>? gaps;
                if (options.RequireReady && deficiencies.TryGetValue(s.Key, out gaps) && gaps.Count > 0) { result.SkipReasons.Add(s.CanonicalId + ": not ready (" + string.Join(", ", gaps) + ")"); continue; }
                candidates.Add(new KeyValuePair<WorkItem, int>(s, pts));
            }
            result.Candidates = candidates.Count;

            // Priority: earlier feature target first, then features already started, then catalogue order.
            var featureProgress = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in stories.Where(x => x.State == WorkItemState.Closed)) { var f = parent(s); if (f != null) { int c; featureProgress.TryGetValue(f.Key, out c); featureProgress[f.Key] = c + 1; } }
            var ordered = candidates.Select((kv, index) =>
            {
                var feature = parent(kv.Key);
                var target = feature == null || feature.Meta == null ? null : feature.Meta.Get("target");
                DateTimeOffset targetDate;
                var hasTarget = Delivery.TryParseMonth(target, out targetDate);
                int started;
                var startedCount = feature != null && featureProgress.TryGetValue(feature.Key, out started) ? started : 0;
                // Days to the feature's target dominate; a feature already in progress wins over one not started when targets are within a week.
                var priority = (hasTarget ? (int)Math.Max(0, (targetDate - now).TotalDays) : 9000) * 10 + (startedCount > 0 ? 0 : 70);
                return new { Story = kv.Key, Points = kv.Value, Feature = feature, Target = target, Started = startedCount, Priority = priority, Index = index };
            }).OrderBy(x => x.Priority).ThenBy(x => x.Index).Take(options.MaxStories).ToList();

            var rank = 0;
            foreach (var c in ordered)
            {
                rank++;
                var skills = SkillTagger.CurrentSkills(c.Story).ToList();
                var planned = new PlannedStory
                {
                    Key = c.Story.Key, Number = c.Story.Number, Title = Descriptions.ShortName(c.Story.Title), CanonicalId = c.Story.CanonicalId, Points = c.Points,
                    Feature = c.Feature == null ? null : Descriptions.ShortName(c.Feature.Title), Target = c.Target, Skills = skills, Priority = rank,
                };
                var why = new List<string>();
                why.Add(c.Target != null ? "feature target " + c.Target : "no feature target");
                if (c.Started > 0) why.Add("feature already has " + c.Started + " done stor" + (c.Started == 1 ? "y" : "ies"));
                SprintPlan? frame = null;
                foreach (var f in result.Sprints) if (f.Remaining >= c.Points) { frame = f; break; }
                if (frame == null)
                {
                    planned.Reason = string.Join("; ", why) + "; no sprint in the next " + options.SprintsAhead + " has " + c.Points + " points left";
                    result.Unplaced.Add(planned);
                    continue;
                }
                planned.Sprint = frame.Sprint;
                // Owner: best skill match with room; skills nobody has are recorded as gaps.
                var missing = skills.Where(sk => !members.Any(m => m.Has(sk))).ToList();
                planned.MissingSkills = missing;
                foreach (var g in missing) { int n; result.SkillGaps.TryGetValue(g, out n); result.SkillGaps[g] = n + 1; }
                PersonLoad? best = null;
                var bestScore = -1.0;
                foreach (var load in frame.People)
                {
                    if (load.Remaining < c.Points) continue;
                    var person = members.First(m => m.Id == load.Id);
                    var score = skills.Count == 0 ? 1.0 : skills.Sum(sk => person.Has(sk) ? 1.0 + person.Confidence(sk) / 4.0 : 0.0);
                    if (skills.Count > 0 && score <= 0) continue; // never hand work to someone with none of its skills
                    score += load.Remaining / 100.0; // tie-break: spread load
                    if (score > bestScore) { bestScore = score; best = load; }
                }
                if (best != null)
                {
                    best.Proposed += c.Points;
                    planned.Owner = best.Id;
                    planned.MatchScore = Math.Round(bestScore, 2);
                    var matched = skills.Where(sk => members.First(m => m.Id == best.Id).Has(sk)).ToList();
                    why.Add(matched.Count > 0 ? best.Name + " has " + string.Join(", ", matched.Select(sk => sk + " (confidence " + members.First(m => m.Id == best.Id).Confidence(sk) + ")")) : best.Name + " has room (" + best.Remaining + " pts left)");
                }
                else why.Add(skills.Count == 0 ? "nobody has room this sprint" : "nobody with " + string.Join("/", skills) + " has room; left unassigned");
                if (missing.Count > 0) why.Add("skill gap: " + string.Join(", ", missing) + " (nobody on the team)");
                frame.Proposed += c.Points;
                why.Add(frame.Sprint + " load " + (frame.Existing + frame.Proposed) + "/" + frame.CommitCapacity + " committed pts");
                planned.Reason = string.Join("; ", why);
                frame.Stories.Add(planned);
            }
            if (result.Unplaced.Count > 0) result.Notes.Add(result.Unplaced.Count + " ready stories do not fit in the next " + options.SprintsAhead + " sprint(s) at " + (int)Math.Round(share * 100) + "% commitment.");
            if (result.SkillGaps.Count > 0) result.Notes.Add("Skills nobody on the team has: " + string.Join(", ", result.SkillGaps.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " ×" + kv.Value)) + ". Consider Shellback growth plans or hiring.");
            return result;
        }

        /// <summary>Turns a plan into a proposal batch: one SprintPlan proposal per newly placed story.</summary>
        public static ProposalBatch ToProposals(PlanResult plan, RepositoryRef target)
        {
            var batch = new ProposalBatch { Kind = ProposalKind.SprintPlan, Target = target.Key, Source = "deterministic planner", Considered = plan.Candidates };
            batch.Skipped = plan.SkipReasons.Count + plan.Unplaced.Count;
            batch.SkipReasons.AddRange(plan.SkipReasons);
            batch.SkipReasons.AddRange(plan.Unplaced.Select(u => u.CanonicalId + ": " + u.Reason));
            foreach (var frame in plan.Sprints)
            {
                foreach (var s in frame.Stories.Where(x => !x.AlreadyPlanned))
                {
                    var meta = new Dictionary<string, string?> { { "sprint", s.Sprint } };
                    if (s.Owner != null) meta["owner"] = s.Owner;
                    batch.Proposals.Add(new Proposal
                    {
                        BatchId = batch.Id, Kind = ProposalKind.SprintPlan, ItemKey = s.Key, ItemNumber = s.Number, ItemCanonicalId = s.CanonicalId, ItemTitle = "STORY :: " + s.Title,
                        CurrentValue = "unplanned", ProposedValue = s.Sprint + (s.Owner != null ? " · " + s.Owner : " · unassigned"), ProposedMeta = meta,
                        Confidence = s.MissingSkills.Count > 0 ? "low" : s.Owner == null ? "low" : s.Skills.Count == 0 ? "medium" : "high",
                        Reason = s.Reason, Source = "deterministic planner",
                    });
                }
            }
            foreach (var n in plan.Notes) batch.Warnings.Add(n);
            return batch;
        }
    }
}
