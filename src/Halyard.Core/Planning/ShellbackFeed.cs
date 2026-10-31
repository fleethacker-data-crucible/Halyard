using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Planning
{
    public sealed class ShellbackRefreshResult
    {
        public string Source { get; set; }
        public int Fetched { get; set; }
        public int Written { get; set; }
        public int Unchanged { get; set; }
        public int SkippedNoConsent { get; set; }
        public int SkippedInvalid { get; set; }
        /// <summary>Cached cards that are no longer in the export (withdrawn consent or left the team).</summary>
        public int Removed { get; set; }
        public List<string> Warnings { get; set; }

        public ShellbackRefreshResult()
        {
            Source = string.Empty;
            Warnings = new List<string>();
        }
    }

    /// <summary>
    /// Reads Shellback peer cards from a Shellback export (a file or folder) or an endpoint instead of cards dropped by hand in
    /// <c>config/shellback/</c>. Accepted shapes: one card, an array of cards, or <c>{ "cards": [...] }</c>. Only consented cards
    /// are kept, in a cache folder under <c>output/</c> that the team loader reads first; a card that leaves the export, or loses
    /// consent, is removed from the cache on the next refresh. Read-only towards Shellback: only GET is ever issued.
    /// </summary>
    public static class ShellbackFeed
    {
        public static ShellbackRefreshResult RefreshFromFile(string path, string liveDirectory)
        {
            var result = new ShellbackRefreshResult { Source = path };
            var cards = new List<JObject>();
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                    cards.AddRange(Parse(File.ReadAllText(file), result.Warnings, Path.GetFileName(file)));
            }
            else if (File.Exists(path)) cards.AddRange(Parse(File.ReadAllText(path), result.Warnings, Path.GetFileName(path)));
            else throw new FileNotFoundException("The Shellback export was not found: " + path);
            return Apply(cards, liveDirectory, result);
        }

        public static async Task<ShellbackRefreshResult> RefreshFromUrlAsync(IHttpTransport http, string url, string token, string liveDirectory, CancellationToken cancellation)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("The Shellback endpoint must be an http(s) URL.");
            var call = new HttpCall("GET", url);
            call.Headers["Accept"] = "application/json";
            call.Headers["User-Agent"] = "Halyard/1.0 (+delivery-intelligence)";
            if (!string.IsNullOrWhiteSpace(token)) call.Headers["Authorization"] = "Bearer " + token.Trim();
            var reply = await http.SendAsync(call, cancellation).ConfigureAwait(false);
            if (!reply.IsSuccess) throw new ProviderHttpException(reply.Status, "GET " + HttpClientTransport.SafeUrl(url) + " failed with HTTP " + reply.Status, reply.Body);
            var result = new ShellbackRefreshResult { Source = HttpClientTransport.SafeUrl(url) };
            return Apply(Parse(reply.Body, result.Warnings, "endpoint"), liveDirectory, result);
        }

        /// <summary>Cards in a document, whichever of the three shapes it has. Throws on text that is not JSON.</summary>
        public static List<JObject> Parse(string text, List<string> warnings, string label)
        {
            var cards = new List<JObject>();
            JToken root;
            try { root = JToken.Parse(text); }
            catch (JsonException ex) { throw new InvalidDataException(label + " is not valid JSON: " + ex.Message); }
            var array = root as JArray;
            var obj = root as JObject;
            if (obj != null && obj["cards"] is JArray) array = (JArray)obj["cards"];
            if (array != null)
            {
                foreach (var c in array)
                {
                    var co = c as JObject;
                    if (co == null) warnings.Add(label + ": an entry is not a card object; skipped.");
                    else cards.Add(co);
                }
            }
            else if (obj != null) cards.Add(obj);
            else warnings.Add(label + ": expected a card, an array of cards or { \"cards\": [...] }.");
            return cards;
        }

        private static ShellbackRefreshResult Apply(List<JObject> cards, string liveDirectory, ShellbackRefreshResult result)
        {
            Directory.CreateDirectory(liveDirectory);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                result.Fetched++;
                var name = (string)card["name"];
                if (!string.Equals((string)card["format"], "synapse-peer-card", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name))
                {
                    result.SkippedInvalid++;
                    result.Warnings.Add("A card without the peer-card format or a name was skipped.");
                    continue;
                }
                if (!((bool?)card["consent"] ?? false))
                {
                    result.SkippedNoConsent++;
                    result.Warnings.Add(name + ": no consent flag; not imported.");
                    continue;
                }
                var fileName = FileNameFor((string)card["id"] ?? name) + ".json";
                if (!keep.Add(fileName)) { result.Warnings.Add(name + ": a second card with the same id was ignored."); continue; }
                var path = Path.Combine(liveDirectory, fileName);
                var text = card.ToString(Formatting.Indented);
                if (File.Exists(path) && File.ReadAllText(path) == text) { result.Unchanged++; continue; }
                File.WriteAllText(path, text, new UTF8Encoding(false));
                result.Written++;
            }
            foreach (var old in Directory.GetFiles(liveDirectory, "*.json"))
            {
                if (keep.Contains(Path.GetFileName(old))) continue;
                File.Delete(old);
                result.Removed++;
            }
            return result;
        }

        private static string FileNameFor(string id)
        {
            var sb = new StringBuilder();
            foreach (var ch in id.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
            var s = sb.ToString().Trim('-');
            return s.Length == 0 ? "card" : (s.Length > 80 ? s.Substring(0, 80) : s);
        }
    }
}
