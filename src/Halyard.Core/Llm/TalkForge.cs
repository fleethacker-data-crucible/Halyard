using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Llm
{
    public sealed class TalkForgeVoice
    {
        public string Id { get; set; }
        public string Quality { get; set; }
        public int Speakers { get; set; }
        public string Language { get; set; }
        public bool Default { get; set; }
    }

    public sealed class TalkForgeVoices
    {
        public bool Found { get; set; }
        public string DefaultVoice { get; set; }
        public List<TalkForgeVoice> Voices { get; set; }
        public string Message { get; set; }

        public TalkForgeVoices()
        {
            DefaultVoice = string.Empty;
            Voices = new List<TalkForgeVoice>();
            Message = string.Empty;
        }
    }

    public sealed class TalkForgeState
    {
        /// <summary>ready | starting | offline | missing</summary>
        public string State { get; set; }
        public string Message { get; set; }
    }

    /// <summary>TalkForge is not answering on its port (or answers without Halyard's token); the page starts it and tries again.</summary>
    public sealed class TalkForgeOfflineException : Exception
    {
        public TalkForgeOfflineException(string message) : base(message) { }
    }

    /// <summary>
    /// Spoken answers through TalkForge (Piper neural voices, on this PC), the way Lazarette does it: the engine talks to TalkForge's
    /// loopback <c>/v1/audio/speech</c> and starts TalkForge itself when it is not running. Halyard runs its own copy on port 25008
    /// (TalkForge's block is 25000-25009; Lazarette uses 25009) with a random token made for this run, which lives only in memory
    /// and in that child's environment: never on a command line, in a file or in a log. The copy stops by itself after 30 idle
    /// minutes and is asked to stop when Halyard stops. The text is never logged.
    /// </summary>
    public sealed class TalkForge : IDisposable
    {
        public const int DefaultPort = 25008;
        public const int MaxChars = 4000;
        public const int StartSeconds = 45;
        public const int IdleMinutes = 30;
        private static readonly Regex VoiceId = new Regex(@"^[A-Za-z0-9._\-]{1,80}$", RegexOptions.Compiled);

        private readonly string _folder;
        private readonly int _port;
        private readonly IHttpTransport _http;
        private readonly object _gate = new object();
        private string _token;
        private Process _process;
        private DateTimeOffset _startedAt;
        private string _lastError;

        public TalkForge(string folder, int port, IHttpTransport http)
        {
            _folder = folder;
            _port = port > 0 ? port : DefaultPort;
            _http = http ?? new HttpClientTransport();
        }

        public string Folder { get { return _folder; } }
        public int Port { get { return _port; } }
        public string BaseUrl { get { return "http://127.0.0.1:" + _port; } }
        /// <summary>Where shutdown problems are reported (the host's log). Never given the token or any text.</summary>
        public Action<string> Log { get; set; }

        private void Note(string message)
        {
            var log = Log;
            if (log != null) log(message);
        }

        /// <summary>A token for a TalkForge someone else started (HALYARD_TALKFORGE_TOKEN), read when needed.</summary>
        public Func<string> ExternalToken { get; set; }

        /// <summary>Voices with both <c>assets\models\&lt;id&gt;.onnx</c> and its <c>.onnx.json</c>, TalkForge's default first. TalkForge need not run.</summary>
        public TalkForgeVoices ListVoices()
        {
            var result = new TalkForgeVoices();
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder)) { result.Message = "TalkForge was not found, so its voices cannot be listed. Set HALYARD_TALKFORGE_PATH."; return result; }
            result.Found = true;
            var engine = SafePath.Under(_folder, "tools", "node-engine.mjs");
            if (File.Exists(engine))
            {
                var m = Regex.Match(File.ReadAllText(engine), @"DEFAULT_VOICE\s*=\s*'([A-Za-z0-9._\-]{1,80})'");
                if (m.Success) result.DefaultVoice = m.Groups[1].Value;
            }
            var models = SafePath.Under(_folder, "assets", "models");
            if (Directory.Exists(models))
            {
                foreach (var f in Directory.GetFiles(models, "*.onnx").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var id = Path.GetFileNameWithoutExtension(f);
                    if (!VoiceId.IsMatch(id) || !File.Exists(f + ".json")) continue;
                    string t;
                    try { t = File.ReadAllText(f + ".json"); } catch (IOException) { continue; }
                    // Read only the few fields shown: Piper configs are large and carry phoneme maps.
                    Func<string, string> get = p => { var m = Regex.Match(t, p); return m.Success ? m.Groups[1].Value : string.Empty; };
                    int speakers;
                    if (!int.TryParse(get("\"num_speakers\"\\s*:\\s*(\\d{1,6})"), out speakers)) speakers = 1;
                    var language = string.Join(", ", new[] { get("\"name_english\"\\s*:\\s*\"([A-Za-z ()-]{1,40})\""), get("\"country_english\"\\s*:\\s*\"([A-Za-z ()-]{1,40})\"") }.Where(x => x.Length > 0));
                    result.Voices.Add(new TalkForgeVoice { Id = id, Quality = get("\"quality\"\\s*:\\s*\"([A-Za-z_]{1,20})\""), Speakers = speakers, Language = language, Default = id == result.DefaultVoice });
                }
            }
            result.Voices = result.Voices.Where(v => v.Default).Concat(result.Voices.Where(v => !v.Default)).ToList();
            if (result.Voices.Count == 0) result.Message = "TalkForge has no voices installed. In the TalkForge folder run: populate-assets.ps1 -Only 'models/en_US-libritts_r-medium*'";
            return result;
        }

        /// <summary>True when TalkForge answers its lifecycle probe on the port (no token needed).</summary>
        public async Task<bool> HeartbeatAsync(CancellationToken cancellation)
        {
            try
            {
                var reply = await _http.SendAsync(new HttpCall("GET", BaseUrl + "/__fleethacker/heartbeat") { Timeout = TimeSpan.FromMilliseconds(900) }, cancellation).ConfigureAwait(false);
                if (!reply.IsSuccess || string.IsNullOrWhiteSpace(reply.Body)) return false;
                var o = JToken.Parse(reply.Body) as JObject;
                if (o == null) return false;
                // Older TalkForge builds answer { ok: true } without a tool name; accept those too.
                var tool = (string)o["tool"];
                return tool == null ? o["ok"] != null : tool == "talkforge";
            }
            catch (ProviderHttpException) { return false; }
            catch (JsonException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        private bool Starting
        {
            get
            {
                lock (_gate) return _process != null && !_process.HasExited && DateTimeOffset.UtcNow - _startedAt < TimeSpan.FromSeconds(StartSeconds);
            }
        }

        private string Token()
        {
            lock (_gate) if (_token != null && _process != null && !_process.HasExited) return _token;
            var external = ExternalToken == null ? null : ExternalToken();
            return string.IsNullOrWhiteSpace(external) ? null : external.Trim();
        }

        public async Task<TalkForgeState> StateAsync(CancellationToken cancellation)
        {
            if (await HeartbeatAsync(cancellation).ConfigureAwait(false))
            {
                if (Token() == null) return new TalkForgeState { State = "offline", Message = "Another TalkForge is on port " + _port + " and Halyard has no token for it. Set HALYARD_TALKFORGE_PORT to a free port in 25000-25009, or HALYARD_TALKFORGE_TOKEN." };
                return new TalkForgeState { State = "ready", Message = "TalkForge is running." };
            }
            if (Starting) return new TalkForgeState { State = "starting", Message = "Getting the voice ready..." };
            lock (_gate)
            {
                if (_process != null && _process.HasExited && _lastError == null) _lastError = "TalkForge stopped while starting (exit code " + _process.ExitCode + "). Run talkforge serve in a terminal to see why.";
            }
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder)) return new TalkForgeState { State = "missing", Message = "TalkForge was not found. Set HALYARD_TALKFORGE_PATH to the TalkForge folder." };
            return new TalkForgeState { State = "offline", Message = _lastError ?? "TalkForge is not running." };
        }

        /// <summary>Starts TalkForge unless it is running or starting. Returns the state as <see cref="StateAsync"/> does.</summary>
        public async Task<TalkForgeState> StartAsync(CancellationToken cancellation)
        {
            var now = await StateAsync(cancellation).ConfigureAwait(false);
            if (now.State != "offline" || await HeartbeatAsync(cancellation).ConfigureAwait(false)) return now;
            if (string.IsNullOrEmpty(_folder)) return new TalkForgeState { State = "missing", Message = "TalkForge was not found." };
            var script = SafePath.Under(_folder, "tools", "talkforge.mjs");
            if (!File.Exists(script)) return new TalkForgeState { State = "missing", Message = "TalkForge's tools\\talkforge.mjs was not found in " + _folder + "." };
            var node = FindNode();
            if (node == null) return new TalkForgeState { State = "offline", Message = "TalkForge needs Node.js 18 or newer, and node was not found." };
            lock (_gate)
            {
                _lastError = null;
                _token = NewToken();
                var si = new ProcessStartInfo
                {
                    FileName = node,
                    Arguments = "\"" + script + "\" serve --port " + _port + " --idle-timeout " + IdleMinutes,
                    WorkingDirectory = _folder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var name in new[] { "TALKFORGE_TOKEN_FILE", "TALKFORGE_LAZARETTE_ITEM" }.Where(n => si.EnvironmentVariables.ContainsKey(n))) si.EnvironmentVariables.Remove(name);
                si.EnvironmentVariables["TALKFORGE_TOKEN"] = _token;
                try { _process = Process.Start(si); _startedAt = DateTimeOffset.UtcNow; }
                catch (System.ComponentModel.Win32Exception ex) { _process = null; _token = null; return new TalkForgeState { State = "offline", Message = "TalkForge could not be started: " + ex.Message }; }
            }
            return new TalkForgeState { State = "starting", Message = "Getting the voice ready..." };
        }

        public static string Payload(string text, string voice, double speed)
        {
            var p = new JObject
            {
                { "input", text },
                { "response_format", "wav" },
                { "speed", Math.Round(Math.Min(2.0, Math.Max(0.5, speed <= 0 ? 1.0 : speed)), 2) },
            };
            if (!string.IsNullOrWhiteSpace(voice)) p["voice"] = voice.Trim();
            return p.ToString(Formatting.None);
        }

        /// <summary>WAV audio for one piece of text. Throws <see cref="TalkForgeOfflineException"/> when TalkForge is not answering.</summary>
        public async Task<byte[]> SpeakAsync(string text, string voice, double speed, CancellationToken cancellation)
        {
            var t = (text ?? string.Empty).Trim();
            if (t.Length == 0 || t.Length > MaxChars) throw new ArgumentException("Send 1-" + MaxChars + " characters of text.", "text");
            if (!string.IsNullOrWhiteSpace(voice) && !VoiceId.IsMatch(voice.Trim())) throw new ArgumentException("That is not a voice id.", "voice");
            var token = Token();
            if (token == null) throw new TalkForgeOfflineException("TalkForge is not running.");
            var call = new HttpCall("POST", BaseUrl + "/v1/audio/speech") { JsonBody = Payload(t, voice, speed), Timeout = TimeSpan.FromSeconds(60), BinaryResponse = true };
            call.Headers["Authorization"] = "Bearer " + token;
            call.Headers["Accept"] = "audio/wav";
            HttpReply reply;
            try { reply = await _http.SendAsync(call, cancellation).ConfigureAwait(false); }
            catch (ProviderHttpException ex)
            {
                if (ex.Status == 0) throw new TalkForgeOfflineException("TalkForge is not running.");
                throw;
            }
            if (reply.Status == 401) throw new TalkForgeOfflineException("TalkForge on port " + _port + " did not accept Halyard's token.");
            if (!reply.IsSuccess) throw new ProviderHttpException(reply.Status, "TalkForge could not speak (HTTP " + reply.Status + ").", null);
            return reply.BodyBytes ?? new byte[0];
        }

        /// <summary>Asks the TalkForge this engine started to stop (the token and the intent header), then ends it if it lingers.</summary>
        public void Dispose()
        {
            Process p;
            string token;
            lock (_gate) { p = _process; token = _token; _process = null; _token = null; }
            if (p == null) return;
            using (p)
            {
                try
                {
                    if (!p.HasExited && token != null)
                    {
                        var call = new HttpCall("POST", BaseUrl + "/__fleethacker/shutdown") { JsonBody = "{}", Timeout = TimeSpan.FromSeconds(2) };
                        call.Headers["Authorization"] = "Bearer " + token;
                        call.Headers["X-FleetHacker-Shutdown"] = "1";
                        try { _http.SendAsync(call, CancellationToken.None).Wait(2500); }
                        catch (AggregateException ex) { Note("TalkForge did not take the shutdown request (" + (ex.InnerException ?? ex).GetType().Name + "); stopping its process."); }
                        if (!p.WaitForExit(2000)) { Note("TalkForge (PID " + p.Id + ") did not stop in time; ending it."); p.Kill(); }
                    }
                }
                catch (InvalidOperationException) { Note("TalkForge had already exited."); }
                catch (System.ComponentModel.Win32Exception ex) { Note("TalkForge could not be ended: " + ex.Message); }
            }
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>node.exe on PATH, else in Program Files\nodejs.</summary>
        public static string FindNode()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';').Where(d => d.Trim().Length > 0).ToList();
            var pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(pf)) dirs.Add(SafePath.Under(pf, "nodejs"));
            foreach (var d in dirs)
            {
                try { var p = SafePath.Under(d.Trim().Trim('"'), "node.exe"); if (File.Exists(p)) return p; }
                catch (ArgumentException) { continue; }   // a PATH entry with characters a path cannot hold: skip it
                catch (NotSupportedException) { continue; }   // .NET Framework: a malformed entry such as C::b
            }
            return null;
        }
    }
}
