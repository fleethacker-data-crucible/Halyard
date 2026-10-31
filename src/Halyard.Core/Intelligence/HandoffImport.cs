using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Halyard.Core.Model;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    public sealed class HandoffResult
    {
        public string App { get; set; }
        public string ExportedAt { get; set; }
        public List<WorkItem> Items { get; set; }
        public RepositoryRef Source { get; set; }
        public List<string> Warnings { get; set; }

        public HandoffResult()
        {
            App = string.Empty;
            Items = new List<WorkItem>();
            Warnings = new List<string>();
        }
    }

    /// <summary>
    /// Items handed over by another Fleet app as a plain file (flow 4 of the Fleet relationship map): PacketRat's reviewed action items,
    /// Eight Bells' lessons learned and follow-ups. Format <c>fleet-handoff</c> v1, documented in docs/HANDOFF.md:
    /// <c>{ "format": "fleet-handoff", "version": 1, "source": { "app": "packetrat", "exportedAt": "..." }, "items": [ { "ref", "title", "body", "labels", "url", "due", "kind" } ] }</c>.
    /// The items become raw tickets in a virtual source repository named after the app; they then go through the normal Intake path
    /// (classification, a halyard-meta body, a link back, human review) and never straight into the backlog.
    /// </summary>
    public static class HandoffImport
    {
        public const string Format = "fleet-handoff";
        public const int MaxItems = 500;
        public const int MaxTitle = 200;
        public const int MaxBody = 20000;
        private static readonly Regex AppName = new Regex("^[a-z][a-z0-9-]{1,30}$", RegexOptions.Compiled);
        private static readonly Regex Control = new Regex("[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F]", RegexOptions.Compiled);

        /// <summary>Throws <see cref="FormatException"/> when the document is not a fleet-handoff v1 file.</summary>
        public static HandoffResult Parse(string text)
        {
            JToken root;
            try { root = JToken.Parse(text); }
            catch (JsonException ex) { throw new FormatException("The handoff file is not valid JSON: " + ex.Message); }
            var doc = root as JObject;
            if (doc == null) throw new FormatException("The handoff file must be a JSON object.");
            if (!string.Equals((string)doc["format"], Format, StringComparison.Ordinal)) throw new FormatException("Not a " + Format + " file (format is '" + (string)doc["format"] + "').");
            if (((int?)doc["version"] ?? 0) != 1) throw new FormatException("Unsupported " + Format + " version " + doc["version"] + "; this Halyard reads version 1.");
            var source = doc["source"] as JObject;
            var app = source == null ? null : ((string)source["app"] ?? string.Empty).Trim().ToLowerInvariant();
            if (app == null || !AppName.IsMatch(app)) throw new FormatException("source.app must be the exporting app's name in lower case, for example 'packetrat' or 'eight-bells'.");
            var items = doc["items"] as JArray;
            if (items == null) throw new FormatException("items must be an array.");
            if (items.Count > MaxItems) throw new FormatException("A handoff holds at most " + MaxItems + " items; this one has " + items.Count + ".");

            var result = new HandoffResult { App = app, ExportedAt = (string)source["exportedAt"], Source = new RepositoryRef(ProviderKind.GitHub, "handoff/" + app) };
            var seen = new HashSet<long>();
            var index = 0;
            foreach (var token in items)
            {
                index++;
                var o = token as JObject;
                if (o == null) { result.Warnings.Add("Item " + index + " is not an object; skipped."); continue; }
                var reference = Clean(((string)o["ref"] ?? string.Empty).Trim(), 120);
                var title = Clean(((string)o["title"] ?? string.Empty).Trim(), MaxTitle);
                if (reference.Length == 0) { result.Warnings.Add("Item " + index + " has no ref; skipped."); continue; }
                if (title.Length == 0) { result.Warnings.Add(reference + ": no title; skipped."); continue; }
                var number = StableNumber(reference);
                if (!seen.Add(number)) { result.Warnings.Add(reference + ": duplicate ref in this file; the first one was kept."); continue; }
                var body = new StringBuilder(Clean((string)o["body"] ?? string.Empty, MaxBody));
                var due = (string)o["due"];
                if (!string.IsNullOrWhiteSpace(due)) body.Append(body.Length > 0 ? "\n\n" : string.Empty).Append("Due: ").Append(Clean(due.Trim(), 40));
                body.Append(body.Length > 0 ? "\n\n" : string.Empty).Append("Handed over by ").Append(app).Append(" (ref ").Append(reference).Append(")");
                var item = new WorkItem(result.Source, number) { Title = title, Body = body.ToString(), Url = Url((string)o["url"]), State = WorkItemState.Open };
                item.Labels.Add("source::" + app);
                var kind = ((string)o["kind"] ?? string.Empty).Trim().ToLowerInvariant();
                if (kind == "story" || kind == "feature" || kind == "epic" || kind == "theme") item.Labels.Add("type::" + kind);
                foreach (var l in o["labels"] as JArray ?? new JArray())
                {
                    var label = Clean(((string)l ?? string.Empty).Trim(), 60);
                    if (label.Length > 0 && !item.Labels.Contains(label, StringComparer.OrdinalIgnoreCase)) item.Labels.Add(label);
                }
                result.Items.Add(item);
            }
            return result;
        }

        /// <summary>A positive 31-bit number that depends only on the ref, so importing the same export twice recognises what was already copied.</summary>
        public static long StableNumber(string reference)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var b in Encoding.UTF8.GetBytes(reference)) { hash ^= b; hash *= 16777619; }
                return (long)(hash & 0x7FFFFFFF) + 1;
            }
        }

        private static string Clean(string value, int max)
        {
            var v = Control.Replace(value ?? string.Empty, string.Empty);
            return v.Length > max ? v.Substring(0, max) : v;
        }

        private static string Url(string value)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.ToString() : null;
        }
    }
}
