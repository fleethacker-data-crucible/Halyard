using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cascade.Core.Model;
using Cascade.Core.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cascade.Core.Synthetic
{
    public sealed class SyntheticOptions
    {
        public int Themes { get; set; } = 2;
        public int EpicsPerTheme { get; set; } = 3;
        public int FeaturesPerEpic { get; set; } = 3;
        public int Stories { get; set; } = 50;
        public int Tasks { get; set; } = 24;
        public int Seed { get; set; } = 20261024;
        public string Area { get; set; } = "data-crucible";
        public string SprintPrefix { get; set; } = "2026-S";
        public int FirstSprint { get; set; } = 21;
        public int SprintCount { get; set; } = 4;
        /// <summary>When true, a small share of items gets deliberate DoR gaps so audits have something to show.</summary>
        public bool IncludeDefects { get; set; }

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
        public CascadeMeta Meta { get; set; } = new CascadeMeta();
        /// <summary>Narrative markdown without fence or mirror.</summary>
        public string Narrative { get; set; } = string.Empty;
        /// <summary>Full issue body: fence + mirror + narrative.</summary>
        public string Description { get { return Descriptions.Compose(Meta, Narrative); } }
        public List<string> Defects { get; set; } = new List<string>();
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
                    var sprint = options.SprintPrefix + (options.FirstSprint + (sprintIndex++ % Math.Max(1, options.SprintCount))).ToString("00");
                    story.Meta.Set("type", "STORY").Set("id", story.Id).Set("theme", featItem.ThemeId).Set("parent", featItem.Id).Set("state", "open").Set("area", options.Area)
                        .Set("points", points.ToString()).Set("sprint", sprint);
                    if (featItem.Meta.Has("milestone") && rng.Next(3) == 0) story.Meta.Set("milestone", featItem.Meta.Milestone);
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
                task.Meta.Set("type", "TASK").Set("id", task.Id).Set("theme", story.ThemeId).Set("parent", story.Id).Set("state", "open").Set("area", options.Area);
                task.Narrative = TaskBody(verb, story);
                task.Labels = BaseLabels(task, options.Area);
                items.Add(task);
            }

            if (options.IncludeDefects) InjectDefects(items, rng);
            return items;
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

        private static void InjectDefects(List<SyntheticWorkItem> items, SeededRandom rng)
        {
            var stories = items.Where(i => i.Type == WorkItemType.Story).ToList();
            var features = items.Where(i => i.Type == WorkItemType.Feature).ToList();
            if (stories.Count > 3)
            {
                var s1 = stories[rng.Next(stories.Count)];
                s1.Meta.Remove("parent");
                s1.Defects.Add(Deficiencies.StoryMissingParent);
                var s2 = stories[rng.Next(stories.Count)];
                if (s2 != s1)
                {
                    s2.Meta.Set("points", "13");
                    s2.Defects.Add(Deficiencies.StoryInvalidPoints);
                }
                var s3 = stories[rng.Next(stories.Count)];
                if (s3 != s1 && s3 != s2)
                {
                    s3.Narrative = s3.Narrative.Replace("## Acceptance criteria", "## Notes");
                    s3.Defects.Add(Deficiencies.StoryMissingAcceptanceCriteria);
                }
            }
            if (features.Count > 1)
            {
                var f1 = features[rng.Next(features.Count)];
                f1.Meta.Set("effort", "TBD");
                f1.Meta.Set("points", "5");
                f1.Defects.Add(Deficiencies.FeatureHasPoints);
            }
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
            sb.Append("> Synthetic work item generated by Cascade for sandbox review. No real system, person or commitment is described.");
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
            sb.Append("> Synthetic work item generated by Cascade for sandbox review.");
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
            sb.Append("> Synthetic work item generated by Cascade for sandbox review.");
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
            sb.Append("> Synthetic work item generated by Cascade for sandbox review.");
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
            var root = new JObject
            {
                ["version"] = Version,
                ["synthetic"] = true,
                ["warning"] = "SYNTHETIC DATA. No real ticket, person, date, credential or commitment is described.",
                ["generated_with"] = JObject.FromObject(options),
                ["counts"] = JObject.FromObject(counts),
                ["items"] = new JArray(list.Select(i => new JObject
                {
                    ["id"] = i.Id,
                    ["type"] = WorkItemTypes.ToToken(i.Type),
                    ["parent"] = i.ParentId,
                    ["theme"] = i.ThemeId,
                    ["name"] = i.Name,
                    ["title"] = i.Title,
                    ["labels"] = new JArray(i.Labels),
                    ["meta"] = JObject.FromObject(i.Meta.ToDictionary()),
                    ["narrative"] = i.Narrative,
                    ["defects"] = new JArray(i.Defects),
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
                var meta = j["meta"] as JObject;
                if (meta != null) foreach (var p in meta.Properties()) item.Meta.Set(p.Name, (string?)p.Value);
                item.Defects = (j["defects"] as JArray ?? new JArray()).Select(d => (string?)d ?? string.Empty).ToList();
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
                var w = new WorkItem(repository, n++) { Title = s.Title, Body = s.Description, Url = "synthetic://" + s.Id };
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
