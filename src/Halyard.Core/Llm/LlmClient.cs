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
    public enum LlmProfile
    {
        Auto = 0,
        /// <summary>Any OpenAI-compatible chat-completions endpoint: api.openai.com, Azure OpenAI, an organisation gateway, a local server.</summary>
        OpenAi = 1,
        /// <summary>Databricks model-serving endpoint (<c>/serving-endpoints/{name}/invocations</c>; no <c>stream</c> flag).</summary>
        Databricks = 2,
    }

    public sealed class LlmSettings
    {
        public LlmProfile Profile { get; set; }
        public string OpenAiUrl { get; set; }
        public string OpenAiModel { get; set; }
        public string OpenAiToken { get; set; }
        public string DatabricksUrl { get; set; }
        public string DatabricksModel { get; set; }
        public string DatabricksToken { get; set; }
        public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        public int TimeoutSeconds { get; set; }
        /// <summary>Directory for redacted request/response transcripts; null disables them.</summary>
        public string TranscriptDirectory { get; set; }

        public const string DefaultOpenAiUrl = "https://api.openai.com/v1/chat/completions";
        public const string DefaultOpenAiModel = "gpt-4o-mini";

        public static readonly LlmProfile[] Concrete = { LlmProfile.OpenAi, LlmProfile.Databricks };

        public LlmSettings()
        {
            Profile = LlmProfile.Auto;
            MaxTokens = 4000;
            Temperature = 0.2;
            TimeoutSeconds = 300;
        }

        public static LlmProfile? ParseProfile(string text)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "": case "auto": return LlmProfile.Auto;
                case "openai": case "openai-compatible": case "gateway": return LlmProfile.OpenAi;
                case "databricks": return LlmProfile.Databricks;
                default: return null;
            }
        }

        public static string ProfileToken(LlmProfile profile)
        {
            return profile.ToString().ToLowerInvariant();
        }

        /// <summary>Resolves Auto to a concrete profile from whichever token is configured; OpenAI-compatible wins ties.</summary>
        public LlmProfile Resolve()
        {
            if (Profile != LlmProfile.Auto) return Profile;
            if (!string.IsNullOrWhiteSpace(OpenAiToken)) return LlmProfile.OpenAi;
            if (!string.IsNullOrWhiteSpace(DatabricksToken)) return LlmProfile.Databricks;
            return LlmProfile.OpenAi;
        }

        public string Url(LlmProfile profile)
        {
            switch (profile)
            {
                case LlmProfile.Databricks: return (DatabricksUrl ?? string.Empty).Trim().TrimEnd('/');
                default: return NormalizeOpenAiUrl(OpenAiUrl);
            }
        }

        /// <summary>
        /// Accepts a full chat-completions URL or a base such as <c>https://api.openai.com/v1</c> (the shape
        /// OPENAI_BASE_URL uses) and returns the chat-completions URL.
        /// </summary>
        public static string NormalizeOpenAiUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return DefaultOpenAiUrl;
            var u = url.Trim().TrimEnd('/');
            if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) || u.EndsWith("/invocations", StringComparison.OrdinalIgnoreCase)) return u;
            if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || u.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)) return u + "/chat/completions";
            return u + "/v1/chat/completions";
        }

        public string Model(LlmProfile profile)
        {
            switch (profile)
            {
                case LlmProfile.Databricks: return (DatabricksModel ?? string.Empty).Trim();
                default: return string.IsNullOrWhiteSpace(OpenAiModel) ? DefaultOpenAiModel : OpenAiModel.Trim();
            }
        }

        public string Token(LlmProfile profile)
        {
            switch (profile)
            {
                case LlmProfile.Databricks: return Clean(DatabricksToken);
                default: return Clean(OpenAiToken);
            }
        }

        public bool IsConfigured(LlmProfile profile)
        {
            if (string.IsNullOrWhiteSpace(Token(profile))) return false;
            if (profile == LlmProfile.Databricks) return !string.IsNullOrWhiteSpace(DatabricksUrl);
            return true;
        }

        /// <summary>Why a profile is not usable, or null when it is.</summary>
        public string MissingFor(LlmProfile profile)
        {
            if (profile == LlmProfile.Databricks)
            {
                if (string.IsNullOrWhiteSpace(DatabricksUrl)) return "DATABRICKS_URL is not set.";
                if (string.IsNullOrWhiteSpace(Token(profile))) return "DATABRICKS_TOKEN is not set.";
                return null;
            }
            return string.IsNullOrWhiteSpace(Token(profile)) ? "No API key: set LLM_TOKEN (or OPENAI_API_KEY) in config/halyard.env, or enter one in Settings." : null;
        }

        /// <summary>Strips quotes, a 'Bearer ' prefix and line breaks: the usual vault paste accidents.</summary>
        public static string Clean(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var t = token.Trim().Trim('\'', '"');
            var m = Regex.Match(t, @"^(?i)Bearer\s+(.+)$");
            if (m.Success) t = m.Groups[1].Value.Trim();
            return t.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
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
        public string Profile { get; set; }
        public string Model { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string TranscriptPath { get; set; }

        public LlmResponse()
        {
            Text = string.Empty;
            Profile = string.Empty;
            Model = string.Empty;
        }
    }

    public interface ILlmClient
    {
        LlmProfile Profile { get; }
        string Model { get; }
        Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation);
        Task<LlmResponse> TestAsync(CancellationToken cancellation);
    }

    /// <summary>
    /// One client for the OpenAI-shaped endpoints Halyard talks to. Body shape differs slightly
    /// (Databricks has no <c>stream</c> flag); response parsing tolerates the variations gateways
    /// return. Tokens live in memory and are never written to transcripts.
    /// </summary>
    public sealed class LlmClient : ILlmClient
    {
        private static readonly Regex TokenShaped = new Regex(@"\b(sk-[A-Za-z0-9_\-]{10,}|dapi[a-f0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|glpat-[A-Za-z0-9_\-]{15,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})\b", RegexOptions.Compiled);
        private readonly IHttpTransport _http;
        private readonly LlmSettings _settings;

        public LlmClient(LlmSettings settings, IHttpTransport http = null, LlmProfile? profile = null)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _http = http ?? new HttpClientTransport();
            Profile = profile ?? settings.Resolve();
            if (Profile == LlmProfile.Auto) Profile = LlmProfile.OpenAi;
        }

        public LlmProfile Profile { get; private set; }
        public string Model { get { return _settings.Model(Profile); } }
        public string Url { get { return _settings.Url(Profile); } }

        public async Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("At least one message is required.", "messages");
            var token = _settings.Token(Profile);
            var missing = _settings.MissingFor(Profile);
            if (missing != null) throw new InvalidOperationException("LLM profile " + LlmSettings.ProfileToken(Profile) + " is not usable: " + missing);

            var payload = new JObject
            {
                { "messages", new JArray(messages.Select(m => new JObject { { "role", m.Role }, { "content", m.Content } })) },
                { "model", Model },
                { "temperature", _settings.Temperature },
            };
            // OpenAI's current chat models take max_completion_tokens; Databricks and older gateways take max_tokens.
            payload[Profile == LlmProfile.Databricks ? "max_tokens" : "max_completion_tokens"] = _settings.MaxTokens;
            if (Profile != LlmProfile.Databricks) payload["stream"] = false;

            var started = DateTimeOffset.UtcNow;
            string transcript = null;
            HttpReply reply;
            // Endpoints disagree about which parameters they accept (max_tokens vs max_completion_tokens, temperature
            // on reasoning models). When the endpoint names the parameter it rejected, drop or rename it and retry.
            for (var attempt = 0; ; attempt++)
            {
                var call = new HttpCall("POST", Url) { JsonBody = payload.ToString(Formatting.None), Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds) };
                call.Headers["Authorization"] = "Bearer " + token;
                call.Headers["Accept"] = "application/json";
                call.Headers["User-Agent"] = "Halyard/1.0";
                transcript = BeginTranscript(payload);
                try
                {
                    reply = await _http.SendAsync(call, cancellation).ConfigureAwait(false);
                }
                catch (ProviderHttpException ex)
                {
                    EndTranscript(transcript, null, ex.Message);
                    throw;
                }
                if (reply.IsSuccess) break;
                EndTranscript(transcript, reply.Body, "HTTP " + reply.Status);
                var rejected = attempt < 3 && reply.Status == 400 ? RejectedParameter(reply.Body, payload) : null;
                if (rejected == null)
                    throw new ProviderHttpException(reply.Status, "LLM endpoint " + HttpClientTransport.SafeUrl(Url) + " returned HTTP " + reply.Status + ": " + Summarize(reply.Body), reply.Body);
                if (rejected == "max_completion_tokens") { payload.Remove("max_completion_tokens"); payload["max_tokens"] = _settings.MaxTokens; }
                else if (rejected == "max_tokens") { payload.Remove("max_tokens"); payload["max_completion_tokens"] = _settings.MaxTokens; }
                else payload.Remove(rejected);
            }
            var json = JToken.Parse(string.IsNullOrWhiteSpace(reply.Body) ? "{}" : reply.Body);
            var text = ExtractText(json);
            EndTranscript(transcript, reply.Body, null);
            var usage = json["usage"] as JObject;
            return new LlmResponse
            {
                Text = text,
                Profile = Profile.ToString(),
                Model = Model,
                PromptTokens = usage == null ? null : (int?)usage["prompt_tokens"],
                CompletionTokens = usage == null ? null : (int?)usage["completion_tokens"],
                Elapsed = DateTimeOffset.UtcNow - started,
                TranscriptPath = transcript,
            };
        }

        /// <summary>
        /// The request parameter an OpenAI-style 400 complains about, when it is one we sent and can live without.
        /// Reads the error's <c>param</c> field first, then the message text.
        /// </summary>
        public static string RejectedParameter(string body, JObject payload)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            var candidates = new[] { "max_tokens", "max_completion_tokens", "temperature", "stream" };
            string param = null;
            string message = body;
            try
            {
                var err = JObject.Parse(body)["error"];
                if (err != null)
                {
                    param = (string)err["param"];
                    message = (string)err["message"] ?? body;
                }
            }
            catch (JsonException) { }
            if (param != null && Array.IndexOf(candidates, param) >= 0 && payload[param] != null) return param;
            if (!Regex.IsMatch(message, @"(?i)unsupported|not supported|does not support|unknown parameter|unrecognized")) return null;
            foreach (var c in candidates)
                if (payload[c] != null && Regex.IsMatch(message, @"\b" + Regex.Escape(c) + @"\b")) return c;
            return null;
        }

        public Task<LlmResponse> TestAsync(CancellationToken cancellation)
        {
            return ChatAsync(new[] { LlmMessage.System("You are a connectivity probe. Reply with exactly: OK"), LlmMessage.User("Reply with OK.") }, cancellation);
        }

        /// <summary>Finds the assistant text in the response shapes seen across gateways.</summary>
        /// <summary><c>candidates[0].text</c>, or null when either level is missing.</summary>
        private static string CandidateText(JToken candidates)
        {
            var first = candidates == null ? null : candidates[0];
            return first == null ? null : (string)first["text"];
        }

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
            // Databricks / generic shapes
            foreach (var alt in new[] { "output_text", "text", "content", "result", "prediction" })
            {
                var s = response[alt];
                if (s != null && s.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)s)) return ((string)s).Trim();
            }
            var predictions = response["predictions"] as JArray;
            if (predictions != null && predictions.Count > 0)
            {
                var s = predictions[0].Type == JTokenType.String ? (string)predictions[0] : CandidateText(predictions[0]["candidates"]);
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
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
                var basePath = Path.Combine(dir, stamp + "_" + Profile.ToString().ToLowerInvariant());
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
