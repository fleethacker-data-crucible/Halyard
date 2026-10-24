using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cascade.Core.Llm
{
    public enum LlmProfile
    {
        Auto = 0,
        /// <summary>api.genai.mil, OpenAI-compatible chat completions.</summary>
        GenAi = 1,
        /// <summary>Databricks model-serving endpoint (Grok on Advana).</summary>
        Grok = 2,
        /// <summary>Any OpenAI-compatible endpoint (personal development).</summary>
        OpenAi = 3,
    }

    public sealed class LlmSettings
    {
        public LlmProfile Profile { get; set; } = LlmProfile.Auto;
        public string? GenAiUrl { get; set; }
        public string? GenAiModel { get; set; }
        public string? GenAiToken { get; set; }
        public string? GrokUrl { get; set; }
        public string? GrokModel { get; set; }
        public string? GrokToken { get; set; }
        public string? OpenAiUrl { get; set; }
        public string? OpenAiModel { get; set; }
        public string? OpenAiToken { get; set; }
        public int MaxTokens { get; set; } = 4000;
        public double Temperature { get; set; } = 0.2;
        public int TimeoutSeconds { get; set; } = 300;
        /// <summary>Directory for redacted request/response transcripts; null disables them.</summary>
        public string? TranscriptDirectory { get; set; }

        public const string DefaultGenAiUrl = "https://api.genai.mil/v1/chat/completions";
        public const string DefaultGenAiModel = "gemini-3.8-flash";
        public const string DefaultGrokUrl = "https://advana-jupiter.cloud.databricks.mil/serving-endpoints/databricks-grok-4-6/invocations";
        public const string DefaultGrokModel = "system.ai.grok-4-6";
        public const string DefaultOpenAiUrl = "https://api.openai.com/v1/chat/completions";
        public const string DefaultOpenAiModel = "gpt-4o-mini";

        /// <summary>Resolves Auto to a concrete profile from whichever token is configured.</summary>
        public LlmProfile Resolve()
        {
            if (Profile != LlmProfile.Auto) return Profile;
            if (!string.IsNullOrWhiteSpace(GenAiToken)) return LlmProfile.GenAi;
            if (!string.IsNullOrWhiteSpace(GrokToken)) return LlmProfile.Grok;
            if (!string.IsNullOrWhiteSpace(OpenAiToken)) return LlmProfile.OpenAi;
            return LlmProfile.GenAi;
        }

        public string Url(LlmProfile profile)
        {
            switch (profile)
            {
                case LlmProfile.Grok: return (string.IsNullOrWhiteSpace(GrokUrl) ? DefaultGrokUrl : GrokUrl!).TrimEnd('/');
                case LlmProfile.OpenAi: return (string.IsNullOrWhiteSpace(OpenAiUrl) ? DefaultOpenAiUrl : OpenAiUrl!).TrimEnd('/');
                default: return (string.IsNullOrWhiteSpace(GenAiUrl) ? DefaultGenAiUrl : GenAiUrl!).TrimEnd('/');
            }
        }

        public string Model(LlmProfile profile)
        {
            switch (profile)
            {
                case LlmProfile.Grok: return string.IsNullOrWhiteSpace(GrokModel) ? DefaultGrokModel : GrokModel!;
                case LlmProfile.OpenAi: return string.IsNullOrWhiteSpace(OpenAiModel) ? DefaultOpenAiModel : OpenAiModel!;
                default: return string.IsNullOrWhiteSpace(GenAiModel) ? DefaultGenAiModel : GenAiModel!;
            }
        }

        public string? Token(LlmProfile profile)
        {
            switch (profile)
            {
                case LlmProfile.Grok: return Clean(GrokToken);
                case LlmProfile.OpenAi: return Clean(OpenAiToken);
                default: return Clean(GenAiToken);
            }
        }

        public bool IsConfigured(LlmProfile profile)
        {
            return !string.IsNullOrWhiteSpace(Token(profile));
        }

        /// <summary>Strips quotes, a 'Bearer ' prefix and line breaks: the usual vault paste accidents.</summary>
        public static string? Clean(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var t = token!.Trim().Trim('\'', '"');
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
        public string Text { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string? TranscriptPath { get; set; }
    }

    public interface ILlmClient
    {
        LlmProfile Profile { get; }
        string Model { get; }
        Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation);
        Task<LlmResponse> TestAsync(CancellationToken cancellation);
    }

    /// <summary>
    /// One client for the three OpenAI-shaped endpoints Cascade talks to. Body shape differs slightly
    /// (Databricks has no <c>stream</c> flag); response parsing tolerates the variations government
    /// gateways return. Tokens live in memory and are never written to transcripts.
    /// </summary>
    public sealed class LlmClient : ILlmClient
    {
        private static readonly Regex TokenShaped = new Regex(@"\b(sk-[A-Za-z0-9_\-]{10,}|dapi[a-f0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|glpat-[A-Za-z0-9_\-]{15,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})\b", RegexOptions.Compiled);
        private readonly IHttpTransport _http;
        private readonly LlmSettings _settings;

        public LlmClient(LlmSettings settings, IHttpTransport? http = null, LlmProfile? profile = null)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");
            _http = http ?? new HttpClientTransport();
            Profile = profile ?? settings.Resolve();
            if (Profile == LlmProfile.Auto) Profile = LlmProfile.GenAi;
        }

        public LlmProfile Profile { get; private set; }
        public string Model { get { return _settings.Model(Profile); } }
        public string Url { get { return _settings.Url(Profile); } }

        public async Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation)
        {
            if (messages == null || messages.Count == 0) throw new ArgumentException("At least one message is required.", "messages");
            var token = _settings.Token(Profile);
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("No token is configured for the " + Profile + " profile. Unlock the vault or set the token environment variable.");

            var payload = new JObject
            {
                ["messages"] = new JArray(messages.Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Content })),
                ["max_tokens"] = _settings.MaxTokens,
                ["model"] = Model,
                ["temperature"] = _settings.Temperature,
            };
            if (Profile != LlmProfile.Grok) payload["stream"] = false;

            var call = new HttpCall("POST", Url) { JsonBody = payload.ToString(Formatting.None), Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds) };
            call.Headers["Authorization"] = "Bearer " + token;
            call.Headers["Accept"] = "application/json";
            call.Headers["User-Agent"] = "Cascade/1.0";

            var started = DateTimeOffset.UtcNow;
            var transcript = BeginTranscript(payload);
            HttpReply reply;
            try
            {
                reply = await _http.SendAsync(call, cancellation).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex)
            {
                EndTranscript(transcript, null, ex.Message);
                throw;
            }
            if (!reply.IsSuccess)
            {
                EndTranscript(transcript, reply.Body, "HTTP " + reply.Status);
                throw new ProviderHttpException(reply.Status, "LLM endpoint " + HttpClientTransport.SafeUrl(Url) + " returned HTTP " + reply.Status + ": " + Summarize(reply.Body), reply.Body);
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

        public Task<LlmResponse> TestAsync(CancellationToken cancellation)
        {
            return ChatAsync(new[] { LlmMessage.System("You are a connectivity probe. Reply with exactly: OK"), LlmMessage.User("Reply with OK.") }, cancellation);
        }

        /// <summary>Finds the assistant text in the response shapes seen across gateways.</summary>
        public static string ExtractText(JToken? response)
        {
            if (response == null) return string.Empty;
            var choices = response["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var msg = choices[0]["message"];
                if (msg != null)
                {
                    var content = msg["content"];
                    if (content is JArray parts)
                    {
                        var sb = new StringBuilder();
                        foreach (var p in parts)
                        {
                            if (p.Type == JTokenType.String) sb.Append((string?)p);
                            else sb.Append((string?)p["text"] ?? (string?)p["content"] ?? string.Empty);
                        }
                        if (sb.Length > 0) return sb.ToString().Trim();
                    }
                    else if (content != null && content.Type != JTokenType.Null)
                    {
                        var s = (string?)content;
                        if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                    }
                    foreach (var alt in new[] { "reasoning_content", "reasoning", "text", "output_text", "refusal" })
                    {
                        var s = (string?)msg[alt];
                        if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                    }
                }
                var plain = (string?)choices[0]["text"];
                if (!string.IsNullOrWhiteSpace(plain)) return plain!.Trim();
            }
            // Databricks / generic shapes
            foreach (var alt in new[] { "output_text", "text", "content", "result", "prediction" })
            {
                var s = response[alt];
                if (s != null && s.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)s)) return ((string)s!).Trim();
            }
            var predictions = response["predictions"] as JArray;
            if (predictions != null && predictions.Count > 0)
            {
                var s = predictions[0].Type == JTokenType.String ? (string?)predictions[0] : (string?)predictions[0]["candidates"]?[0]?["text"];
                if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
            }
            return string.Empty;
        }

        private string? BeginTranscript(JObject request)
        {
            var dir = _settings.TranscriptDirectory;
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                Directory.CreateDirectory(dir!);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                var basePath = Path.Combine(dir!, stamp + "_" + Profile.ToString().ToLowerInvariant());
                var redacted = Redact(request.ToString(Formatting.Indented));
                File.WriteAllText(basePath + "_request.json", redacted, new UTF8Encoding(false));
                return basePath;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void EndTranscript(string? basePath, string? body, string? error)
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

        private static string Summarize(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            var t = Redact(body!.Trim());
            return t.Length > 240 ? t.Substring(0, 240) : t;
        }
    }
}
