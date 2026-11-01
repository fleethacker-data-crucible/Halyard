using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Intelligence;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Schema;
using Halyard.Core.Serialization;
using Halyard.Core.Snapshots;

namespace Halyard.Core.Host
{
    public sealed class HostOptions
    {
        public HalyardSettings Settings { get; set; }
        /// <summary>Directory holding the built UI (index.html). When missing, the API still runs and / returns a notice.</summary>
        public string WebRoot { get; set; }
        public int PreferredPort { get; set; }
        /// <summary>Random per-session token the UI must send as X-Halyard-Session. Generated when null.</summary>
        public string SessionToken { get; set; }
        /// <summary>Free-form state from the PowerShell launcher (Lazarette vault unlocked, console hidden...).</summary>
        public Dictionary<string, object> LauncherState { get; set; }
        public IHttpTransport Transport { get; set; }
        /// <summary>
        /// Stop the engine once the browser tab that drives it has gone: the UI sends a heartbeat every few seconds
        /// and a "leaving" beacon on unload; with no heartbeat for <see cref="HeartbeatTimeout"/> (and no job running)
        /// the host stops itself. HALYARD_AUTO_STOP=0 or --stay turns this off.
        /// </summary>
        public bool AutoStop { get; set; }
        public TimeSpan HeartbeatTimeout { get; set; }
        /// <summary>
        /// Timeout while the tab reports itself hidden or frozen: background tabs get timers about once a minute and
        /// sleeping tabs none, so the visible-tab timeout would stop an engine whose tab is still open.
        /// </summary>
        public TimeSpan HiddenHeartbeatTimeout { get; set; }
        /// <summary>Grace after a "leaving" beacon before stopping; a reload sends a new heartbeat well inside it.</summary>
        public TimeSpan LeavingGrace { get; set; }

        public HostOptions()
        {
            Settings = new HalyardSettings();
            PreferredPort = 8773;
            LauncherState = new Dictionary<string, object>();
            AutoStop = true;
            HeartbeatTimeout = TimeSpan.FromSeconds(20);
            HiddenHeartbeatTimeout = TimeSpan.FromMinutes(60);
            LeavingGrace = TimeSpan.FromSeconds(5);
        }
    }

    public sealed class HostInfo
    {
        public int Port { get; set; }
        public string Url { get; set; }
        public string SessionToken { get; set; }
        /// <summary>URL with the session token, for opening the browser once.</summary>
        public string LaunchUrl { get; set; }
        public string LogFile { get; set; }
        public string RuntimeFile { get; set; }

        public HostInfo()
        {
            Url = string.Empty;
            SessionToken = string.Empty;
            LaunchUrl = string.Empty;
        }
    }

    /// <summary>
    /// Composition root: owns settings, provider, write gateway, stores and the HTTP server. PowerShell creates
    /// one of these, calls <see cref="Start"/>, opens the browser and waits on <see cref="WaitForStop"/>.
    /// </summary>
    public sealed partial class HalyardHost : IDisposable
    {
        public const string SessionHeader = "X-Halyard-Session";
        public const string IntentHeader = "X-Halyard-Intent";
        public const string Version = "1.1.0";
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly object _providerLock = new object();
        private HttpServer _server;
        private IWorkItemProvider _provider;
        private ProviderIdentity _identity;
        private string _providerError;
        private string _runtimeFile;

        public HalyardHost(HostOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            Options = options;
            Settings = options.Settings;
            Log = new Diagnostics(Settings.LogDirectory);
            Jobs = new JobManager(Log);
            Jobs.Lease = new OperatorLease(System.IO.Path.Combine(Settings.OutputDirectory, "write.lease"), OperatorLease.DefaultHolder(), TimeSpan.FromHours(2));
            Gateway = new WriteGateway(Settings.BuildWritePolicy());
            Snapshots = new SnapshotStore(Settings.SnapshotsDirectory);
            Proposals = new ProposalStore(Settings.ProposalsDirectory);
            Journal = new ApplyJournal(Settings.ApplyJournalDirectory);
            Visions = new VisionStore(Settings.VisionDirectory);
            Taxonomy = Schema.Taxonomy.Load(Settings.TaxonomyPath);
            SessionToken = string.IsNullOrEmpty(options.SessionToken) ? NewToken() : options.SessionToken;
            Router = new Router();
            StartedAt = DateTimeOffset.UtcNow;
            RegisterRoutes();
        }

        public HostOptions Options { get; private set; }
        public HalyardSettings Settings { get; private set; }
        public Diagnostics Log { get; private set; }
        public JobManager Jobs { get; private set; }
        public WriteGateway Gateway { get; private set; }
        public SnapshotStore Snapshots { get; private set; }
        public ProposalStore Proposals { get; private set; }
        public ApplyJournal Journal { get; private set; }
        public VisionStore Visions { get; private set; }
        public Taxonomy Taxonomy { get; private set; }
        public Router Router { get; private set; }
        /// <summary>Teams, people and the sprint calendar; re-read from disk on each use so edits to config/team.json apply without a relaunch.</summary>
        public Planning.TeamConfig Team
        {
            get
            {
                RefreshShellbackFile();
                return Planning.TeamConfig.Load(Settings.TeamPath, Settings.ShellbackDirectories);
            }
        }
        public Planning.SkillsTaxonomy Skills { get { return Planning.SkillsTaxonomy.Load(Settings.SkillsPath); } }
        public string SessionToken { get; private set; }
        public DateTimeOffset StartedAt { get; private set; }
        public int Port { get { return _server == null ? 0 : _server.Port; } }
        public bool StopRequested { get; private set; }

        private readonly object _presenceLock = new object();
        private DateTimeOffset? _lastHeartbeat;
        private DateTimeOffset? _leavingAt;
        private bool _hidden;
        private System.Threading.Timer _presenceTimer;

        /// <summary>Recorded by the UI's heartbeat. <paramref name="leaving"/> marks the tab as unloading.</summary>
        public void RecordPresence(bool leaving)
        {
            RecordPresence(leaving, false);
        }

        /// <summary>
        /// Recorded by the UI's heartbeat. <paramref name="hidden"/> says the tab is in the background or frozen, where
        /// browsers slow timers to about once a minute (Edge's sleeping tabs stop them): the engine then waits
        /// <see cref="HostOptions.HiddenHeartbeatTimeout"/> instead. Closing the tab still sends "leaving".
        /// </summary>
        public void RecordPresence(bool leaving, bool hidden)
        {
            lock (_presenceLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (leaving) { _leavingAt = now; }
                else { _lastHeartbeat = now; _leavingAt = null; _hidden = hidden; }
            }
        }

        /// <summary>Presence state for diagnostics and tests.</summary>
        public object PresenceState()
        {
            lock (_presenceLock) return new { autoStop = Options.AutoStop, lastHeartbeat = _lastHeartbeat, leavingAt = _leavingAt, hidden = _hidden, timeoutSeconds = (int)CurrentTimeout().TotalSeconds };
        }

        private TimeSpan CurrentTimeout()
        {
            return _hidden && Options.HiddenHeartbeatTimeout > Options.HeartbeatTimeout ? Options.HiddenHeartbeatTimeout : Options.HeartbeatTimeout;
        }

        /// <summary>True when the browser has been gone long enough and nothing is running: the engine should stop.</summary>
        public bool BrowserGone(DateTimeOffset now)
        {
            if (!Options.AutoStop || Jobs.AnyRunning) return false;
            lock (_presenceLock)
            {
                if (_lastHeartbeat == null) return false; // nothing ever connected; the launcher's own timeout handles that
                if (_leavingAt != null && now - _leavingAt.Value >= Options.LeavingGrace) return true;
                return now - _lastHeartbeat.Value >= CurrentTimeout();
            }
        }

        private void PresenceTick(object state)
        {
            if (StopRequested) return;
            if (!BrowserGone(DateTimeOffset.UtcNow)) return;
            Log.Info("HOST_AUTO_STOP", "No browser heartbeat; the engine stops itself (HALYARD_AUTO_STOP=0 keeps it running).");
            Stop();
        }

        public HostInfo Start()
        {
            if (_server != null) throw new InvalidOperationException("Host already started.");
            Directory.CreateDirectory(Settings.OutputDirectory);
            _server = new HttpServer(HandleAsync, Log);
            var port = _server.Start(Options.PreferredPort > 0 ? Options.PreferredPort : Settings.PreferredPort);
            var info = new HostInfo
            {
                Port = port,
                Url = "http://127.0.0.1:" + port + "/",
                SessionToken = SessionToken,
                LaunchUrl = "http://127.0.0.1:" + port + "/?session=" + SessionToken,
                LogFile = Log.FilePath,
            };
            WriteRuntimeFile(info);
            info.RuntimeFile = _runtimeFile;
            if (Options.AutoStop) _presenceTimer = new System.Threading.Timer(PresenceTick, null, 2000, 2000);
            Log.Info("HOST_READY", "Halyard " + Version + " provider=" + Settings.Provider.Kind + " " + Gateway.Policy.Describe());
            return info;
        }

        public void Stop()
        {
            if (StopRequested) return;
            StopRequested = true;
            Log.Info("HOST_STOP", "Stop requested.");
            try { if (_presenceTimer != null) _presenceTimer.Dispose(); } catch (ObjectDisposedException) { }
            try { if (_server != null) _server.Stop(); } finally { _stopped.Set(); }
            if (_runtimeFile != null)
            {
                try { File.Delete(_runtimeFile); } catch (IOException) { }
            }
        }

        /// <summary>Blocks the calling (PowerShell) thread until the UI's End-session control or Stop() fires.</summary>
        public bool WaitForStop(int timeoutMilliseconds = -1)
        {
            return timeoutMilliseconds < 0 ? WaitForever() : _stopped.Wait(timeoutMilliseconds);
        }

        private bool WaitForever()
        {
            _stopped.Wait();
            return true;
        }

        public void Dispose()
        {
            Stop();
            if (_server != null) _server.Dispose();
            _stopped.Dispose();
        }

        private void WriteRuntimeFile(HostInfo info)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogDirectory);
                _runtimeFile = Path.Combine(Settings.LogDirectory, "halyard-server-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
                // The session token is deliberately excluded: anything on the machine can read this file.
                File.WriteAllText(_runtimeFile, Json.Serialize(new { pid = System.Diagnostics.Process.GetCurrentProcess().Id, port = info.Port, url = info.Url, started = StartedAt, version = Version }));
            }
            catch (IOException) { _runtimeFile = null; }
            catch (UnauthorizedAccessException) { _runtimeFile = null; }
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        // ---- provider access ----

        public IWorkItemProvider GetProvider()
        {
            lock (_providerLock)
            {
                if (_provider != null) return _provider;
                if (!Settings.Provider.HasToken)
                    throw new ProviderNotReadyException("No " + Settings.Provider.Kind + " token is available. " + (Settings.Provider.Kind == ProviderKind.GitLab ? "Unlock the vault or set GITLAB_TOKEN." : "Set GITHUB_TOKEN (or run 'gh auth login')."));
                if (Settings.Provider.Source == null && Settings.Provider.Sandbox == null)
                    throw new ProviderNotReadyException("Configure a source or sandbox repository in Settings.");
                try
                {
                    _provider = ProviderFactory.Create(Settings.Provider, Options.Transport);
                    _providerError = null;
                }
                catch (ArgumentException ex) { throw NotReady(ex); }
                catch (InvalidOperationException ex) { throw NotReady(ex); }
                catch (NotSupportedException ex) { throw NotReady(ex); }
                return _provider;
            }
        }

        private ProviderNotReadyException NotReady(Exception ex)
        {
            _providerError = ex.Message;
            return new ProviderNotReadyException(ex.Message);
        }

        public void ResetProvider()
        {
            lock (_providerLock)
            {
                _provider = null;
                _identity = null;
                _providerError = null;
                Gateway = new WriteGateway(Settings.BuildWritePolicy());
            }
        }

        public RepositoryRef ResolveRepository(string which)
        {
            return ResolveRepositories(which)[0];
        }

        /// <summary>
        /// "source" = every configured source repository, "sandbox" = the sandbox, "all" = both, or one exact
        /// identifier that is among the configured repositories (never an arbitrary repo from the browser).
        /// </summary>
        public List<RepositoryRef> ResolveRepositories(string which)
        {
            var w = (which ?? "source").Trim();
            var lower = w.ToLowerInvariant();
            var sources = Settings.Provider.Sources;
            var sandbox = Settings.Provider.Sandbox;
            List<RepositoryRef> list;
            if (lower == "sandbox") list = sandbox == null ? new List<RepositoryRef>() : new List<RepositoryRef> { sandbox };
            else if (lower == "source") list = sources.Count > 0 ? sources : (sandbox == null ? new List<RepositoryRef>() : new List<RepositoryRef> { sandbox });
            else if (lower == "all")
            {
                list = new List<RepositoryRef>(sources);
                if (sandbox != null && !list.Any(r => r.Equals(sandbox))) list.Add(sandbox);
            }
            else
            {
                var known = new List<RepositoryRef>(sources);
                if (sandbox != null) known.Add(sandbox);
                list = known.Where(r => string.Equals(r.Identifier, w, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
                if (list.Count == 0) throw new BadRequestException("'" + w + "' is not a configured repository. Use source, sandbox, all, or one of the identifiers in config/halyard.env.");
            }
            if (list.Count == 0) throw new BadRequestException("The " + lower + " repository is not configured.");
            return list;
        }

        /// <summary>Items of the current snapshot that live in the sandbox: the only ones proposals can be written for.</summary>
        public List<WorkItem> SandboxItems(List<WorkItem> visible)
        {
            var sandbox = Settings.Provider.Sandbox;
            if (sandbox == null) return new List<WorkItem>();
            return visible.Where(i => i.Repository.Equals(sandbox)).ToList();
        }

        /// <summary>Finds a snapshot item by key (<c>github:owner/repo:12</c>) or, within the sandbox, by number.</summary>
        public WorkItem FindItem(string keyOrNumber)
        {
            var snap = Snapshots.Current;
            if (snap == null || string.IsNullOrWhiteSpace(keyOrNumber)) return null;
            var text = keyOrNumber.Trim().TrimStart('#');
            var byKey = snap.Items.FirstOrDefault(i => string.Equals(i.Key, text, StringComparison.OrdinalIgnoreCase));
            if (byKey != null) return byKey;
            long number;
            if (!long.TryParse(text, out number)) return null;
            var sandbox = Settings.Provider.Sandbox;
            var matches = snap.Items.Where(i => i.Number == number).ToList();
            if (matches.Count == 1) return matches[0];
            return matches.FirstOrDefault(i => sandbox != null && i.Repository.Equals(sandbox)) ?? matches.FirstOrDefault();
        }

        public RepositoryRef SandboxOrThrow()
        {
            var sandbox = Settings.Provider.Sandbox;
            if (sandbox == null) throw new BadRequestException("No sandbox repository is configured (HALYARD_SANDBOX_REPO).");
            return sandbox;
        }

        public ILlmClient CreateLlm()
        {
            var client = new LlmClient(Settings.Llm, Options.Transport);
            var missing = Settings.Llm.MissingFor();
            if (missing != null) throw new ProviderNotReadyException("Model calls are not usable: " + missing);
            return client;
        }

        /// <summary>Items in the current snapshot, optionally narrowed to one exact label.</summary>
        public List<WorkItem> VisibleItems(string label, out List<WorkItem> all)
        {
            var snap = Snapshots.Current;
            all = snap == null ? new List<WorkItem>() : snap.Items;
            if (string.IsNullOrWhiteSpace(label)) return all.ToList();
            return all.Where(i => i.HasLabel(label)).ToList();
        }

        // ---- request pipeline ----

        private async Task<HttpResponse> HandleAsync(HttpRequest request)
        {
            var host = request.Header("Host") ?? string.Empty;
            var hostName = host.StartsWith("[") ? host.Substring(0, host.IndexOf(']') + 1) : host.Split(':')[0];
            if (hostName != "127.0.0.1" && hostName != "localhost" && hostName != "[::1]")
                return Json.Error(403, "BAD_HOST", "Loopback host names only.", request.RequestId);

            if (request.Path.StartsWith("/api/", StringComparison.Ordinal) || request.Path == "/api")
            {
                if (request.Path != "/api/health" && !TokenMatches(request) && !BeaconTokenMatches(request))
                    return Json.Error(401, "SESSION_REQUIRED", "Missing or invalid X-Halyard-Session token. Reopen Halyard from the launcher.", request.RequestId);
                var origin = request.Header("Origin");
                if (origin != null && !IsOwnOrigin(origin))
                    return Json.Error(403, "BAD_ORIGIN", "Cross-origin requests are not accepted.", request.RequestId);
                bool mismatch;
                var handler = Router.Resolve(request, out mismatch);
                if (handler == null) return Json.Error(mismatch ? 405 : 404, mismatch ? "METHOD_NOT_ALLOWED" : "NOT_FOUND", mismatch ? "Method not allowed." : "No such API route.", request.RequestId);
                try
                {
                    return await handler(request).ConfigureAwait(false);
                }
                catch (BadRequestException ex) { return Json.Error(400, "BAD_REQUEST", ex.Message, request.RequestId); }
                catch (NotFoundException ex) { return Json.Error(404, "NOT_FOUND", ex.Message, request.RequestId); }
                catch (ProviderNotReadyException ex) { return Json.Error(409, "PROVIDER_NOT_READY", ex.Message, request.RequestId); }
                catch (WriteBlockedException ex) { return Json.Error(403, "WRITE_BLOCKED", ex.Message, request.RequestId); }
                catch (ProviderHttpException ex)
                {
                    Log.Warn("PROVIDER_ERROR", ex.Code + " " + ex.Message, request.Path, request.RequestId);
                    return Json.Error(ex.Status == 401 || ex.Status == 403 ? 502 : 502, ex.Code, ex.Message, request.RequestId);
                }
                catch (InvalidOperationException ex) { return Json.Error(409, "CONFLICT", ex.Message, request.RequestId); }
            }

            var webRoot = Options.WebRoot ?? Settings.WebRootDirectory;
            if (!string.IsNullOrEmpty(webRoot))
            {
                var files = new StaticFiles(webRoot);
                var served = files.Serve(request);
                if (served != null) return served;
            }
            if (request.Path == "/" || request.Path == "/index.html")
                return HttpResponse.Text(200, "Halyard host is running on port " + Port + ", but no built UI was found" + (string.IsNullOrEmpty(webRoot) ? "." : " at " + webRoot + ".") + " Build it with build/Build-Halyard.ps1 and relaunch.", "text/html; charset=utf-8");
            return HttpResponse.Text(404, "Not found");
        }

        /// <summary>
        /// The unload beacon (navigator.sendBeacon) cannot set headers, so for /api/heartbeat alone the token may
        /// travel in the JSON body as "session". Bodies are never logged; the query string is still refused.
        /// </summary>
        private bool BeaconTokenMatches(HttpRequest request)
        {
            if (request.Path != "/api/heartbeat" || !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
            if (request.Body == null || request.Body.Length == 0 || request.Body.Length > 512) return false;
            try
            {
                var body = Json.Body(request);
                var token = Json.Read<string>(body, "session");
                return !string.IsNullOrEmpty(token) && FixedTimeEquals(token, SessionToken);
            }
            catch (JsonException) { return false; }
        }

        private bool TokenMatches(HttpRequest request)
        {
            // Header only: the launch link's ?session= is read by the page and removed from the address bar,
            // and API URLs never carry the token, so it does not end up in history or logs.
            var token = request.Header(SessionHeader);
            return !string.IsNullOrEmpty(token) && FixedTimeEquals(token, SessionToken);
        }

        private bool IsOwnOrigin(string origin)
        {
            return string.Equals(origin, "http://127.0.0.1:" + Port, StringComparison.OrdinalIgnoreCase) || string.Equals(origin, "http://localhost:" + Port, StringComparison.OrdinalIgnoreCase);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    public sealed class ProviderNotReadyException : Exception
    {
        public ProviderNotReadyException(string message) : base(message) { }
    }
}
