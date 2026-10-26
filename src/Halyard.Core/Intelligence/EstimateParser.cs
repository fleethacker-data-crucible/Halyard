using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    public sealed class ParsedEstimate
    {
        /// <summary>Provider number (#12) or canonical id (STORY-…) as the model wrote it.</summary>
        public string Ref { get; set; } = string.Empty;
        public string? Points { get; set; }
        public string? Effort { get; set; }
        public string Confidence { get; set; } = "medium";
        public string Reason { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    /// <summary>
    /// Tolerant parser for model output about sizes. Order of attempts: JSON (array, or object with a list
    /// property), markdown table rows, then a loose regex sweep. Never invents a value: an unparseable
    /// reply yields no estimates, which the caller turns into TBD.
    /// </summary>
    public static class EstimateParser
    {
        private static readonly string[] ListProps = { "estimates", "items", "results", "data", "stories", "features", "proposals" };

        public static IReadOnlyList<ParsedEstimate> Parse(string? text)
        {
            var rows = new List<ParsedEstimate>();
            if (string.IsNullOrWhiteSpace(text)) return rows;
            var raw = text!;

            var json = TryJson(raw);
            if (json != null)
            {
                foreach (var row in json)
                {
                    var o = row as JObject;
                    if (o == null) continue;
                    var r = new ParsedEstimate
                    {
                        Ref = FirstString(o, "id", "ref", "iid", "number", "issue", "key", "canonical_id", "item") ?? string.Empty,
                        Points = FirstString(o, "points", "story_points", "estimate", "size"),
                        Effort = FirstString(o, "effort", "effort_band", "t_shirt"),
                        Confidence = NormalizeConfidence(FirstString(o, "confidence")),
                        Reason = FirstString(o, "reason", "rationale", "why", "notes") ?? string.Empty,
                        Source = "parsed:json",
                    };
                    if (r.Ref.Length == 0 || (r.Points == null && r.Effort == null)) continue;
                    rows.Add(r);
                }
                if (rows.Count > 0) return rows;
            }

            foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.IndexOf('|') < 0) continue;
                if (Regex.IsMatch(line, @"^\s*\|?\s*:?-{2,}")) continue;
                if (Regex.IsMatch(line, @"(?i)\|\s*(iid|id|ref|number|item\s*id|issue)\s*\|")) continue;
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim().Trim('*', '`', ' ')).ToList();
                if (cells.Count < 2) continue;
                var refCell = cells.FirstOrDefault(c => Regex.IsMatch(c, @"^#?\d{1,7}$") || Regex.IsMatch(c, @"^(STORY|FEATURE|EPIC|TASK)-[a-z0-9\-]+$", RegexOptions.IgnoreCase));
                if (refCell == null) continue;
                string? points = null, effort = null;
                foreach (var c in cells)
                {
                    if (c == refCell) continue;
                    var pm = Regex.Match(c, @"^(1|2|3|5|8|13|21|TBD)\s*(pts?|points?|story\s*points?)?$", RegexOptions.IgnoreCase);
                    if (pm.Success) points = pm.Groups[1].Value;
                    var em = Regex.Match(c, @"^(XS|S|M|L|XL|XXL|Small|Medium|Large|TBD)$", RegexOptions.IgnoreCase);
                    if (em.Success) effort = em.Groups[1].Value;
                }
                if (points == null)
                {
                    var loose = Regex.Match(line, @"(?i)\b(1|2|3|5|8|13)\s*(?:story\s*)?points?\b");
                    if (loose.Success) points = loose.Groups[1].Value;
                }
                if (points == null && effort == null) continue;
                var conf = cells.FirstOrDefault(c => Regex.IsMatch(c, @"^(low|medium|high)$", RegexOptions.IgnoreCase));
                var reason = cells.Where(c => c != refCell && c != points && c != effort && c != conf).OrderByDescending(c => c.Length).FirstOrDefault() ?? string.Empty;
                rows.Add(new ParsedEstimate { Ref = refCell.TrimStart('#'), Points = points, Effort = effort, Confidence = NormalizeConfidence(conf), Reason = reason, Source = "parsed:markdown" });
            }
            if (rows.Count > 0) return rows;

            foreach (Match m in Regex.Matches(raw, @"(?i)(?:#|iid\s*|id\s*[:=]?\s*)?\b(\d{1,7}|(?:STORY|FEATURE)-[a-z0-9\-]+)\b[^\n]{0,140}?(?:points?|estimate|effort|size)\s*[:=]?\s*\**\s*(1|2|3|5|8|13|21|TBD|XS|S|M|L|XL|Small|Medium|Large)\b"))
            {
                var val = m.Groups[2].Value;
                var isPoints = Regex.IsMatch(val, @"^(\d+|TBD)$", RegexOptions.IgnoreCase);
                rows.Add(new ParsedEstimate { Ref = m.Groups[1].Value, Points = isPoints ? val : null, Effort = isPoints ? null : val, Confidence = "low", Reason = "regex fallback", Source = "parsed:regex" });
            }
            return rows;
        }

        private static JArray? TryJson(string raw)
        {
            var clean = raw.Trim();
            clean = Regex.Replace(clean, @"^\s*```(?:json|JSON)?\s*", string.Empty);
            clean = Regex.Replace(clean, @"\s*```\s*$", string.Empty);
            clean = clean.Replace('“', '"').Replace('”', '"');
            clean = Regex.Replace(clean, @",\s*([\]\}])", "$1");
            JToken? parsed = null;
            foreach (var candidate in new[] { clean, FirstMatch(clean, @"\[[\s\S]*\]"), FirstMatch(clean, @"\{[\s\S]*\}") })
            {
                if (candidate == null) continue;
                try { parsed = JToken.Parse(candidate); break; }
                catch (JsonException) { }
            }
            if (parsed == null) return null;
            if (parsed is JArray arr) return arr;
            var obj = parsed as JObject;
            if (obj == null) return null;
            foreach (var prop in ListProps)
            {
                if (obj[prop] is JArray list) return list;
            }
            return new JArray(obj);
        }

        private static string? FirstMatch(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Value : null;
        }

        private static string? FirstString(JObject o, params string[] names)
        {
            foreach (var n in names)
            {
                var prop = o.Properties().FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
                if (prop == null || prop.Value.Type == JTokenType.Null) continue;
                var s = prop.Value.Type == JTokenType.String || prop.Value.Type == JTokenType.Integer || prop.Value.Type == JTokenType.Float ? prop.Value.ToString() : null;
                if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
            }
            return null;
        }

        public static string NormalizeConfidence(string? value)
        {
            var v = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (v == "high" || v == "medium" || v == "low") return v;
            double d;
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
            {
                if (d > 1) d /= 100.0;
                return d >= 0.75 ? "high" : d >= 0.4 ? "medium" : "low";
            }
            return "medium";
        }
    }
}
