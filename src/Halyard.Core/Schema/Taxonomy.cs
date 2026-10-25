using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Halyard.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Halyard.Core.Schema
{
    public sealed class TaxonomyTheme
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string Status { get; set; } = "Active";
    }

    /// <summary>
    /// Required meta keys by type plus the standing theme catalogue. Loaded from <c>config/taxonomy.json</c>
    /// when present; the built-in defaults match the playbook so the core works without the file.
    /// </summary>
    public sealed class Taxonomy
    {
        public string Version { get; private set; } = "1.0";
        public Dictionary<WorkItemType, string[]> RequiredMeta { get; private set; }
        public List<TaxonomyTheme> Themes { get; private set; }
        public string IdPattern { get; private set; } = "{TYPE}-{slug}";

        public Taxonomy()
        {
            RequiredMeta = DefaultRequiredMeta();
            Themes = new List<TaxonomyTheme>();
        }

        public static Taxonomy Default()
        {
            var t = new Taxonomy();
            t.Themes.AddRange(new[]
            {
                new TaxonomyTheme { Id = "THEME-process-improvement", Title = "THEME :: Process Improvement", Area = "cascade", Purpose = "Agile operating system, Halyard agent, delivery process" },
                new TaxonomyTheme { Id = "THEME-data-platform", Title = "THEME :: Data Platform", Area = "data-platform", Purpose = "RDS, schema, IAM, shared data platform services" },
                new TaxonomyTheme { Id = "THEME-data-pipeline-modernization", Title = "THEME :: Data Pipeline Modernization", Area = "pipeline", Purpose = "ETL/ELT reliability and modernization" },
                new TaxonomyTheme { Id = "THEME-platform-engineering", Title = "THEME :: Platform Engineering", Area = "platform", Purpose = "CI, environments, shared engineering tooling" },
                new TaxonomyTheme { Id = "THEME-automation", Title = "THEME :: Automation, AI and Agentic Workflows", Area = "automation", Purpose = "Scripting, bots, agentic operators, AI-assisted workflows" },
                new TaxonomyTheme { Id = "THEME-security-compliance", Title = "THEME :: Security & Compliance", Area = "security", Purpose = "Controls, secrets, audit, least privilege" },
                new TaxonomyTheme { Id = "THEME-observability", Title = "THEME :: Observability", Area = "observability", Purpose = "Logs, metrics, traces, alerting" },
                new TaxonomyTheme { Id = "THEME-reliability-dr", Title = "THEME :: Reliability & DR", Area = "reliability", Purpose = "Backups, failover, RPO/RTO" },
                new TaxonomyTheme { Id = "THEME-technical-debt", Title = "THEME :: Technical Debt", Area = "tech-debt", Purpose = "Explicit pay-down of debt" },
                new TaxonomyTheme { Id = "THEME-training", Title = "THEME :: Training", Area = "training", Purpose = "Enablement and quantified time away from tasking" },
            });
            return t;
        }

        public static Dictionary<WorkItemType, string[]> DefaultRequiredMeta()
        {
            return new Dictionary<WorkItemType, string[]>
            {
                { WorkItemType.Theme, new[] { "type", "id", "state", "status", "area" } },
                { WorkItemType.Epic, new[] { "type", "id", "theme", "state", "area" } },
                { WorkItemType.Feature, new[] { "type", "id", "parent", "theme", "state", "area", "effort" } },
                { WorkItemType.Story, new[] { "type", "id", "parent", "theme", "state", "area", "points", "sprint" } },
                { WorkItemType.Task, new[] { "type", "id", "parent", "theme", "state", "area" } },
                { WorkItemType.Bug, new[] { "type", "id", "theme", "state", "area" } },
            };
        }

        public string[] RequiredFor(WorkItemType type)
        {
            string[]? keys;
            if (RequiredMeta.TryGetValue(type, out keys) && keys != null) return keys;
            return type == WorkItemType.Unclassified ? new[] { "type", "id" } : new[] { "type", "id", "state", "area" };
        }

        public IEnumerable<string> MissingKeys(WorkItemType type, HalyardMeta? meta)
        {
            foreach (var key in RequiredFor(type))
            {
                if (meta == null || !meta.Has(key)) yield return key;
            }
        }

        public static Taxonomy Load(string path)
        {
            if (!File.Exists(path)) return Default();
            return FromJson(File.ReadAllText(path));
        }

        public static Taxonomy FromJson(string json)
        {
            var root = JObject.Parse(json);
            var t = new Taxonomy();
            t.Version = (string?)root["version"] ?? "1.0";
            t.IdPattern = (string?)root["id_pattern"] ?? t.IdPattern;
            var required = root["required_meta"] as JObject;
            if (required != null)
            {
                foreach (var prop in required.Properties())
                {
                    WorkItemType type;
                    if (!WorkItemTypes.TryParse(prop.Name, out type)) continue;
                    var keys = prop.Value.Select(v => ((string?)v ?? string.Empty).Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToArray();
                    t.RequiredMeta[type] = keys;
                }
            }
            var themes = root["themes"] as JArray;
            if (themes != null)
            {
                foreach (var th in themes)
                {
                    t.Themes.Add(new TaxonomyTheme
                    {
                        Id = (string?)th["id"] ?? string.Empty,
                        Title = (string?)th["title"] ?? string.Empty,
                        Area = (string?)th["area"] ?? string.Empty,
                        Purpose = (string?)th["purpose"] ?? string.Empty,
                        Status = (string?)th["status"] ?? "Active",
                    });
                }
            }
            return t;
        }

        public string ToJson()
        {
            var root = new JObject
            {
                ["version"] = Version,
                ["hierarchy"] = new JArray(WorkItemTypes.Hierarchy.Select(WorkItemTypes.ToToken)),
                ["id_pattern"] = IdPattern,
                ["meta_fence"] = HalyardMeta.Fence,
                ["effort_values"] = new JArray(Sizing.EffortScale),
                ["story_points"] = new JArray(Sizing.StoryPointScale),
                ["required_meta"] = new JObject(RequiredMeta.Select(kv => new JProperty(WorkItemTypes.ToToken(kv.Key), new JArray(kv.Value)))),
                ["themes"] = new JArray(Themes.Select(th => new JObject
                {
                    ["id"] = th.Id, ["title"] = th.Title, ["area"] = th.Area, ["purpose"] = th.Purpose, ["status"] = th.Status,
                })),
            };
            return root.ToString(Formatting.Indented);
        }
    }
}
