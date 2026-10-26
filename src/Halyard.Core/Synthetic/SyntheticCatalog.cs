using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Halyard.Core.Model;
using Halyard.Core.Schema;
using Halyard.Core.Serialization;

namespace Halyard.Core.Synthetic
{
    public sealed class SyntheticOptions
    {
        public int Themes { get; set; } = 2;
        public int EpicsPerTheme { get; set; } = 3;
        public int FeaturesPerEpic { get; set; } = 3;
        public int Stories { get; set; } = 50;
        public int Tasks { get; set; } = 24;
        public int Seed { get; set; } = 20261024;
        public string Area { get; set; } = "platform";
        public string SprintPrefix { get; set; } = "2026-S";
        public int FirstSprint { get; set; } = 21;
        public int SprintCount { get; set; } = 4;
        /// <summary>Calendar date of the first sprint (ISO date). Sprints are two weeks.</summary>
        public string SprintStart { get; set; } = "2026-09-21";
        /// <summary>Which sprint (offset from the first) is "now": earlier sprints are history, later ones are plan.</summary>
        public int CurrentSprintOffset { get; set; } = 2;
        /// <summary>Points planned into each sprint before the remaining stories stay in the unplanned backlog (matches config/team.json's demo team).</summary>
        public int SprintCapacity { get; set; } = 22;
        /// <summary>People ids used as owners; the defaults are the fictional Shellback example personas.</summary>
        public string[] People { get; set; } = { "jordan-brooks", "riley-chen", "casey-rivera", "morgan-vale" };
        /// <summary>When true, <see cref="DefectRate"/> of the items get deliberate gaps so the human review workflow has real work.</summary>
        public bool IncludeDefects { get; set; }
        /// <summary>Share of items given a defect when <see cref="IncludeDefects"/> is set. Default 0.2: 80% complete, 20% needing review.</summary>
        public double DefectRate { get; set; } = 0.20;

        public int Total { get { return Themes + Themes * EpicsPerTheme + Themes * EpicsPerTheme * FeaturesPerEpic + Stories + Tasks; } }

        /// <summary>Picks counts that add up to <paramref name="total"/> with the playbook's shape (few lanes, many stories).</summary>
        public static SyntheticOptions ForTotal(int total, int seed = 20261024)
        {
            if (total < 5) total = 5;
            var o = new SyntheticOptions { Seed = seed };
            o.Themes = Math.Max(1, (int)Math.Round(total * 0.02));
            o.EpicsPerTheme = total >= 50 ? 3 : 2;
            o.FeaturesPerEpic = total >= 50 ? 3 : 2;
            var containers = o.Themes + o.Themes * o.EpicsPerTheme + o.Themes * o.EpicsPerTheme * o.FeaturesPerEpic;
            while (containers > total * 0.5 && o.FeaturesPerEpic > 1)
            {
                o.FeaturesPerEpic--;
                containers = o.Themes + o.Themes * o.EpicsPerTheme + o.Themes * o.EpicsPerTheme * o.FeaturesPerEpic;
            }
            o.Tasks = Math.Max(0, (int)Math.Round(total * 0.24));
            o.Stories = Math.Max(o.Themes * o.EpicsPerTheme * o.FeaturesPerEpic, total - containers - o.Tasks);
            o.Tasks = Math.Max(0, total - containers - o.Stories);
            return o;
        }
    }

    /// <summary>One generated work item: everything needed to create it on any provider.</summary>
    public sealed class SyntheticWorkItem
    {
        public string Id { get; set; } = string.Empty;
        public WorkItemType Type { get; set; }
        public string? ParentId { get; set; }
        public string? ThemeId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Title { get { return Descriptions.FormatTitle(Type, Name); } }
        public List<string> Labels { get; set; } = new List<string>();
        public HalyardMeta Meta { get; set; } = new HalyardMeta();
        /// <summary>Narrative markdown without fence or mirror.</summary>
        public string Narrative { get; set; } = string.Empty;
        /// <summary>True for work finished in a past sprint (created closed on the provider).</summary>
        public bool Closed { get; set; }
        /// <summary>Full issue body: fence + mirror + narrative.</summary>
        public string Description { get { return Descriptions.Compose(Meta, Narrative); } }
        /// <summary>Injected defect kinds (see <see cref="SyntheticCatalog.DefectKinds"/>). Empty for a complete item.</summary>
        public List<string> Defects { get; set; } = new List<string>();
        /// <summary>Deficiency / topology codes the audit is expected to raise for this item.</summary>
        public List<string> ExpectedCodes { get; set; } = new List<string>();
        public bool NeedsHumanReview { get { return Defects.Count > 0; } }
    }

    /// <summary>
    /// Deterministic generator for a complete THEME → EPIC → FEATURE → STORY → TASK hierarchy using a
    /// vocabulary of plausible data-platform work. Same seed and options → byte-identical catalogue on
    /// every runtime, so fixtures can be regenerated and diffed.
    /// </summary>
    public static class SyntheticCatalog
    {
        public const string Version = "2.0";

        public static List<SyntheticWorkItem> Generate(SyntheticOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            var rng = new SeededRandom(options.Seed);
            var items = new List<SyntheticWorkItem>();
            var themes = Vocabulary.Themes.Take(Math.Min(options.Themes, Vocabulary.Themes.Length)).ToList();
            for (var i = themes.Count; i < options.Themes; i++)
            {
                var src = Vocabulary.Themes[i % Vocabulary.Themes.Length];
                themes.Add(new ThemeSeed(src.Name + " " + ToRoman(i / Vocabulary.Themes.Length + 2), src.Purpose, src.InScope, src.OutOfScope, src.Epics));
            }

            var features = new List<SyntheticWorkItem>();
            var featureSeeds = new Dictionary<SyntheticWorkItem, FeatureSeed>();
            var currentSprint = options.FirstSprint + Math.Max(0, Math.Min(options.SprintCount - 1, options.CurrentSprintOffset));
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var theme in themes)
            {
                var themeItem = new SyntheticWorkItem
                {
                    Type = WorkItemType.Theme,
                    Name = theme.Name,
                    Id = Unique(usedIds, Descriptions.MakeId(WorkItemType.Theme, theme.Name)),
                };
                themeItem.ThemeId = themeItem.Id;
                themeItem.Meta.Set("type", "THEME").Set("id", themeItem.Id).Set("theme", themeItem.Id).Set("state", "open").Set("status", "active").Set("area", options.Area);
                themeItem.Narrative = ThemeBody(theme);
                themeItem.Labels = BaseLabels(themeItem, options.Area);
                items.Add(themeItem);

                for (var e = 0; e < options.EpicsPerTheme; e++)
                {
                    var epic = theme.Epics[e % theme.Epics.Length];
                    var epicName = e < theme.Epics.Length ? epic.Name : epic.Name + " phase " + (e / theme.Epics.Length + 2);
                    var epicItem = new SyntheticWorkItem
                    {
                        Type = WorkItemType.Epic,
                        Name = epicName,
                        ParentId = themeItem.Id,
                        ThemeId = themeItem.Id,
                        Id = Unique(usedIds, Descriptions.MakeId(WorkItemType.Epic, epicName)),
                    };
                    epicItem.Meta.Set("type", "EPIC").Set("id", epicItem.Id).Set("theme", themeItem.Id).Set("parent", themeItem.Id).Set("state", "open").Set("area", options.Area)
                        .Set("milestone", Vocabulary.Milestones[rng.Next(Vocabulary.Milestones.Length)]);
                    epicItem.Narrative = EpicBody(epic, theme);
                    epicItem.Labels = BaseLabels(epicItem, options.Area);
                    items.Add(epicItem);

                    for (var f = 0; f < options.FeaturesPerEpic; f++)
                    {
                        var feat = epic.Features[f % epic.Features.Length];
                        var featName = f < epic.Features.Length ? feat.Name : feat.Name + " " + ToRoman(f / epic.Features.Length + 2);
                        var featItem = new SyntheticWorkItem
                        {
                            Type = WorkItemType.Feature,
                            Name = featName,
                            ParentId = epicItem.Id,
                            ThemeId = themeItem.Id,
                            Id = Unique(usedIds, Descriptions.MakeId(WorkItemType.Feature, featName)),
                        };
                        var effort = Sizing.EffortScale[rng.Next(Sizing.EffortScale.Length)];
                        featItem.Meta.Set("type", "FEATURE").Set("id", featItem.Id).Set("theme", themeItem.Id).Set("parent", epicItem.Id).Set("state", "open").Set("area", options.Area)
                            .Set("effort", effort).Set("artifact_level", "Proposal for Work");
                        if (rng.Next(2) == 0) featItem.Meta.Set("milestone", epicItem.Meta.Milestone);
                        featItem.Meta.Set("target", Vocabulary.TargetMonths[rng.Next(Vocabulary.TargetMonths.Length)]);
                        featItem.Meta.Set("owner", options.People.Length == 0 ? "unassigned" : options.People[rng.Next(options.People.Length)]);
                        var featureSkills = Vocabulary.SkillPool[features.Count % Vocabulary.SkillPool.Length];
                        featItem.Meta.Set("skills", string.Join(", ", featureSkills));
                        featItem.Narrative = FeatureBody(feat, epic, effort);
                        featItem.Labels = BaseLabels(featItem, options.Area);
                        items.Add(featItem);
                        features.Add(featItem);
                        featureSeeds[featItem] = feat;
                    }
                }
            }

            // Distribute stories across features: fill evenly, remainder to the first features.
            var stories = new List<SyntheticWorkItem>();
            var perFeature = features.Count == 0 ? 0 : options.Stories / features.Count;
            var remainder = features.Count == 0 ? 0 : options.Stories % features.Count;
            var sprintIndex = 0;
            var sprintLoad = new int[Math.Max(1, options.SprintCount)];
            for (var fi = 0; fi < features.Count; fi++)
            {
                var featItem = features[fi];
                var featSeed = featureSeeds[featItem];
                var count = perFeature + (fi < remainder ? 1 : 0);
                for (var s = 0; s < count; s++)
                {
                    var seed = featSeed.Stories[s % featSeed.Stories.Length];
                    var storyName = s < featSeed.Stories.Length ? seed.Name : seed.Name + " (" + (s / featSeed.Stories.Length + 2) + ")";
                    var story = new SyntheticWorkItem
                    {
                        Type = WorkItemType.Story,
                        Name = storyName,
                        ParentId = featItem.Id,
                        ThemeId = featItem.ThemeId,
                        Id = Unique(usedIds, Descriptions.MakeId(WorkItemType.Story, storyName)),
                    };
                    var points = Sizing.StoryPointScale[WeightedPointIndex(rng)];
                    // Sprints fill round-robin up to the capacity; what does not fit stays in the backlog with sprint TBD,
                    // which is what a sprint planner later has to place.
                    var slot = sprintIndex++ % sprintLoad.Length;
                    var planned = sprintLoad[slot] + points <= options.SprintCapacity + 2;
                    var sprintNumber = options.FirstSprint + slot;
                    story.Meta.Set("type", "STORY").Set("id", story.Id).Set("theme", featItem.ThemeId).Set("parent", featItem.Id).Set("state", "open").Set("area", options.Area)
                        .Set("points", points.ToString()).Set("sprint", planned ? options.SprintPrefix + sprintNumber.ToString("00") : Sizing.Tbd);
                    if (planned) sprintLoad[slot] += points;
                    if (featItem.Meta.Has("milestone") && rng.Next(3) == 0) story.Meta.Set("milestone", featItem.Meta.Milestone);
                    // Delivery history: past sprints are done (one in five carried over), the current sprint is partly done.
                    var done = planned && (sprintNumber < currentSprint ? rng.Next(5) != 0 : sprintNumber == currentSprint && rng.Next(5) < 2);
                    if (planned && sprintNumber < currentSprint && !done)
                    {
                        // Carried over: into the current sprint when it still has room, otherwise back to the backlog.
                        var currentSlot = currentSprint - options.FirstSprint;
                        if (currentSlot >= 0 && currentSlot < sprintLoad.Length && sprintLoad[currentSlot] + points <= options.SprintCapacity + 2) { sprintLoad[currentSlot] += points; story.Meta.Set("sprint", options.SprintPrefix + currentSprint.ToString("00")); }
                        else story.Meta.Set("sprint", Sizing.Tbd);
                    }
                    if (done) { story.Closed = true; story.Meta.Set("state", "closed").Set("status", "done"); }
                    if (planned && sprintNumber <= currentSprint && options.People.Length > 0) story.Meta.Set("owner", options.People[rng.Next(options.People.Length)]);
                    var pool = (featItem.Meta.Get("skills") ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
                    if (pool.Length > 0) story.Meta.Set("skills", rng.Next(3) == 0 && pool.Length > 1 ? pool[0] + ", " + pool[1] : pool[rng.Next(pool.Length)]);
                    story.Narrative = StoryBody(seed, featItem, points);
                    story.Labels = BaseLabels(story, options.Area);
                    items.Add(story);
                    stories.Add(story);
                }
            }

            // Tasks: one per story, round-robin, until the requested count is reached.
            var taskVerbs = Vocabulary.TaskVerbs;
            for (var t = 0; t < options.Tasks && stories.Count > 0; t++)
            {
                var story = stories[t % stories.Count];
                var verb = taskVerbs[(t / Math.Max(1, stories.Count)) % taskVerbs.Length];
                var taskName = verb + " " + Descriptions.ShortName(story.Name);
                var task = new SyntheticWorkItem
                {
                    Type = WorkItemType.Task,
                    Name = taskName,
                    ParentId = story.Id,
                    ThemeId = story.ThemeId,
                    Id = Unique(usedIds, Descriptions.MakeId(WorkItemType.Task, taskName)),
                };
                task.Meta.Set("type", "TASK").Set("id", task.Id).Set("theme", story.ThemeId).Set("parent", story.Id).Set("state", story.Closed ? "closed" : "open").Set("area", options.Area);
                task.Closed = story.Closed;
                task.Narrative = TaskBody(verb, story);
                task.Labels = BaseLabels(task, options.Area);
                items.Add(task);
            }

            InjectBlockers(items, rng, options, currentSprint);
            if (options.IncludeDefects) InjectDefects(items, rng, options.DefectRate);
            return items;
        }

        /// <summary>A few open stories in the current or next sprint are blocked (ops or dependency), with an age.</summary>
        private static void InjectBlockers(List<SyntheticWorkItem> items, SeededRandom rng, SyntheticOptions options, int currentSprint)
        {
            var open = items.Where(i => i.Type == WorkItemType.Story && !i.Closed && i.Meta.Has("sprint")).ToList();
            if (open.Count < 6) return;
            var count = Math.Max(2, Math.Min(4, open.Count / 12));
            DateTimeOffset start;
            if (!DateTimeOffset.TryParse(options.SprintStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out start)) start = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
            var currentStart = start.AddDays((currentSprint - options.FirstSprint) * 14.0);
            for (var k = 0; k < count; k++)
            {
                var story = open[rng.Next(open.Count)];
                if (story.Labels.Any(l => l.StartsWith("blocked::"))) continue;
                var kind = k % 2 == 0 ? "ops" : "dependency";
                story.Labels.Add("blocked::" + kind);
                story.Meta.Set("blocked_since", currentStart.AddDays(-(2 + k * 5)).ToString("yyyy-MM-dd"));
                story.Meta.Set("blocked_reason", kind == "ops" ? Vocabulary.OpsBlockers[k % Vocabulary.OpsBlockers.Length] : Vocabulary.DependencyBlockers[k % Vocabulary.DependencyBlockers.Length]);
            }
        }

        private static int WeightedPointIndex(SeededRandom rng)
        {
            // 1:10% 2:20% 3:35% 5:25% 8:10%
            var r = rng.Next(100);
            if (r < 10) return 0;
            if (r < 30) return 1;
            if (r < 65) return 2;
            if (r < 90) return 3;
            return 4;
        }

        /// <summary>
        /// The kinds of gap the generator can inject. Each maps to deficiency or topology codes the audit must
        /// surface and to the proposal path that repairs it (sizes, parents, refinement, metadata repair).
        /// </summary>
        public static readonly string[] DefectKinds =
        {
            "story_no_acceptance_criteria",   // refinement
            "story_no_user_need",             // refinement
            "story_no_tasks",                 // refinement
            "story_no_points",                // size proposal
            "story_points_tbd",               // size proposal
            "story_points_13",                // size proposal (clamp/split)
            "story_no_parent",                // parent proposal
            "story_parent_unknown",           // parent proposal (declared id does not exist)
            "story_parent_wrong_level",       // topology finding
            "story_no_sprint",                // metadata repair
            "story_effort_band",              // metadata repair
            "story_no_meta",                  // metadata repair (labels only)
            "feature_no_effort",              // effort proposal
            "feature_effort_tbd",             // effort proposal
            "feature_has_points",             // metadata repair
            "feature_no_done_when",           // refinement
            "feature_no_parent",              // parent proposal
            "epic_no_success_picture",        // refinement
            "epic_no_outcome",                // refinement
            "task_no_parent",                 // parent proposal
            "theme_no_scope",                 // refinement
        };

        private static void InjectDefects(List<SyntheticWorkItem> items, SeededRandom rng, double rate)
        {
            if (rate <= 0) return;
            if (rate > 0.5) rate = 0.5;
            var target = Math.Max(1, (int)Math.Round(items.Count * rate));
            // Quotas keep the review queue representative: mostly stories, some features and tasks, one or two
            // containers. Within each type the picks are a deterministic shuffle.
            var chosen = new List<SyntheticWorkItem>();
            var quotas = new List<KeyValuePair<WorkItemType, int>>
            {
                new KeyValuePair<WorkItemType, int>(WorkItemType.Theme, target >= 10 ? 1 : 0),
                new KeyValuePair<WorkItemType, int>(WorkItemType.Epic, target >= 5 ? 1 : 0),
                new KeyValuePair<WorkItemType, int>(WorkItemType.Feature, (int)Math.Round(target * 0.2)),
                new KeyValuePair<WorkItemType, int>(WorkItemType.Task, (int)Math.Round(target * 0.1)),
            };
            foreach (var q in quotas) chosen.AddRange(Pick(items.Where(i => i.Type == q.Key).ToList(), q.Value, rng));
            chosen.AddRange(Pick(items.Where(i => i.Type == WorkItemType.Story).ToList(), Math.Max(0, target - chosen.Count), rng));
            if (chosen.Count < target) chosen.AddRange(Pick(items.Where(i => !chosen.Contains(i) && i.Type != WorkItemType.Theme).ToList(), target - chosen.Count, rng));
            var kindIndex = new Dictionary<WorkItemType, int>();
            foreach (var item in chosen)
            {
                var kinds = DefectKinds.Where(k => k.StartsWith(WorkItemTypes.ToToken(item.Type).ToLowerInvariant() + "_")).ToArray();
                if (kinds.Length == 0) continue;
                int idx;
                kindIndex.TryGetValue(item.Type, out idx);
                var kind = kinds[idx % kinds.Length];
                kindIndex[item.Type] = idx + 1;
                Apply(item, kind, items, rng);
                // Every fifth defective story also loses its points, so some items need two kinds of help.
                if (item.Type == WorkItemType.Story && idx % 5 == 4 && item.Meta.Has("points") && !item.Defects.Contains("story_no_points"))
                    Apply(item, "story_no_points", items, rng);
            }
        }

        private static List<SyntheticWorkItem> Pick(List<SyntheticWorkItem> pool, int count, SeededRandom rng)
        {
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp;
            }
            return pool.Take(Math.Max(0, count)).ToList();
        }

        private static void Apply(SyntheticWorkItem item, string kind, List<SyntheticWorkItem> all, SeededRandom rng)
        {
            switch (kind)
            {
                case "story_no_acceptance_criteria":
                    item.Narrative = RemoveSection(item.Narrative, "Acceptance criteria");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryMissingAcceptanceCriteria); break;
                case "story_no_user_need":
                    item.Narrative = RemoveSection(item.Narrative, "User need");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryMissingUserNeed); break;
                case "story_no_tasks":
                    // No task checklist anywhere: the acceptance criteria become plain bullets, as hand-written tickets usually are.
                    item.Narrative = RemoveSection(item.Narrative, "Tasks").Replace("- [ ] ", "- ");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryMissingTasks); break;
                case "story_no_points":
                    item.Meta.Remove("points"); item.Narrative = RemoveSection(item.Narrative, "Sizing");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryMissingPoints); break;
                case "story_points_tbd":
                    item.Meta.Set("points", Sizing.Tbd); item.Narrative = ReplaceSizing(item.Narrative, "- **Story points:** TBD");
                    item.Defects.Add(kind); break; // valid but unsized: an estimate candidate, not a deficiency
                case "story_points_13":
                    item.Meta.Set("points", "13"); item.Narrative = ReplaceSizing(item.Narrative, "- **Story points:** 13");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryInvalidPoints); break;
                case "story_no_parent":
                case "feature_no_parent":
                case "task_no_parent":
                    item.Meta.Remove("parent"); item.ParentId = null;
                    item.Narrative = RemoveSection(item.Narrative, item.Type == WorkItemType.Task ? "Parent Story" : item.Type == WorkItemType.Story ? "Parent Feature" : "Parent Epic");
                    item.Defects.Add(kind);
                    item.ExpectedCodes.Add(item.Type == WorkItemType.Task ? Deficiencies.TaskMissingParent : item.Type == WorkItemType.Story ? Deficiencies.StoryMissingParent : Deficiencies.FeatureMissingParent);
                    break;
                case "story_parent_unknown":
                    item.Meta.Set("parent", "FEATURE-retired-capability"); item.ParentId = null;
                    item.Defects.Add(kind); item.ExpectedCodes.Add("PARENT_UNRESOLVED"); break;
                case "story_parent_wrong_level":
                    {
                        var epic = all.Where(i => i.Type == WorkItemType.Epic).ToList();
                        if (epic.Count == 0) return;
                        var e = epic[rng.Next(epic.Count)];
                        item.Meta.Set("parent", e.Id); item.ParentId = e.Id;
                        item.Defects.Add(kind); item.ExpectedCodes.Add("PARENT_TYPE_MISMATCH"); break;
                    }
                case "story_no_sprint":
                    item.Meta.Remove("sprint"); item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryMissingSprint); break;
                case "story_effort_band":
                    item.Meta.Set("effort", "Medium"); item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.StoryHasEffort); break;
                case "story_no_meta":
                    item.Meta = new HalyardMeta(); // no fence at all; type:: and theme:: labels still classify it
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.MissingMeta); break;
                case "feature_no_effort":
                    item.Meta.Remove("effort"); item.Narrative = RemoveSection(item.Narrative, "Sizing");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.FeatureMissingEffort); break;
                case "feature_effort_tbd":
                    item.Meta.Set("effort", Sizing.Tbd); item.Narrative = ReplaceSizing(item.Narrative, "- **Effort:** TBD");
                    item.Defects.Add(kind); break;
                case "feature_has_points":
                    item.Meta.Set("points", "5"); item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.FeatureHasPoints); break;
                case "feature_no_done_when":
                    item.Narrative = RemoveSection(item.Narrative, "Definition of done");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.FeatureMissingDoneWhen); break;
                case "epic_no_success_picture":
                    item.Narrative = RemoveSection(item.Narrative, "Success picture");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.EpicMissingSuccessPicture); break;
                case "epic_no_outcome":
                    item.Narrative = RemoveSection(item.Narrative, "Outcome").Replace("Outcome", "Result");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.EpicMissingOutcome); break;
                case "theme_no_scope":
                    item.Narrative = RemoveSection(RemoveSection(item.Narrative, "In scope"), "Out of scope");
                    item.Defects.Add(kind); item.ExpectedCodes.Add(Deficiencies.ThemeMissingScope); break;
            }
        }

        /// <summary>Removes a <c>## Heading</c> section (heading and body up to the next heading).</summary>
        public static string RemoveSection(string narrative, string heading)
        {
            var m = System.Text.RegularExpressions.Regex.Match(narrative, @"(?im)^[ \t]*##[ \t]*" + System.Text.RegularExpressions.Regex.Escape(heading) + @"[ \t]*\r?\n(?:(?!^#).*\r?\n?)*");
            if (!m.Success) return narrative;
            return (narrative.Substring(0, m.Index) + narrative.Substring(m.Index + m.Length)).Trim();
        }

        private static string ReplaceSizing(string narrative, string line)
        {
            var without = RemoveSection(narrative, "Sizing");
            return without + "\n\n## Sizing\n" + line + "\n";
        }

        private static List<string> BaseLabels(SyntheticWorkItem item, string area)
        {
            var labels = new List<string>
            {
                Safety.Labels.Managed,
                Safety.Labels.Synthetic,
                WorkItemTypes.ToLabel(item.Type),
                Safety.Labels.AreaPrefix + area,
            };
            if (!string.IsNullOrEmpty(item.ThemeId)) labels.Add(Safety.Labels.ThemePrefix + item.ThemeId);
            return labels;
        }

        private static string Unique(HashSet<string> used, string id)
        {
            var candidate = id;
            var n = 2;
            while (!used.Add(candidate)) candidate = id + "-" + n++;
            return candidate;
        }

        private static string ThemeBody(ThemeSeed theme)
        {
            var sb = new StringBuilder();
            sb.Append("## Purpose\n").Append(theme.Purpose).Append("\n\n");
            sb.Append("## In scope\n").Append(Bullets(theme.InScope)).Append("\n\n");
            sb.Append("## Out of scope\n").Append(Bullets(theme.OutOfScope)).Append("\n\n");
            sb.Append("## Owner\nPortfolio lead for ").Append(theme.Name).Append(" (role, not a named individual).\n\n");
            sb.Append("## Child Epics\n").Append(Bullets(theme.Epics.Select(e => "EPIC :: " + e.Name))).Append("\n\n");
            sb.Append("> Synthetic work item generated by Halyard for sandbox review. No real system, person or commitment is described.");
            return sb.ToString();
        }

        private static string EpicBody(EpicSeed epic, ThemeSeed theme)
        {
            var sb = new StringBuilder();
            sb.Append("## Outcome\n").Append(epic.Outcome).Append("\n\n");
            sb.Append("## Success picture\n").Append(Checklist(epic.SuccessPicture)).Append("\n\n");
            sb.Append("## Theme\n").Append(theme.Name).Append("\n\n");
            sb.Append("## Owner\nEpic steward (role).\n\n");
            sb.Append("## Child Features\n").Append(Bullets(epic.Features.Select(f => "FEATURE :: " + f.Name))).Append("\n\n");
            sb.Append("## Risks / dependencies\n").Append(Bullets(epic.Risks)).Append("\n\n");
            sb.Append("> Synthetic work item generated by Halyard for sandbox review.");
            return sb.ToString();
        }

        private static string FeatureBody(FeatureSeed feat, EpicSeed epic, string effort)
        {
            var sb = new StringBuilder();
            sb.Append("## Description\n").Append(feat.Description).Append("\n\n");
            sb.Append("## Parent Epic\nEPIC :: ").Append(epic.Name).Append("\n\n");
            sb.Append("## Proposal for Work\n");
            sb.Append("**Problem / outcome:** ").Append(feat.Problem).Append("\n\n");
            sb.Append("**Approach:** ").Append(feat.Approach).Append("\n\n");
            sb.Append("**Scope in:** ").Append(string.Join("; ", feat.ScopeIn)).Append("\n\n");
            sb.Append("**Scope out:** Real credentials, production data, and systems outside the sandbox.\n\n");
            sb.Append("## Definition of done\n").Append(Checklist(feat.DoneWhen)).Append("\n\n");
            sb.Append("## Sizing\n- **Effort:** ").Append(effort).Append(" — ").Append(EffortRationale(effort)).Append("\n\n");
            sb.Append("## Child Stories\n").Append(Bullets(feat.Stories.Select(s => "STORY :: " + s.Name))).Append("\n\n");
            sb.Append("> Synthetic work item generated by Halyard for sandbox review.");
            return sb.ToString();
        }

        private static string StoryBody(StorySeed story, SyntheticWorkItem feature, int points)
        {
            var sb = new StringBuilder();
            sb.Append("## User need\nAs a ").Append(story.Role).Append(", I want to ").Append(story.Goal).Append(" so that ").Append(story.Benefit).Append(".\n\n");
            sb.Append("## Acceptance criteria\n").Append(Checklist(story.Criteria)).Append("\n\n");
            sb.Append("## Tasks\n").Append(Checklist(story.Tasks)).Append("\n\n");
            sb.Append("## Parent Feature\n").Append(feature.Id).Append(" — ").Append(feature.Name).Append("\n\n");
            sb.Append("## Sizing\n- **Story points:** ").Append(points).Append("\n\n");
            sb.Append("## Constraints\nSynthetic data only. Nothing in this Story touches a real credential, workstation policy or production system.\n\n");
            sb.Append("> Synthetic work item generated by Halyard for sandbox review.");
            return sb.ToString();
        }

        private static string TaskBody(string verb, SyntheticWorkItem story)
        {
            var sb = new StringBuilder();
            sb.Append("## Objective\n").Append(verb).Append(" the narrow slice described by the parent Story, using an isolated synthetic fixture.\n\n");
            sb.Append("## Parent Story\n").Append(story.Id).Append(" — ").Append(story.Name).Append("\n\n");
            sb.Append("## Procedure\n- Re-read the parent Story's acceptance criteria.\n- Record the fixture inputs used.\n- Attach the evidence to the parent Story.\n\n");
            sb.Append("## Done when\n- [ ] The parent Story's acceptance check for this slice is demonstrated with one positive and one negative example.\n\n");
            sb.Append("> Standalone Task records are optional; a checklist inside the Story is the default. This one exists to exercise the full hierarchy.");
            return sb.ToString();
        }

        private static string EffortRationale(string effort)
        {
            switch (effort)
            {
                case "Small": return "about one sprint of focused work.";
                case "Large": return "multi-sprint; watch for split pressure and a hidden Epic.";
                default: return "two to three sprints across several Stories.";
            }
        }

        private static string Bullets(IEnumerable<string> lines)
        {
            return string.Join("\n", lines.Select(l => "- " + l));
        }

        private static string Checklist(IEnumerable<string> lines)
        {
            return string.Join("\n", lines.Select(l => "- [ ] " + l));
        }

        private static string ToRoman(int n)
        {
            var map = new[] { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
            return n >= 0 && n < map.Length ? map[n] : n.ToString();
        }

        // ---- persistence ----

        public static string ToJson(IEnumerable<SyntheticWorkItem> items, SyntheticOptions options)
        {
            var list = items.ToList();
            var counts = list.GroupBy(i => WorkItemTypes.ToToken(i.Type)).ToDictionary(g => g.Key, g => g.Count());
            var review = list.Count(i => i.NeedsHumanReview);
            var root = new JObject
            {
                ["version"] = Version,
                ["synthetic"] = true,
                ["warning"] = "SYNTHETIC DATA. No real ticket, person, date, credential or commitment is described.",
                ["generated_with"] = JObject.FromObject(options),
                ["counts"] = JObject.FromObject(counts),
                ["complete"] = list.Count - review,
                ["needs_review"] = review,
                ["items"] = new JArray(list.Select(i => new JObject
                {
                    ["id"] = i.Id,
                    ["type"] = WorkItemTypes.ToToken(i.Type),
                    ["parent"] = i.ParentId,
                    ["theme"] = i.ThemeId,
                    ["name"] = i.Name,
                    ["title"] = i.Title,
                    ["labels"] = new JArray(i.Labels),
                    ["closed"] = i.Closed,
                    ["meta"] = JObject.FromObject(i.Meta.ToDictionary()),
                    ["narrative"] = i.Narrative,
                    ["defects"] = new JArray(i.Defects),
                    ["expected_codes"] = new JArray(i.ExpectedCodes),
                })),
            };
            return root.ToString(Formatting.Indented);
        }

        public static List<SyntheticWorkItem> FromJson(string json)
        {
            var root = JObject.Parse(json);
            var list = new List<SyntheticWorkItem>();
            foreach (var j in root["items"] as JArray ?? new JArray())
            {
                var item = new SyntheticWorkItem
                {
                    Id = (string?)j["id"] ?? string.Empty,
                    Type = WorkItemTypes.Parse((string?)j["type"]),
                    ParentId = (string?)j["parent"],
                    ThemeId = (string?)j["theme"],
                    Name = (string?)j["name"] ?? string.Empty,
                    Narrative = (string?)j["narrative"] ?? string.Empty,
                };
                item.Labels = (j["labels"] as JArray ?? new JArray()).Select(l => (string?)l ?? string.Empty).Where(l => l.Length > 0).ToList();
                item.Closed = (bool?)j["closed"] ?? false;
                var meta = j["meta"] as JObject;
                if (meta != null) foreach (var p in meta.Properties()) item.Meta.Set(p.Name, (string?)p.Value);
                item.Defects = (j["defects"] as JArray ?? new JArray()).Select(d => (string?)d ?? string.Empty).ToList();
                item.ExpectedCodes = (j["expected_codes"] as JArray ?? new JArray()).Select(d => (string?)d ?? string.Empty).ToList();
                list.Add(item);
            }
            return list;
        }

        public static List<SyntheticWorkItem> Load(string path)
        {
            return FromJson(File.ReadAllText(path));
        }

        public static void Save(string path, IEnumerable<SyntheticWorkItem> items, SyntheticOptions options)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            File.WriteAllText(path, ToJson(items, options), new UTF8Encoding(false));
        }

        /// <summary>Converts catalogue entries into in-memory work items (for offline analysis and tests).</summary>
        public static List<WorkItem> ToWorkItems(IEnumerable<SyntheticWorkItem> items, RepositoryRef repository)
        {
            var list = new List<WorkItem>();
            long n = 1;
            foreach (var s in items)
            {
                var w = new WorkItem(repository, n++) { Title = s.Title, Body = s.Meta.Count == 0 ? s.Narrative : s.Description, Url = "synthetic://" + s.Id, State = s.Closed ? WorkItemState.Closed : WorkItemState.Open };
                w.Labels.AddRange(s.Labels);
                list.Add(w);
            }
            return list;
        }
    }

    /// <summary>xorshift32: identical sequences on .NET Framework and .NET, unlike System.Random across versions.</summary>
    public sealed class SeededRandom
    {
        private uint _state;

        public SeededRandom(int seed)
        {
            _state = (uint)seed == 0 ? 0x9E3779B9u : (uint)seed;
        }

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException("maxExclusive");
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return (int)(x % (uint)maxExclusive);
        }
    }
}
