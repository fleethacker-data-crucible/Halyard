using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Providers;

namespace Halyard.Core.Llm
{
    /// <summary>Fairlead is not answering on its address. Raised before a model job starts, so a run fails once with one clear reason.</summary>
    public sealed class FairleadOfflineException : Exception
    {
        public FairleadOfflineException(string message) : base(message) { }
    }

    public sealed class FairleadState
    {
        /// <summary>ready | offline</summary>
        public string State { get; set; }
        public string Url { get; set; }
        /// <summary>Whether a Fairlead folder with its start script was found, so Halyard can offer to start it.</summary>
        public bool CanStart { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Fairlead from Halyard's side: is it answering, and if not, start it. Every model call goes to Fairlead (fleet contract 1),
    /// so before a model job Halyard asks Fairlead's open <c>GET /v1/health</c> on the same host and port as the chat address.
    /// Any HTTP answer counts as running (a key or route problem is reported by the call itself); only no answer counts as down.
    /// Starting runs Fairlead's own <c>scripts\Start-Fairlead.ps1 --no-browser</c>: its tray app, the same as double-clicking
    /// Launch-Fairlead.bat, which reuses a running copy. Halyard never stops Fairlead; other tools depend on it.
    /// </summary>
    public sealed class Fairlead
    {
        public const int ProbeSeconds = 3;
        public const string StartScript = "scripts\\Start-Fairlead.ps1";

        private readonly LlmSettings _settings;
        private readonly IHttpTransport _http;
        private readonly string _folder;

        public Fairlead(LlmSettings settings, IHttpTransport http, string folder)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _http = http ?? new HttpClientTransport();
            _folder = folder;
        }

        /// <summary>Test seam: starts the process. Defaults to a hidden Windows PowerShell running the start script.</summary>
        public Func<ProcessStartInfo, bool> Launch { get; set; }

        /// <summary><c>scheme://host:port/v1/health</c> for the configured chat-completions address.</summary>
        public static string HealthUrl(string chatUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(LlmSettings.NormalizeUrl(chatUrl), UriKind.Absolute, out uri)) return null;
            return uri.GetLeftPart(UriPartial.Authority) + "/v1/health";
        }

        /// <summary>
        /// The Fairlead folder: HALYARD_FAIRLEAD_PATH, else a Fairlead folder next to Halyard, else %USERPROFILE%\Projects\Fairlead.
        /// It must hold Fairlead's start script.
        /// </summary>
        public static string Find(string configuredPath, string halyardRoot)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath.Trim());
            else
            {
                if (!string.IsNullOrEmpty(halyardRoot))
                {
                    var parent = Path.GetDirectoryName(Path.GetFullPath(halyardRoot));
                    if (!string.IsNullOrEmpty(parent)) candidates.Add(SafePath.Under(parent, "Fairlead"));
                }
                var profile = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(profile)) candidates.Add(SafePath.Under(profile, "Projects", "Fairlead"));
            }
            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, StartScript)));
        }

        public bool CanStart { get { return !string.IsNullOrEmpty(_folder); } }

        /// <summary>True when Fairlead answers its health address at all.</summary>
        public async Task<bool> IsUpAsync(CancellationToken cancellation)
        {
            var url = HealthUrl(_settings.FairleadUrl);
            if (url == null) return false;
            var call = new HttpCall("GET", url) { Timeout = TimeSpan.FromSeconds(ProbeSeconds) };
            call.Headers["Accept"] = "application/json";
            call.Headers["User-Agent"] = "Halyard/1.0";
            try
            {
                await _http.SendAsync(call, cancellation).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) throw; return false; }
            catch (Exception) { return false; }
        }

        public string OfflineMessage()
        {
            return "Fairlead is not running at " + HttpClientTransport.SafeUrl(_settings.Url()) + ", so model calls cannot be made. "
                + (CanStart ? "Start Fairlead (Halyard can start it for you) and try again." : "Start Fairlead (Launch-Fairlead.bat) and try again, or set HALYARD_FAIRLEAD_PATH so Halyard can start it.");
        }

        /// <summary>Throws <see cref="FairleadOfflineException"/> when Fairlead does not answer.</summary>
        public async Task EnsureUpAsync(CancellationToken cancellation)
        {
            if (!await IsUpAsync(cancellation).ConfigureAwait(false)) throw new FairleadOfflineException(OfflineMessage());
        }

        public async Task<FairleadState> StatusAsync(CancellationToken cancellation)
        {
            var up = await IsUpAsync(cancellation).ConfigureAwait(false);
            return new FairleadState { State = up ? "ready" : "offline", Url = HttpClientTransport.SafeUrl(_settings.Url()), CanStart = CanStart, Message = up ? "Fairlead is answering." : OfflineMessage() };
        }

        /// <summary>
        /// Starts Fairlead when it is not answering. Returns at once with "starting"; the page asks for the status until it is
        /// ready. Nothing is passed but <c>--no-browser</c>: no key, token or address goes on the command line.
        /// </summary>
        public async Task<FairleadState> StartAsync(CancellationToken cancellation)
        {
            if (await IsUpAsync(cancellation).ConfigureAwait(false))
                return new FairleadState { State = "ready", Url = HttpClientTransport.SafeUrl(_settings.Url()), CanStart = CanStart, Message = "Fairlead is already running." };
            if (!CanStart)
                return new FairleadState { State = "missing", Url = HttpClientTransport.SafeUrl(_settings.Url()), CanStart = false, Message = "Fairlead was not found next to Halyard or in %USERPROFILE%\\Projects\\Fairlead. Start it yourself, or set HALYARD_FAIRLEAD_PATH to its folder." };
            var system = Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows";
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(system, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-STA -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Path.Combine(_folder, StartScript) + "\" --no-browser",
                WorkingDirectory = _folder,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var launch = Launch ?? DefaultLaunch;
            bool started;
            try { started = launch(info); }
            catch (Exception ex) { started = false; return new FairleadState { State = "offline", Url = HttpClientTransport.SafeUrl(_settings.Url()), CanStart = true, Message = "Fairlead could not be started: " + ex.Message }; }
            return new FairleadState { State = started ? "starting" : "offline", Url = HttpClientTransport.SafeUrl(_settings.Url()), CanStart = true, Message = started ? "Starting Fairlead. Its tray icon appears when it is ready." : "Fairlead could not be started." };
        }

        private static bool DefaultLaunch(ProcessStartInfo info)
        {
            // Not kept or stopped by Halyard: Fairlead outlives it, because other tools use it too.
            using (var p = Process.Start(info)) { return p != null; }
        }
    }
}
