using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Config;
using Cascade.Core.Host;
using Cascade.Core.Model;
using Cascade.Core.Providers;
using Cascade.Core.Safety;
using Cascade.Core.Synthetic;

namespace Cascade.Core
{
    /// <summary>
    /// Synchronous entry points for PowerShell. Windows PowerShell 5.1 cannot await, so every operation here
    /// blocks until finished and returns plain objects. Progress is reported through an optional callback.
    /// </summary>
    public static class Facade
    {
        public static string Version { get { return CascadeHost.Version; } }

        public static int LoadEnvFile(string path)
        {
            return CascadeSettings.LoadEnvFile(path);
        }

        public static CascadeSettings SettingsFromEnvironment(string rootDirectory)
        {
            return CascadeSettings.FromEnvironment(rootDirectory);
        }

        public static CascadeHost CreateHost(CascadeSettings settings, string? webRoot, int preferredPort, Dictionary<string, object?>? launcherState)
        {
            return CreateHost(settings, webRoot, preferredPort, launcherState, settings.AutoStop);
        }

        public static CascadeHost CreateHost(CascadeSettings settings, string? webRoot, int preferredPort, Dictionary<string, object?>? launcherState, bool autoStop)
        {
            return new CascadeHost(new HostOptions
            {
                Settings = settings,
                WebRoot = webRoot,
                PreferredPort = preferredPort,
                LauncherState = launcherState ?? new Dictionary<string, object?>(),
                AutoStop = autoStop,
            });
        }

        /// <summary>The leadership report (HTML) from the current snapshot on disk; used by Send-CascadeReport.ps1.</summary>
        public static string LeadershipReportHtml(CascadeSettings settings, string? labelFilter = null, string? title = null)
        {
            var store = new Snapshots.SnapshotStore(settings.SnapshotsDirectory);
            var snap = store.Current;
            if (snap == null) throw new InvalidOperationException("No snapshot under " + settings.SnapshotsDirectory + "; synchronize first (Sync-CascadeSnapshot or the Connection page).");
            var items = string.IsNullOrWhiteSpace(labelFilter) ? snap.Items : snap.Items.Where(i => i.HasLabel(labelFilter!)).ToList();
            var team = Planning.TeamConfig.Load(settings.TeamPath, settings.SynapseDirectory);
            var report = Analysis.Delivery.Build(items, team, DateTimeOffset.UtcNow, snap.Repository);
            return Analysis.DeliveryHtml.Render(report, string.IsNullOrWhiteSpace(title) ? "Cascade Leadership Dashboard" : title!);
        }

        /// <summary>Synchronizes every configured source (or the sandbox) into a new snapshot without the host; used by scheduled reports.</summary>
        public static Snapshots.Snapshot SyncSnapshot(CascadeSettings settings, string which = "source", bool includeClosed = true)
        {
            var provider = ProviderFactory.Create(settings.Provider);
            var repos = string.Equals(which, "sandbox", StringComparison.OrdinalIgnoreCase)
                ? new List<RepositoryRef> { settings.Provider.Sandbox ?? throw new InvalidOperationException("No sandbox repository is configured.") }
                : (settings.Provider.Sources.Count > 0 ? settings.Provider.Sources : new List<RepositoryRef> { settings.Provider.Sandbox ?? throw new InvalidOperationException("No source or sandbox repository is configured.") });
            var all = new List<WorkItem>();
            foreach (var repo in repos)
            {
                var reader = provider.ForRepository(repo);
                var query = new WorkItemQuery { MaxItems = settings.MaxSyncItems, State = includeClosed ? (WorkItemState?)null : WorkItemState.Open };
                all.AddRange(Task.Run(() => reader.ListWorkItemsAsync(query, null, CancellationToken.None)).GetAwaiter().GetResult());
            }
            return new Snapshots.SnapshotStore(settings.SnapshotsDirectory).Save(repos, all, null);
        }

        public static ProviderIdentity TestConnection(CascadeSettings settings)
        {
            var provider = ProviderFactory.Create(settings.Provider);
            return Task.Run(() => provider.TestConnectionAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }

        public static List<SyntheticWorkItem> GenerateCatalog(int total, int seed, bool includeDefects)
        {
            return GenerateCatalog(total, seed, includeDefects, 0.20);
        }

        /// <param name="defectRate">Share of items given a gap when <paramref name="includeDefects"/> is set (0.2 = 80% complete, 20% needing review).</param>
        public static List<SyntheticWorkItem> GenerateCatalog(int total, int seed, bool includeDefects, double defectRate)
        {
            var options = SyntheticOptions.ForTotal(total, seed);
            options.IncludeDefects = includeDefects;
            options.DefectRate = defectRate;
            return SyntheticCatalog.Generate(options);
        }

        public static string WriteCatalog(string path, int total, int seed, bool includeDefects)
        {
            return WriteCatalog(path, total, seed, includeDefects, 0.20);
        }

        public static string WriteCatalog(string path, int total, int seed, bool includeDefects, double defectRate)
        {
            var options = SyntheticOptions.ForTotal(total, seed);
            options.IncludeDefects = includeDefects;
            options.DefectRate = defectRate;
            var items = SyntheticCatalog.Generate(options);
            SyntheticCatalog.Save(path, items, options);
            return Path.GetFullPath(path);
        }

        public static SeedManifest Seed(CascadeSettings settings, int total, int seed, bool includeDefects, string? runId, bool nativeLinks, Action<string>? progress)
        {
            return Seed(settings, total, seed, includeDefects, 0.20, runId, nativeLinks, progress);
        }

        public static SeedManifest Seed(CascadeSettings settings, int total, int seed, bool includeDefects, double defectRate, string? runId, bool nativeLinks, Action<string>? progress)
        {
            var catalog = GenerateCatalog(total, seed, includeDefects, defectRate);
            return Seed(settings, catalog, runId, nativeLinks, progress);
        }

        public static SeedManifest SeedFromFile(CascadeSettings settings, string catalogPath, string? runId, bool nativeLinks, Action<string>? progress)
        {
            return Seed(settings, SyntheticCatalog.Load(catalogPath), runId, nativeLinks, progress);
        }

        private static SeedManifest Seed(CascadeSettings settings, List<SyntheticWorkItem> catalog, string? runId, bool nativeLinks, Action<string>? progress)
        {
            var sandbox = settings.Provider.Sandbox;
            if (sandbox == null) throw new InvalidOperationException("No sandbox repository is configured (CASCADE_SANDBOX_REPO).");
            var provider = ProviderFactory.Create(settings.Provider);
            var gateway = new WriteGateway(settings.BuildWritePolicy());
            var options = new SeedOptions { NativeLinks = nativeLinks, ManifestDirectory = settings.SeedsDirectory };
            if (!string.IsNullOrWhiteSpace(runId)) options.RunId = runId!.Trim();
            return RunWithProgress(reporter => SandboxSeeder.SeedAsync(provider, gateway, sandbox, catalog, options, reporter, CancellationToken.None), progress);
        }

        public static TeardownResult Teardown(CascadeSettings settings, string scope, string? runId, string? confirmText, bool dryRun, Action<string>? progress)
        {
            var sandbox = settings.Provider.Sandbox;
            if (sandbox == null) throw new InvalidOperationException("No sandbox repository is configured (CASCADE_SANDBOX_REPO).");
            var provider = ProviderFactory.Create(settings.Provider);
            var gateway = new WriteGateway(settings.BuildWritePolicy());
            var options = new TeardownOptions { RunId = runId, ConfirmText = confirmText, DryRun = dryRun };
            switch ((scope ?? "run").Trim().ToLowerInvariant())
            {
                case "run": options.Scope = TeardownScope.Run; break;
                case "allsynthetic": case "all": options.Scope = TeardownScope.AllSynthetic; break;
                case "everything": options.Scope = TeardownScope.Everything; break;
                default: throw new ArgumentException("scope must be run, allSynthetic or everything.");
            }
            return RunWithProgress(reporter => SandboxTeardown.RunAsync(provider, gateway, sandbox, options, reporter, CancellationToken.None), progress);
        }

        public static List<WorkItem> ListWorkItems(CascadeSettings settings, string repository, int max)
        {
            var provider = ProviderFactory.Create(settings.Provider);
            var repo = string.Equals(repository, "sandbox", StringComparison.OrdinalIgnoreCase) ? settings.Provider.Sandbox : settings.Provider.Source;
            if (repo == null) throw new InvalidOperationException("Repository '" + repository + "' is not configured.");
            var reader = provider.ForRepository(repo);
            return Task.Run(() => reader.ListWorkItemsAsync(new WorkItemQuery { MaxItems = max }, null, CancellationToken.None)).GetAwaiter().GetResult().ToList();
        }

        /// <summary>Hides or shows the console window that hosts the PowerShell process (Windows only; no-op elsewhere).</summary>
        public static bool SetConsoleVisible(bool visible)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try
            {
                var handle = NativeMethods.GetConsoleWindow();
                if (handle == IntPtr.Zero) return false;
                return NativeMethods.ShowWindow(handle, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static class NativeMethods
        {
            public const int SW_HIDE = 0;
            public const int SW_SHOW = 5;

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetConsoleWindow();

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the thread pool and delivers progress messages on the calling thread.
        /// PowerShell script blocks only run on their own runspace thread, so callbacks must not fire from
        /// the worker threads that async continuations land on.
        /// </summary>
        public static T RunWithProgress<T>(Func<IProgress<SeedProgress>?, Task<T>> work, Action<string>? progress)
        {
            if (progress == null) return Task.Run(() => work(null)).GetAwaiter().GetResult();
            using (var queue = new BlockingCollection<string>())
            {
                var reporter = new QueueProgress(queue);
                var task = Task.Run(async () =>
                {
                    try { return await work(reporter).ConfigureAwait(false); }
                    finally { queue.CompleteAdding(); }
                });
                foreach (var message in queue.GetConsumingEnumerable()) progress(message);
                return task.GetAwaiter().GetResult();
            }
        }

        private sealed class QueueProgress : IProgress<SeedProgress>
        {
            private readonly BlockingCollection<string> _queue;
            public QueueProgress(BlockingCollection<string> queue) { _queue = queue; }
            public void Report(SeedProgress value)
            {
                if (!_queue.IsAddingCompleted) _queue.TryAdd(value.Done + "/" + value.Total + " " + value.Message);
            }
        }
    }
}
