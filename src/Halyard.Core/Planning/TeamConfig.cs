using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Halyard.Core.Planning
{
    /// <summary>One skill from the capability taxonomy (Shellback ids, so peer cards line up without translation).</summary>
    public sealed class SkillDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
    }

    public sealed class SkillsTaxonomy
    {
        public List<SkillDefinition> Skills { get; private set; } = new List<SkillDefinition>();
        public Dictionary<string, string> Domains { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SkillDefinition? Find(string id)
        {
            return Skills.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public bool Contains(string id) { return Find(id) != null; }

        public static SkillsTaxonomy Load(string path)
        {
            var t = new SkillsTaxonomy();
            if (!File.Exists(path)) return t;
            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var d in root["domains"] as JArray ?? new JArray()) t.Domains[(string?)d["id"] ?? string.Empty] = (string?)d["name"] ?? string.Empty;
            foreach (var s in root["skills"] as JArray ?? new JArray())
                t.Skills.Add(new SkillDefinition { Id = (string?)s["id"] ?? string.Empty, Name = (string?)s["name"] ?? string.Empty, Domain = (string?)s["domain"] ?? string.Empty });
            t.Skills.RemoveAll(s => s.Id.Length == 0);
            return t;
        }

        /// <summary>Slug the way Shellback does: lower-case, non-alphanumerics to '-', trimmed.</summary>
        public static string Slug(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var s = System.Text.RegularExpressions.Regex.Replace(text!.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return s;
        }
    }

    public sealed class SprintCalendar
    {
        public string Prefix { get; set; } = "2026-S";
        public int LengthDays { get; set; } = 14;
        public int FirstNumber { get; set; } = 1;
        public DateTimeOffset FirstStart { get; set; } = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        public string Name(int number) { return Prefix + number.ToString("00"); }

        public int? NumberOf(string? sprintName)
        {
            if (string.IsNullOrWhiteSpace(sprintName) || !sprintName!.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            int n;
            return int.TryParse(sprintName.Substring(Prefix.Length), out n) ? n : (int?)null;
        }

        public DateTimeOffset StartOf(int number) { return FirstStart.AddDays((number - FirstNumber) * (double)LengthDays); }
        public DateTimeOffset EndOf(int number) { return StartOf(number).AddDays(LengthDays); }

        /// <summary>The sprint containing <paramref name="at"/> (never earlier than the first sprint).</summary>
        public int Current(DateTimeOffset at)
        {
            var days = (at - FirstStart).TotalDays;
            if (days < 0) return FirstNumber;
            return FirstNumber + (int)Math.Floor(days / LengthDays);
        }
    }

    public sealed class Team
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int PointsPerSprint { get; set; }
        /// <summary>Shares of the sprint: committed (planned delivery), allocated (support, ops), experiment. Sum ≈ 1.</summary>
        public double Committed { get; set; } = 0.75;
        public double Allocated { get; set; } = 0.15;
        public double Experiment { get; set; } = 0.10;
    }

    public sealed class Person
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Team { get; set; } = string.Empty;
        /// <summary>Points this person can take per sprint; 0 means "an even share of the team's".</summary>
        public int PointsPerSprint { get; set; }
        /// <summary>Skill id → confidence 0..4 (0 = attested, no confidence given).</summary>
        public Dictionary<string, int> Skills { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>available | limited | unknown (from Shellback); unknown counts as available for planning.</summary>
        public string Availability { get; set; } = "unknown";
        public string? ShellbackCard { get; set; }
        public bool FromShellback { get; set; }

        public bool Has(string skill) { return Skills.ContainsKey(skill); }
        public int Confidence(string skill) { int c; return Skills.TryGetValue(skill, out c) ? c : -1; }
    }

    /// <summary>
    /// Teams, people and the sprint calendar, from <c>config/team.json</c>, with skills and confidence merged
    /// from Shellback peer cards in <c>config/shellback/</c> (and the former <c>config/synapse/</c>). Shellback owns
    /// people and consent; Halyard only reads
    /// exported cards and never writes them.
    /// </summary>
    public sealed class TeamConfig
    {
        public SprintCalendar Sprint { get; private set; } = new SprintCalendar();
        public List<Team> Teams { get; private set; } = new List<Team>();
        public List<Person> People { get; private set; } = new List<Person>();
        public List<string> Warnings { get; private set; } = new List<string>();
        public string? Path { get; private set; }
        public int CardsImported { get; private set; }

        public Team? FindTeam(string? id) { return Teams.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)); }
        public Person? FindPerson(string? id) { return People.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase)); }

        public int TeamCapacity(Team team)
        {
            if (team.PointsPerSprint > 0) return team.PointsPerSprint;
            return People.Where(p => string.Equals(p.Team, team.Id, StringComparison.OrdinalIgnoreCase)).Sum(p => p.PointsPerSprint);
        }

        public int PersonCapacity(Person person)
        {
            if (person.PointsPerSprint > 0) return person.PointsPerSprint;
            var team = FindTeam(person.Team);
            if (team == null) return 0;
            var members = People.Count(p => string.Equals(p.Team, team.Id, StringComparison.OrdinalIgnoreCase));
            return members == 0 ? 0 : (int)Math.Floor(team.PointsPerSprint / (double)members);
        }

        public static TeamConfig Default()
        {
            return new TeamConfig();
        }

        public static TeamConfig Load(string path, string? cardDirectory)
        {
            return Load(path, string.IsNullOrEmpty(cardDirectory) ? new string[0] : new[] { cardDirectory! });
        }

        /// <summary>
        /// Loads team.json, then peer cards from each folder in order (normally <c>config/shellback/</c>, then the
        /// former <c>config/synapse/</c>). A card file name already imported from an earlier folder is skipped.
        /// </summary>
        public static TeamConfig Load(string path, IEnumerable<string> cardDirectories)
        {
            var c = new TeamConfig { Path = path };
            if (File.Exists(path))
            {
                try { c.Read(JObject.Parse(File.ReadAllText(path))); }
                catch (JsonException ex) { c.Warnings.Add("team.json could not be parsed: " + ex.Message); }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in cardDirectories ?? new string[0])
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (!seen.Add(System.IO.Path.GetFileName(file))) continue;
                    try { if (c.ImportPeerCard(file)) c.CardsImported++; }
                    catch (JsonException ex) { c.Warnings.Add(System.IO.Path.GetFileName(file) + ": " + ex.Message); }
                }
            }
            return c;
        }

        private void Read(JObject root)
        {
            var sprint = root["sprint"] as JObject;
            if (sprint != null)
            {
                Sprint.Prefix = (string?)sprint["prefix"] ?? Sprint.Prefix;
                Sprint.LengthDays = Math.Max(1, (int?)sprint["lengthDays"] ?? Sprint.LengthDays);
                Sprint.FirstNumber = (int?)sprint["firstNumber"] ?? Sprint.FirstNumber;
                DateTimeOffset start;
                if (DateTimeOffset.TryParse((string?)sprint["firstStart"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out start)) Sprint.FirstStart = start;
            }
            foreach (var t in root["teams"] as JArray ?? new JArray())
            {
                var team = new Team { Id = (string?)t["id"] ?? string.Empty, Name = (string?)t["name"] ?? (string?)t["id"] ?? string.Empty, PointsPerSprint = (int?)t["pointsPerSprint"] ?? 0 };
                var alloc = t["allocation"] as JObject;
                if (alloc != null)
                {
                    team.Committed = (double?)alloc["committed"] ?? team.Committed;
                    team.Allocated = (double?)alloc["allocated"] ?? team.Allocated;
                    team.Experiment = (double?)alloc["experiment"] ?? team.Experiment;
                }
                if (team.Id.Length > 0) Teams.Add(team);
            }
            foreach (var p in root["people"] as JArray ?? new JArray())
            {
                var person = new Person
                {
                    Id = (string?)p["id"] ?? SkillsTaxonomy.Slug((string?)p["name"]),
                    Name = (string?)p["name"] ?? (string?)p["id"] ?? string.Empty,
                    Team = (string?)p["team"] ?? string.Empty,
                    PointsPerSprint = (int?)p["pointsPerSprint"] ?? 0,
                    // "shellbackCard"; "synapseCard" is the former key and still read.
                    ShellbackCard = (string?)p["shellbackCard"] ?? (string?)p["synapseCard"],
                    Availability = (string?)p["availability"] ?? "unknown",
                };
                var skills = p["skills"];
                if (skills is JArray arr) foreach (var s in arr) { var id = (string?)s; if (!string.IsNullOrWhiteSpace(id)) person.Skills[id!.Trim()] = 0; }
                else if (skills is JObject obj) foreach (var kv in obj) person.Skills[kv.Key] = (int?)kv.Value ?? 0;
                if (person.Id.Length > 0) People.Add(person);
            }
        }

        /// <summary>
        /// Merges one Shellback peer card (format id <c>synapse-peer-card</c> v1, kept by Shellback for compatibility).
        /// Only consented cards are read. A card attaches to the person whose <c>shellbackCard</c> names the file, else whose name matches; otherwise a
        /// new person is added with no team.
        /// </summary>
        public bool ImportPeerCard(string file)
        {
            var card = JObject.Parse(File.ReadAllText(file));
            if (!string.Equals((string?)card["format"], "synapse-peer-card", StringComparison.OrdinalIgnoreCase)) return false;
            if (!((bool?)card["consent"] ?? false)) { Warnings.Add(System.IO.Path.GetFileName(file) + ": card has no consent flag; skipped."); return false; }
            var name = (string?)card["name"] ?? string.Empty;
            var fileName = System.IO.Path.GetFileName(file);
            var person = People.FirstOrDefault(p => !string.IsNullOrEmpty(p.ShellbackCard) && string.Equals(System.IO.Path.GetFileName(p.ShellbackCard!), fileName, StringComparison.OrdinalIgnoreCase))
                         ?? People.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (person == null)
            {
                person = new Person { Id = SkillsTaxonomy.Slug(name), Name = name, ShellbackCard = fileName };
                People.Add(person);
            }
            person.FromShellback = true;
            var surveys = card["surveys"] as JObject;
            foreach (var s in card["skills"] as JArray ?? new JArray())
            {
                var id = (string?)s;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var confidence = 0;
                var survey = surveys == null ? null : surveys[id!] as JObject;
                if (survey != null) confidence = Math.Max(0, Math.Min(4, (int?)survey["confidence"] ?? 0));
                person.Skills[id!] = confidence;
            }
            var availability = (string?)card["availability"];
            if (!string.IsNullOrWhiteSpace(availability)) person.Availability = availability!.ToLowerInvariant();
            return true;
        }

        public Dictionary<string, object?> ToPublicDictionary()
        {
            return new Dictionary<string, object?>
            {
                { "path", Path },
                { "sprint", new { Sprint.Prefix, Sprint.LengthDays, Sprint.FirstNumber, firstStart = Sprint.FirstStart.ToString("yyyy-MM-dd"), current = Sprint.Name(Sprint.Current(DateTimeOffset.UtcNow)) } },
                { "teams", Teams.Select(t => new { t.Id, t.Name, pointsPerSprint = TeamCapacity(t), t.Committed, t.Allocated, t.Experiment, members = People.Count(p => string.Equals(p.Team, t.Id, StringComparison.OrdinalIgnoreCase)) }).ToList() },
                { "people", People.Select(p => new { p.Id, p.Name, p.Team, pointsPerSprint = PersonCapacity(p), skills = p.Skills, p.Availability, p.FromShellback, p.ShellbackCard }).ToList() },
                { "cardsImported", CardsImported },
                { "warnings", Warnings },
            };
        }
    }
}
