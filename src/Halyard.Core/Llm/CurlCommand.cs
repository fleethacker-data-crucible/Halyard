using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Halyard.Core.Serialization;

namespace Halyard.Core.Llm
{
    /// <summary>A curl command Halyard cannot use as a model endpoint. The message is meant for the user.</summary>
    public sealed class CurlParseException : Exception
    {
        public CurlParseException(string message) : base(message) { }
    }

    /// <summary>What Halyard took from a pasted curl command. Never holds a token.</summary>
    public sealed class CurlEndpoint
    {
        public string Url { get; set; }
        public string Method { get; set; }
        public LlmWireFormat Format { get; set; }
        /// <summary>The body's <c>model</c>; empty when the body has none (Databricks names the model in the URL).</summary>
        public string Model { get; set; }
        /// <summary>Extra request headers Halyard will send (no credentials, no Content-Type, no Authorization).</summary>
        public Dictionary<string, string> Headers { get; set; }
        /// <summary>Header names that were dropped, with the reason, for the user.</summary>
        public List<string> IgnoredHeaders { get; set; }
        public List<string> Warnings { get; set; }
        /// <summary>The curl carried a literal credential (Authorization header or -u). It was not kept.</summary>
        public bool HadLiteralToken { get; set; }
        /// <summary><c>max_output_tokens</c> / <c>max_tokens</c> from the body, for information only.</summary>
        public int? BodyMaxTokens { get; set; }

        public CurlEndpoint()
        {
            Method = "POST";
            Model = string.Empty;
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IgnoredHeaders = new List<string>();
            Warnings = new List<string>();
        }
    }

    /// <summary>
    /// Reads a pasted curl command (bash, cmd or PowerShell line continuations; single or double quotes) into a model
    /// endpoint: URL, method, extra headers, body format and model. The token never comes from the curl text: a literal
    /// bearer token is dropped and reported, and placeholders such as <c>$TOKEN</c> are left to the token field.
    /// </summary>
    public static class CurlCommand
    {
        public const int MaxLength = 64 * 1024;
        public const int MaxHeaders = 10;
        public const string LiteralTokenWarning = "The curl command contains a literal token or password. Halyard did not keep it: put the token in the token field (and replace it in the command with a placeholder such as $AI_ENDPOINT_TOKEN before sharing the command).";

        private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "-X", "--request", "-H", "--header", "-d", "--data", "--data-raw", "--data-binary", "--data-ascii", "--json",
            "--data-urlencode", "-F", "--form", "--url", "-u", "--user", "--oauth2-bearer",
            "-o", "--output", "-m", "--max-time", "--connect-timeout", "-A", "--user-agent", "-e", "--referer",
            "--retry", "--retry-delay", "--retry-max-time", "-w", "--write-out", "--cacert", "--capath", "-E", "--cert",
            "--key", "--cert-type", "--key-type", "-x", "--proxy", "-U", "--proxy-user", "-b", "--cookie", "-c", "--cookie-jar",
            "--resolve", "--connect-to", "--limit-rate", "-K", "--config", "-T", "--upload-file", "-r", "--range",
            "--interface", "--dns-servers", "-y", "--speed-time", "-Y", "--speed-limit", "--max-filesize", "--trace",
            "--trace-ascii", "--stderr", "-D", "--dump-header", "-z", "--time-cond", "--expect100-timeout", "--keepalive-time",
        };

        /// <summary>Short options that take a value (they may be written attached, as in <c>-XPOST</c>, or last in a group such as <c>-sX</c>).</summary>
        private const string ShortValueLetters = "XHdFuoAmexUbcKTrDzyYwE";

        private static readonly Regex Placeholder = new Regex(@"\$\{[^}]*\}|\$\(?[A-Za-z_][A-Za-z0-9_:]*\)?|<[^<>]*>|\{\{[^}]*\}\}|%[A-Za-z_][A-Za-z0-9_]{2,}%", RegexOptions.Compiled);
        private static readonly Regex HeaderName = new Regex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$", RegexOptions.Compiled);
        private static readonly Regex SecretName = new Regex(@"(?i)auth|token|key|secret|cookie|passw|session|signature|credential|bearer", RegexOptions.Compiled);
        private static readonly HashSet<string> TransportHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Accept", "Accept-Encoding", "Connection", "Content-Length", "Content-Encoding", "Expect", "Host", "Keep-Alive",
            "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "User-Agent",
        };

        public static CurlEndpoint Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new CurlParseException("Paste a curl command first.");
            if (text.Length > MaxLength) throw new CurlParseException("The curl command is longer than " + (MaxLength / 1024) + " KB.");
            var args = Tokenize(text);
            if (args.Count > 0 && Regex.IsMatch(args[0], @"^(?i)curl(\.exe)?$")) args.RemoveAt(0);
            if (args.Count == 0) throw new CurlParseException("The curl command has no URL.");

            var result = new CurlEndpoint();
            string url = null;
            string method = null;
            var headers = new List<string>();
            var bodies = new List<string>();
            var options = true;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (options && a == "--") { options = false; continue; }
                if (!options || a.Length < 2 || a[0] != '-')
                {
                    if (url == null) url = a;
                    else result.Warnings.Add("Ignored an extra argument after the URL.");
                    continue;
                }
                string name;
                string value = null;
                var hasValue = false;
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    name = a;
                    var eq = a.IndexOf('=');
                    if (eq > 2 && ValueOptions.Contains(a.Substring(0, eq))) { name = a.Substring(0, eq); value = a.Substring(eq + 1); hasValue = true; }
                }
                else
                {
                    // A group of short options (-sS, -sSL, -sX POST) or one with its value attached (-XPOST).
                    name = null;
                    for (var k = 1; k < a.Length; k++)
                    {
                        var letter = a[k];
                        if (ShortValueLetters.IndexOf(letter) >= 0)
                        {
                            name = "-" + letter;
                            if (k + 1 < a.Length) { value = a.Substring(k + 1); hasValue = true; }
                            break;
                        }
                        Flag("-" + letter, result);
                    }
                    if (name == null) continue;
                }
                if (!ValueOptions.Contains(name))
                {
                    Flag(name, result);
                    continue;
                }
                if (!hasValue)
                {
                    if (i + 1 >= args.Count) throw new CurlParseException("The option " + name + " has no value.");
                    value = args[++i];
                }
                switch (name)
                {
                    case "-X": case "--request": method = value.Trim().ToUpperInvariant(); break;
                    case "-H": case "--header": headers.Add(value); break;
                    case "--url": if (url == null) url = value; else result.Warnings.Add("Ignored a second URL."); break;
                    case "-d": case "--data": case "--data-raw": case "--data-binary": case "--data-ascii": case "--json":
                        if (name != "--data-raw" && value.StartsWith("@", StringComparison.Ordinal))
                            throw new CurlParseException("The body is read from a file (" + name + " @...). Paste the JSON body into the command instead.");
                        bodies.Add(value);
                        break;
                    case "--data-urlencode": case "-F": case "--form": case "-T": case "--upload-file":
                        throw new CurlParseException("The option " + name + " sends a form or a file; Halyard sends a JSON body. Use -d with the JSON body.");
                    case "-u": case "--user": case "--oauth2-bearer":
                        if (!IsPlaceholderCredential(value)) result.HadLiteralToken = true;
                        break;
                    default: break; // other value options (timeouts, output, proxy...) do not change the request Halyard sends
                }
            }

            result.Url = CheckUrl(url);
            if (bodies.Count > 1) throw new CurlParseException("The curl command has more than one body (-d). Keep a single JSON body.");
            var bodyText = bodies.Count == 1 ? bodies[0] : null;
            result.Method = method ?? "POST";
            if (result.Method != "POST") result.Warnings.Add("The curl command uses " + result.Method + "; Halyard always calls the model with POST.");
            result.Method = "POST";

            foreach (var h in headers) ReadHeader(h, result);
            if (result.HadLiteralToken)
                result.Warnings.Add(LiteralTokenWarning);

            JObject body = null;
            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                JToken parsed;
                try { parsed = JToken.Parse(bodyText); }
                catch (JsonException ex) { throw new CurlParseException("The -d body is not valid JSON (" + ex.Message + "). Check the quotes: wrap the body in single quotes in bash, or escape inner double quotes as \\\" in cmd."); }
                body = parsed as JObject;
                if (body == null) throw new CurlParseException("The -d body must be a JSON object such as {\"model\": ..., \"input\": ...}.");
            }
            result.Format = DetectFormat(result.Url, body);
            if (body != null)
            {
                var model = body["model"];
                if (model != null && model.Type != JTokenType.Null)
                {
                    if (model.Type != JTokenType.String) throw new CurlParseException("The body's model must be a string.");
                    var m = ((string)model).Trim();
                    var ph = Placeholder.Match(m);
                    if (ph.Success) throw new CurlParseException("The body's model is a placeholder (" + ph.Value + "). Put the real model name in the curl command.");
                    if (m.Length > 200 || m.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0) throw new CurlParseException("The body's model is not a valid model name.");
                    result.Model = m;
                }
                var max = body["max_output_tokens"] ?? body["max_completion_tokens"] ?? body["max_tokens"];
                if (max != null && max.Type == JTokenType.Integer) result.BodyMaxTokens = (int?)max;
                if (body["stream"] != null && body["stream"].Type == JTokenType.Boolean && (bool)body["stream"])
                    result.Warnings.Add("The body asks for streaming; Halyard calls the endpoint without streaming.");
            }
            if (result.Model.Length == 0 && result.Format != LlmWireFormat.Databricks)
                throw new CurlParseException("The body has no model. Add \"model\": \"<name>\" to the -d body (Halyard sends the model on every call).");
            return result;
        }

        private static void Flag(string name, CurlEndpoint result)
        {
            switch (name)
            {
                case "-k": case "--insecure":
                    result.Warnings.Add("Ignored " + name + ": Halyard always verifies the endpoint's TLS certificate.");
                    break;
                case "-L": case "--location":
                    result.Warnings.Add("Ignored " + name + ": Halyard never follows redirects (a redirect would carry the token elsewhere).");
                    break;
                case "-G": case "--get":
                    result.Warnings.Add("Ignored " + name + ": Halyard always calls the model with POST.");
                    break;
                default: break; // -s, -S, -v, -i, --compressed, --fail, --http1.1 ... change nothing Halyard sends
            }
        }

        private static void ReadHeader(string raw, CurlEndpoint result)
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                result.IgnoredHeaders.Add(Shorten(raw.Trim().TrimEnd(';')) + " (not a Name: value header)");
                return;
            }
            var name = raw.Substring(0, colon).Trim();
            var value = raw.Substring(colon + 1).Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsPlaceholderCredential(value)) result.HadLiteralToken = true;
                return; // Halyard sends Authorization: Bearer <token field>
            }
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) return; // always application/json
            if (!HeaderName.IsMatch(name)) { result.IgnoredHeaders.Add(Shorten(name) + " (not a valid header name)"); return; }
            if (TransportHeaders.Contains(name)) { result.IgnoredHeaders.Add(name + " (set by Halyard)"); return; }
            if (SecretName.IsMatch(name))
            {
                if (!IsPlaceholderCredential(value)) result.HadLiteralToken = true;
                result.IgnoredHeaders.Add(name + " (looks like a credential; Halyard sends only the token field, as Authorization: Bearer)");
                return;
            }
            if (value.Length == 0) { result.IgnoredHeaders.Add(name + " (empty)"); return; }
            if (Placeholder.IsMatch(value)) { result.IgnoredHeaders.Add(name + " (its value is a placeholder)"); return; }
            if (value.Length > 1024 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) { result.IgnoredHeaders.Add(name + " (value too long or not one line)"); return; }
            if (result.Headers.Count >= MaxHeaders && !result.Headers.ContainsKey(name)) { result.IgnoredHeaders.Add(name + " (more than " + MaxHeaders + " extra headers)"); return; }
            result.Headers[name] = value;
        }

        /// <summary>True for <c>Bearer $TOKEN</c>, <c>Bearer ${X:-y}</c>, <c>Bearer &lt;token&gt;</c> and similar: nothing secret to drop.</summary>
        private static bool IsPlaceholderCredential(string value)
        {
            var v = Regex.Replace(value.Trim(), @"^(?i)(Bearer|Basic|Token)\s+", string.Empty).Trim();
            if (v.Length == 0) return true;
            var m = Placeholder.Match(v);
            return m.Success && m.Index == 0 && m.Length == v.Length;
        }

        private static string Shorten(string s)
        {
            if (s == null) return string.Empty;
            return s.Length > 40 ? s.Substring(0, 40) + "..." : s;
        }

        /// <summary>The body format: <c>input</c> = Responses API, <c>messages</c> = chat completions (Databricks when the URL is a serving endpoint).</summary>
        public static LlmWireFormat DetectFormat(string url, JObject body)
        {
            var path = string.Empty;
            Uri u;
            if (Uri.TryCreate(url ?? string.Empty, UriKind.Absolute, out u)) path = u.AbsolutePath.TrimEnd('/');
            var databricks = Regex.IsMatch(path, @"(?i)/serving-endpoints/[^/]+/invocations$");
            if (body != null)
            {
                var hasInput = body["input"] != null;
                var hasMessages = body["messages"] != null;
                if (hasInput && hasMessages) throw new CurlParseException("The body has both input and messages; keep one (input = Responses API, messages = chat completions).");
                if (hasInput) return LlmWireFormat.Responses;
                if (hasMessages) return databricks ? LlmWireFormat.Databricks : LlmWireFormat.Chat;
                if (body["prompt"] != null) throw new CurlParseException("The body uses prompt (the legacy completions API). Halyard supports chat completions (messages) and the Responses API (input).");
            }
            if (databricks) return LlmWireFormat.Databricks;
            if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)) return LlmWireFormat.Responses;
            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return LlmWireFormat.Chat;
            throw new CurlParseException("Could not tell the API format: the body has neither input (Responses API) nor messages (chat completions), and the URL does not end in /responses, /chat/completions or /invocations.");
        }

        /// <summary>
        /// The endpoint URL, checked: absolute http(s), no user:password, no placeholders, https unless the host is this
        /// machine. Throws <see cref="CurlParseException"/> with the reason.
        /// </summary>
        public static string CheckUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new CurlParseException("The curl command has no URL.");
            url = url.Trim();
            var ph = Placeholder.Match(url);
            if (ph.Success)
                throw new CurlParseException("The URL still contains the placeholder " + ph.Value + ". Replace it with the real endpoint address (the token goes in the token field, not in the URL).");
            var problem = UrlProblem(url);
            if (problem != null) throw new CurlParseException(problem);
            return url;
        }

        /// <summary>Why a URL cannot be a model endpoint, or null when it can. Used for the curl URL and for HALYARD_LLM_CUSTOM_URL.</summary>
        public static string UrlProblem(string url)
        {
            Uri u;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "The URL is not an absolute http(s) address.";
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return "The URL must start with https:// (it starts with " + u.Scheme + "://).";
            if (!string.IsNullOrEmpty(u.UserInfo)) return "The URL contains a user name or password. Remove it; the token goes in the token field.";
            if (!string.IsNullOrEmpty(u.Fragment)) return "The URL contains a #fragment. Remove it.";
            if (u.Scheme == Uri.UriSchemeHttp && !IsLoopback(u)) return "The URL must use https:// (plain http is accepted only for this machine: localhost or 127.0.0.1).";
            foreach (var pair in u.Query.TrimStart('?').Split('&'))
            {
                var name = Uri.UnescapeDataString(pair.Split('=')[0]);
                if (name.Length > 0 && SecretName.IsMatch(name))
                    return "The URL carries a credential in its query string (" + Shorten(name) + "=...). Remove it; Halyard sends the token field as Authorization: Bearer.";
            }
            return null;
        }

        public static bool IsLoopback(Uri u)
        {
            if (u.IsLoopback) return true;
            IPAddress ip;
            return IPAddress.TryParse(u.Host.Trim('[', ']'), out ip) && IPAddress.IsLoopback(ip);
        }

        /// <summary>Scheme, host and port: a token entered for one origin is never sent to another without being entered again.</summary>
        public static string Origin(string url)
        {
            Uri u;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return null;
            return u.Scheme.ToLowerInvariant() + "://" + u.Host.ToLowerInvariant() + ":" + u.Port;
        }

        /// <summary>
        /// Splits a shell command line into arguments: single quotes are literal, double quotes honour \" \\ \$ \`,
        /// a backslash outside quotes escapes the next character, and a line ending in \ (bash), ^ (cmd) or ` (PowerShell)
        /// continues on the next line. Newlines otherwise separate arguments like spaces.
        /// </summary>
        public static List<string> Tokenize(string text)
        {
            var args = new List<string>();
            var sb = new StringBuilder();
            var inToken = false;
            var i = 0;
            var n = text.Length;
            while (i < n)
            {
                var c = text[i];
                var cont = ContinuationLength(text, i);
                if (cont > 0) { i += cont; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (inToken) { args.Add(sb.ToString()); sb.Length = 0; inToken = false; }
                    i++;
                    continue;
                }
                inToken = true;
                if (c == '\'')
                {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0) throw new CurlParseException("A single quote is not closed.");
                    sb.Append(text, i + 1, end - i - 1);
                    i = end + 1;
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    var closed = false;
                    while (i < n)
                    {
                        var d = text[i];
                        if (d == '"') { closed = true; i++; break; }
                        if (d == '\\' && i + 1 < n)
                        {
                            var e = text[i + 1];
                            if (e == '"' || e == '\\' || e == '$' || e == '`') { sb.Append(e); i += 2; continue; }
                            if (e == '\n') { i += 2; continue; }
                            if (e == '\r' && i + 2 < n && text[i + 2] == '\n') { i += 3; continue; }
                        }
                        sb.Append(d);
                        i++;
                    }
                    if (!closed) throw new CurlParseException("A double quote is not closed.");
                    continue;
                }
                if (c == '\\' && i + 1 < n)
                {
                    sb.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            if (inToken) args.Add(sb.ToString());
            return args;
        }

        /// <summary>Length of a line continuation at <paramref name="i"/> (\, ^ or ` then optional spaces and a newline), or 0.</summary>
        private static int ContinuationLength(string text, int i)
        {
            var c = text[i];
            if (c != '\\' && c != '^' && c != '`') return 0;
            var j = i + 1;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
            if (j < text.Length && text[j] == '\r') j++;
            if (j < text.Length && text[j] == '\n') return j + 1 - i;
            return 0;
        }
    }
}
