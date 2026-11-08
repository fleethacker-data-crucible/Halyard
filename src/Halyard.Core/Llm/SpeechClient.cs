using System;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Llm
{
    /// <summary>
    /// Spoken answers: one piece of text to Fairlead's <c>/v1/audio/speech</c> on this PC, WAV audio back. Fairlead routes the
    /// call to TalkForge (Piper voices, on this machine), so Halyard needs no TalkForge token of its own: it uses its Fairlead key
    /// from Credential Manager, the same as for chat. The text is never logged or written to a transcript.
    /// </summary>
    public sealed class SpeechClient
    {
        public const int MaxChars = 4000;
        public const string DefaultRoute = "speech";
        private readonly LlmSettings _settings;
        private readonly IHttpTransport _http;

        public SpeechClient(LlmSettings settings, IHttpTransport http = null)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _http = http ?? new HttpClientTransport();
        }

        /// <summary>The speech address next to the chat address: <c>.../v1/chat/completions</c> becomes <c>.../v1/audio/speech</c>.</summary>
        public static string SpeechUrl(string chatUrl)
        {
            var u = LlmSettings.NormalizeUrl(chatUrl);
            const string tail = "/chat/completions";
            return u.Substring(0, u.Length - tail.Length) + "/audio/speech";
        }

        public string Url { get { return SpeechUrl(_settings.FairleadUrl); } }
        public string Route { get { return string.IsNullOrWhiteSpace(_settings.SpeechRoute) ? DefaultRoute : _settings.SpeechRoute.Trim(); } }

        public static string Payload(string text, string route, string voice, double speed)
        {
            var p = new JObject
            {
                { "model", route },
                { "input", text },
                { "response_format", "wav" },
                { "speed", Math.Round(Math.Min(2.0, Math.Max(0.5, speed <= 0 ? 1.0 : speed)), 2) },
            };
            if (!string.IsNullOrWhiteSpace(voice)) p["voice"] = voice.Trim();
            return p.ToString(Formatting.None);
        }

        public async Task<byte[]> SpeakAsync(string text, CancellationToken cancellation)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Length == 0 || t.Length > MaxChars) throw new ArgumentException("Send 1-" + MaxChars + " characters of text.", "text");
            var missing = _settings.MissingFor();
            if (missing != null) throw new InvalidOperationException("Spoken answers are not usable: " + missing);
            var call = new HttpCall("POST", Url) { JsonBody = Payload(t, Route, _settings.SpeechVoice, _settings.SpeechSpeed), Timeout = TimeSpan.FromSeconds(60), BinaryResponse = true };
            call.Headers["Authorization"] = "Bearer " + _settings.Key();
            call.Headers["X-Fairlead-Label"] = LlmSettings.AppName;
            // Speech never leaves this machine: TalkForge is local, so ask Fairlead for local engines only.
            call.Headers["X-Fairlead-Privacy"] = "local";
            call.Headers["Accept"] = "audio/wav";
            call.Headers["User-Agent"] = "Halyard/1.0";
            HttpReply reply;
            try { reply = await _http.SendAsync(call, cancellation).ConfigureAwait(false); }
            catch (ProviderHttpException ex)
            {
                if (ex.Status == 0) throw new ProviderHttpException(0, "Fairlead did not answer at " + HttpClientTransport.SafeUrl(Url) + ". Start Fairlead (and TalkForge) and try again.", null);
                throw;
            }
            if (!reply.IsSuccess)
            {
                if (reply.Status == 401 || reply.Status == 403) throw new ProviderHttpException(reply.Status, "Fairlead did not accept the Halyard key. Open Fairlead and install a new key for Halyard.", null);
                throw new ProviderHttpException(reply.Status, "Fairlead could not speak (HTTP " + reply.Status + "). Check that the '" + Route + "' route points at TalkForge and that TalkForge is running.", null);
            }
            return reply.BodyBytes ?? new byte[0];
        }
    }
}
