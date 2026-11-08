using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Llm
{
    /// <summary>
    /// Where Halyard sends model calls: the Fleet router (Fairlead) on this machine, nothing else. Fairlead decides which
    /// engine answers and holds every upstream address and token. Halyard holds only its own Fairlead client key, which
    /// lives in Windows Credential Manager (generic credential <c>Fairlead/Halyard</c>), is read when a call is made and is
    /// never kept in settings, files or logs.
    /// </summary>
    public sealed class LlmSettings
    {
        public const string AppName = "Halyard";
        /// <summary>Fairlead's OpenAI-compatible address on this PC. HALYARD_FAIRLEAD_URL may change the port or path; the host must be loopback.</summary>
        public const string DefaultUrl = "http://127.0.0.1:8776/v1/chat/completions";
        /// <summary>The route name Fairlead resolves (HALYARD_FAIRLEAD_MODEL or Settings can pick another, such as fast or smart).</summary>
        public const string DefaultModel = "auto";
        public const string MissingKeyMessage = "Open Fairlead and install a key for Halyard.";

        public string FairleadUrl { get; set; }
        public string Route { get; set; }
        public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        public int TimeoutSeconds { get; set; }
        /// <summary>Directory for redacted request/response transcripts; null disables them.</summary>
        public string TranscriptDirectory { get; set; }
        /// <summary>HALYARD_LLM_PRIVACY=local sends <c>X-Fairlead-Privacy: local</c> so the router only uses local engines.</summary>
        public string Privacy { get; set; }

        public LlmSettings()
        {
            MaxTokens = 4000;
            Temperature = 0.2;
            TimeoutSeconds = 300;
        }

        /// <summary>Accepts a full chat-completions URL or a base such as <c>http://127.0.0.1:8776/v1</c> and returns the chat-completions URL.</summary>
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return DefaultUrl;
            var u = url.Trim().TrimEnd('/');
            if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return u;
            if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return u + "/chat/completions";
            return u + "/v1/chat/completions";
        }

        public string Url()
        {
            return NormalizeUrl(FairleadUrl);
        }

        public string Model()
        {
            return string.IsNullOrWhiteSpace(Route) ? DefaultModel : Route.Trim();
        }

        /// <summary>Why the address cannot be used, or null. Only a loopback http(s) address is accepted: Fairlead runs on this PC.</summary>
        public static string UrlProblem(string url)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return "HALYARD_FAIRLEAD_URL is not a web address.";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "HALYARD_FAIRLEAD_URL must start with http:// or https://.";
            if (!string.IsNullOrEmpty(uri.UserInfo)) return "HALYARD_FAIRLEAD_URL must not contain a user name or password.";
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "HALYARD_FAIRLEAD_URL must not contain a query or fragment.";
            var host = uri.Host;
            var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]" || host == "::1";
            return loopback ? null : "HALYARD_FAIRLEAD_URL must point at this PC (127.0.0.1, ::1 or localhost): Halyard talks to Fairlead only.";
        }

        /// <summary>The Credential Manager target Fairlead writes the key to.</summary>
        public string KeyTarget { get { return CredentialReader.TargetFor(AppName); } }

        /// <summary>The Fairlead client key from Credential Manager, or null when none is installed.</summary>
        public string Key()
        {
            var key = CredentialReader.Read(KeyTarget);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        public bool IsConfigured()
        {
            return MissingFor() == null;
        }

        /// <summary>Why model calls cannot be made, or null when they can.</summary>
        public string MissingFor()
        {
            var problem = UrlProblem(Url());
            if (problem != null) return problem;
            return Key() == null ? MissingKeyMessage : null;
        }
    }

    public sealed class LlmMessage
    {
        public LlmMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public string Role { get; private set; }
        public string Content { get; private set; }

        public static LlmMessage System(string content) { return new LlmMessage("system", content); }
        public static LlmMessage User(string content) { return new LlmMessage("user", content); }
    }

    public sealed class LlmResponse
    {
        public string Text { get; set; }
        public string Model { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string TranscriptPath { get; set; }

        public LlmResponse()
        {
            Text = string.Empty;
            Model = string.Empty;
        }
    }

    public interface ILlmClient
    {
        /// <summary>Where the answers come from, for proposal sources and logs: always "fairlead".</summary>
        string Name { get; }
        /// <summary>The Fairlead route the calls ask for (auto, fast, smart, ...).</summary>
        string Model { get; }
        Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation);
        Task<LlmResponse> TestAsync(CancellationToken cancellation);
    }

    /// <summary>
    /// The one model client: OpenAI chat completions to Fairlead on this PC. The client key is read from Credential
    /// Manager when a call is made and goes only in the Authorization header; transcripts redact token-shaped text.
    /// </summary>
    public sealed class LlmClient : ILlmClient
    {
        private static readonly Regex TokenShaped = new Regex(@"\b(fl-[A-Za-z0-9_\-]{16,}|sk-[A-Za-z0-9_\-]{10,}|gh[pousr]_[A-Za-z0-9]{20,}|glpat-[A-Za-z0-9_\-]{15,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})\b", RegexOptions.Compiled);
        private readonly IHttpTransport _http;
        private readonly LlmSettings _settings;

        public LlmClient(LlmSettings settings, IHttpTransport http = null)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _http = http ?? new HttpClientTransport();
        }

        public string Name { get { return "fairlead"; } }
        public string Model { get { return _settings.Model(); } }
        public string Url { get { return _settings.Url(); } }

        public async Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("At least one message is required.", "messages");
            var missing = _settings.MissingFor();
            if (missing != null) throw new InvalidOperationException("Model calls are not usable: " + missing);
            var key = _settings.Key();

            // Fairlead accepts max_tokens and turns it into whatever each upstream wants.
            var payload = new JObject
            {
                { "messages", new JArray(messages.Select(m => new JObject { { "role", m.Role }, { "content", m.Content } })) },
                { "model", Model },
                { "temperature", _settings.Temperature },
                { "max_tokens", _settings.MaxTokens },
                { "stream", false },
            };

            var started = DateTimeOffset.UtcNow;
            var call = new HttpCall("POST", Url) { JsonBody = payload.ToString(Formatting.None), Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds) };
            call.Headers["Authorization"] = "Bearer " + key;
            call.Headers["X-Fairlead-Label"] = LlmSettings.AppName;
            if (string.Equals(_settings.Privacy, "local", StringComparison.OrdinalIgnoreCase)) call.Headers["X-Fairlead-Privacy"] = "local";
            call.Headers["Accept"] = "application/json";
            call.Headers["User-Agent"] = "Halyard/1.0";
            var transcript = BeginTranscript(payload);
            HttpReply reply;
            try
            {
                reply = await _http.SendAsync(call, cancellation).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex)
            {
                EndTranscript(transcript, null, ex.Message);
                if (ex.Status == 0) throw new ProviderHttpException(0, "Fairlead did not answer at " + HttpClientTransport.SafeUrl(Url) + ". Start Fairlead and try again.", null);
                throw;
            }
            if (!reply.IsSuccess)
            {
                EndTranscript(transcript, reply.Body, "HTTP " + reply.Status);
                if (reply.Status == 401 || reply.Status == 403)
                    throw new ProviderHttpException(reply.Status, "Fairlead did not accept the Halyard key. Open Fairlead and install a new key for Halyard.", reply.Body);
                throw new ProviderHttpException(reply.Status, "Fairlead returned HTTP " + reply.Status + ": " + Summarize(reply.Body), reply.Body);
            }
            var json = JToken.Parse(string.IsNullOrWhiteSpace(reply.Body) ? "{}" : reply.Body);
            var text = ExtractText(json);
            EndTranscript(transcript, reply.Body, null);
            var usage = json["usage"] as JObject;
            return new LlmResponse
            {
                Text = text,
                Model = Model,
                PromptTokens = usage == null ? null : (int?)(usage["prompt_tokens"] ?? usage["input_tokens"]),
                CompletionTokens = usage == null ? null : (int?)(usage["completion_tokens"] ?? usage["output_tokens"]),
                Elapsed = DateTimeOffset.UtcNow - started,
                TranscriptPath = transcript,
            };
        }

        public Task<LlmResponse> TestAsync(CancellationToken cancellation)
        {
            return ChatAsync(new[] { LlmMessage.System("You are a connectivity probe. Reply with exactly: OK"), LlmMessage.User("Reply with OK.") }, cancellation);
        }

        /// <summary>Finds the assistant text in a chat-completions answer.</summary>
        public static string ExtractText(JToken response)
        {
            if (response == null) return string.Empty;
            var choices = response["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var msg = choices[0]["message"];
                if (msg != null)
                {
                    var content = msg["content"];
                    var parts = content as JArray;
                    if (parts != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var p in parts)
                        {
                            if (p.Type == JTokenType.String) sb.Append((string)p);
                            else sb.Append((string)p["text"] ?? (string)p["content"] ?? string.Empty);
                        }
                        if (sb.Length > 0) return sb.ToString().Trim();
                    }
                    else if (content != null && content.Type != JTokenType.Null)
                    {
                        var s = (string)content;
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                    foreach (var alt in new[] { "reasoning_content", "reasoning", "text", "output_text", "refusal" })
                    {
                        var s = (string)msg[alt];
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                }
                var plain = (string)choices[0]["text"];
                if (!string.IsNullOrWhiteSpace(plain)) return plain.Trim();
            }
            return string.Empty;
        }

        private string BeginTranscript(JObject request)
        {
            var dir = _settings.TranscriptDirectory;
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                Directory.CreateDirectory(dir);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                var basePath = Path.Combine(dir, stamp + "_fairlead");
                var redacted = Redact(request.ToString(Formatting.Indented));
                File.WriteAllText(basePath + "_request.json", redacted, new UTF8Encoding(false));
                return basePath;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void EndTranscript(string basePath, string body, string error)
        {
            if (basePath == null) return;
            try
            {
                var text = body ?? string.Empty;
                if (error != null) text = "ERROR: " + error + "\n" + text;
                File.WriteAllText(basePath + "_response.json", Redact(text), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static string Redact(string text)
        {
            return string.IsNullOrEmpty(text) ? text : TokenShaped.Replace(text, "[REDACTED]");
        }

        private static string Summarize(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            var t = Redact(body.Trim());
            return t.Length > 240 ? t.Substring(0, 240) : t;
        }
    }
}
