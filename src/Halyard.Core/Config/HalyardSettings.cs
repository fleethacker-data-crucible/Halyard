using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;

namespace Halyard.Core.Config
{
    /// <summary>
    /// Everything the host needs, resolved from environment variables (optionally pre-loaded from
    /// <c>config/halyard.env</c>). Secrets stay in memory; <see cref="ToPublicDictionary"/> never includes them.
    /// </summary>
    public sealed class HalyardSettings
    {
        public string RootDirectory { get; set; }
        public string OutputDirectory { get { return Path.Combine(RootDirectory, "output"); } }
        public string LogDirectory { get { return Path.Combine(RootDirectory, "logs"); } }
        public string ConfigDirectory { get { return Path.Combine(RootDirectory, "config"); } }
        public string WebRootDirectory { get; set; }

        public ProviderSettings Provider { get; set; }
        public LlmSettings Llm { get; set; }
        public WriteMode WriteMode { get; set; }
        public bool AllowProduction { get; set; }
        public string LabelFilter { get; set; }
        public int PreferredPort { get; set; }
        /// <summary>Stop the engine when the browser tab is gone (HALYARD_AUTO_STOP=0 disables).</summary>
        public bool AutoStop { get; set; }
        public int MaxSyncItems { get; set; }

        public string TaxonomyPath { get { return Path.Combine(ConfigDirectory, "taxonomy.json"); } }
        public string TeamPath { get { return Path.Combine(ConfigDirectory, "team.json"); } }
        public string SkillsPath { get { return Path.Combine(ConfigDirectory, "skills.json"); } }
        /// <summary>Exported Shellback peer cards (<c>config/shellback/</c>).</summary>
        public string ShellbackDirectory { get { return Path.Combine(ConfigDirectory, "shellback"); } }
        /// <summary>Card folders to read.</summary>
        public string[] ShellbackDirectories
        {
            get
            {
                // Cards from a live export (see ShellbackFeed) are read first, so they win over hand-dropped copies of the same file name.
                return Directory.Exists(ShellbackLiveDirectory) ? new[] { ShellbackLiveDirectory, ShellbackDirectory } : new[] { ShellbackDirectory };
            }
        }
        /// <summary>Cache of consented cards refreshed from <see cref="ShellbackExportPath"/> or <see cref="ShellbackUrl"/>.</summary>
        public string ShellbackLiveDirectory { get { return Path.Combine(OutputDirectory, "shellback-live"); } }
        /// <summary>HALYARD_SHELLBACK_EXPORT: a Shellback export file or folder, re-read whenever the team is loaded.</summary>
        public string ShellbackExportPath { get; set; }
        /// <summary>HALYARD_SHELLBACK_URL: an endpoint serving peer cards, fetched on an explicit refresh (GET only).</summary>
        public string ShellbackUrl { get; set; }
        /// <summary>HALYARD_SHELLBACK_TOKEN: bearer token for the endpoint. Never shown or logged.</summary>
        public string ShellbackToken { get; set; }
        public string VisionDirectory { get { return Path.Combine(OutputDirectory, "vision"); } }
        public string ReportsDirectory { get { return Path.Combine(OutputDirectory, "reports"); } }
        /// <summary>The Halyard mark as a 64 px PNG for the emailed report (assets/brand/mark-64.png), or null when the file is absent.</summary>
        public string BrandLogoBase64()
        {
            var path = Path.Combine(RootDirectory, "assets", "brand", "mark-64.png");
            try { return File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        public string IntakesDirectory { get { return Path.Combine(OutputDirectory, "intakes"); } }
        /// <summary>HALYARD_REPORT_WEBHOOK: an https Teams/Slack incoming webhook for the leadership summary. Holds a secret URL; never shown or logged.</summary>
        public string ReportWebhookUrl { get; set; }
        /// <summary>HALYARD_TALKFORGE_PATH: the TalkForge folder whose speech-to-text files voice input uses. Empty: a TalkForge folder next to Halyard, then %USERPROFILE%\Projects\TalkForge.</summary>
        public string TalkForgePath { get; set; }
        public string WatchDirectory { get { return Path.Combine(OutputDirectory, "watch"); } }
        public string ProposalsDirectory { get { return Path.Combine(OutputDirectory, "proposals"); } }
        /// <summary>One file per apply run, with the body each written item had before (for rollback).</summary>
        public string ApplyJournalDirectory { get { return Path.Combine(OutputDirectory, "applied"); } }
        public string SnapshotsDirectory { get { return Path.Combine(OutputDirectory, "snapshots"); } }
        public string SeedsDirectory { get { return Path.Combine(OutputDirectory, "seeds"); } }
        public string TranscriptsDirectory { get { return Path.Combine(OutputDirectory, "llm_transcripts"); } }

        /// <summary>Extra repositories that must never be written, whatever the mode (HALYARD_PROTECTED_REPOS, comma-separated).</summary>
        public List<string> ProtectedRepositories { get; set; }

        public WritePolicy BuildWritePolicy()
        {
            var policy = new WritePolicy { Mode = WriteMode, AllowProduction = AllowProduction };
            var sandbox = Provider.Sandbox;
            if (sandbox != null) policy.SandboxTargets.Add(sandbox);
            var sources = Provider.Sources;
            if (sources.Count > 0 && WriteMode == WriteMode.Production) policy.ProductionTarget = sources[0];
            foreach (var source in sources)
            {
                if (WriteMode == WriteMode.Production && source.Equals(policy.ProductionTarget)) continue;
                if (sandbox == null || !sandbox.Equals(source)) policy.ProtectedTargets.Add(source);
            }
            foreach (var id in ProtectedRepositories)
            {
                var r = new RepositoryRef(Provider.Kind, id);
                if (!policy.ProtectedTargets.Any(t => t.Equals(r))) policy.ProtectedTargets.Add(r);
            }
            return policy;
        }

        /// <summary>Reads the process environment. Call <see cref="LoadEnvFile"/> first to merge config/halyard.env.</summary>
        public static HalyardSettings FromEnvironment(string rootDirectory)
        {
            return FromEnvironment(rootDirectory, Environment.GetEnvironmentVariable);
        }

        /// <summary>Same as <see cref="FromEnvironment(string)"/> with an explicit variable lookup (tests).</summary>
        public static HalyardSettings FromEnvironment(string rootDirectory, Func<string, string> lookup)
        {
            var s = new HalyardSettings { RootDirectory = rootDirectory };
            Func<string, string> env = name =>
            {
                var v = lookup(name);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            };
            Func<string, string> app = suffix => env("HALYARD_" + suffix);

            s.ShellbackExportPath = app("SHELLBACK_EXPORT");
            s.ShellbackUrl = app("SHELLBACK_URL");
            s.ShellbackToken = app("SHELLBACK_TOKEN");
            s.ReportWebhookUrl = app("REPORT_WEBHOOK");
            var kindText = app("PROVIDER") ?? "github";
            s.Provider.Kind = kindText.Equals("gitlab", StringComparison.OrdinalIgnoreCase) ? ProviderKind.GitLab : ProviderKind.GitHub;
            if (s.Provider.Kind == ProviderKind.GitLab)
            {
                s.Provider.BaseUrl = env("GITLAB_URL") ?? string.Empty;
                s.Provider.SourceRepository = app("SOURCE_REPO") ?? string.Empty;
                // The write target comes only from HALYARD_SANDBOX_REPO; generic names such as GITLAB_PROJECT_ID
                // are often set to a real project and must never become writable by accident.
                s.Provider.SandboxRepository = app("SANDBOX_REPO") ?? string.Empty;
                s.Provider.Token = env("GITLAB_TOKEN");
                s.Provider.GitLabVersion = app("GITLAB_VERSION");
            }
            else
            {
                s.Provider.BaseUrl = env("GITHUB_API_URL") ?? string.Empty;
                s.Provider.SourceRepository = app("SOURCE_REPO") ?? string.Empty;
                s.Provider.SandboxRepository = app("SANDBOX_REPO") ?? string.Empty;
                s.Provider.Token = env("GITHUB_TOKEN") ?? env("GH_TOKEN");
            }
            // A config that names the sandbox by its former address is pointed at the moved repository (see RenamedRepositories).
            s.Provider.SourceRepository = RenamedRepositories.CanonicalList(s.Provider.Kind, s.Provider.SourceRepository);
            s.Provider.SandboxRepository = RenamedRepositories.Canonical(s.Provider.Kind, s.Provider.SandboxRepository);
            int delay;
            if (int.TryParse(app("WRITE_DELAY_MS"), out delay)) s.Provider.WriteDelayMs = delay;

            var protectedList = app("PROTECTED_REPOS");
            if (protectedList != null) s.ProtectedRepositories = protectedList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => RenamedRepositories.Canonical(s.Provider.Kind, x)).Where(x => x.Length > 0).ToList();

            s.WriteMode = string.Equals(app("WRITE_MODE"), "production", StringComparison.OrdinalIgnoreCase) ? WriteMode.Production : WriteMode.Sandbox;
            s.AllowProduction = app("ALLOW_PRODUCTION") == "1" || string.Equals(app("ALLOW_PRODUCTION"), "true", StringComparison.OrdinalIgnoreCase);
            s.LabelFilter = app("LABEL_FILTER");
            int port;
            if (int.TryParse(app("PORT"), out port) && port >= 1024 && port <= 65535) s.PreferredPort = port;
            if (app("AUTO_STOP") == "0") s.AutoStop = false;
            int max;
            if (int.TryParse(app("MAX_SYNC_ITEMS"), out max) && max > 0) s.MaxSyncItems = max;
            s.WebRootDirectory = app("WEBROOT") ?? string.Empty;

            // Model calls go to Fairlead on this PC only. The address must be loopback; the key comes from Credential Manager (Fairlead/Halyard).
            s.Llm.FairleadUrl = app("FAIRLEAD_URL");
            s.Llm.Route = app("FAIRLEAD_MODEL");
            s.Llm.Privacy = app("LLM_PRIVACY");
            s.Llm.TranscriptDirectory = app("LLM_TRANSCRIPTS") == "0" ? null : s.TranscriptsDirectory;
            s.Llm.SpeechRoute = app("VOICE_ROUTE");
            s.Llm.SpeechVoice = app("VOICE");
            double speed;
            if (double.TryParse(app("VOICE_SPEED"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out speed)) s.Llm.SpeechSpeed = Math.Min(2.0, Math.Max(0.5, speed));
            s.TalkForgePath = app("TALKFORGE_PATH");
            return s;
        }

        /// <summary>
        /// Loads KEY=VALUE lines into the process environment without overriding variables already set.
        /// Lines starting with # are comments; values may be quoted. Returns the number of keys applied.
        /// </summary>
        public static int LoadEnvFile(string path)
        {
            if (!File.Exists(path)) return 0;
            var applied = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Regex.Match(line, @"^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
                if (!m.Success) continue;
                var key = m.Groups[1].Value;
                var value = m.Groups[2].Value.Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                    value = value.Substring(1, value.Length - 2);
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) continue;
                Environment.SetEnvironmentVariable(key, value);
                applied++;
            }
            return applied;
        }

        /// <summary>
        /// Writes or replaces KEY=VALUE lines in an env file (created when missing). Used to remember an API
        /// key entered in the browser; the file is git-ignored and lives only on this machine.
        /// </summary>
        public static void UpsertEnvFile(string path, IDictionary<string, string> values)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string> { "# Halyard settings (git-ignored). Never commit real tokens." };
            foreach (var kv in values)
            {
                var pattern = new Regex(@"^\s*(?:export\s+)?" + Regex.Escape(kv.Key) + @"\s*=");
                var index = lines.FindIndex(l => pattern.IsMatch(l));
                var line = kv.Value == null ? null : kv.Key + "=" + kv.Value;
                if (index >= 0)
                {
                    if (line == null) lines.RemoveAt(index); else lines[index] = line;
                }
                else if (line != null) lines.Add(line);
            }
            File.WriteAllLines(path, lines);
        }

        /// <summary>The env file this install reads and writes: <c>config/halyard.env</c>.</summary>
        public string EnvFilePath { get { return Path.Combine(ConfigDirectory, EnvFileName); } }

        public const string EnvFileName = "halyard.env";

        public HalyardSettings()
        {
            RootDirectory = Directory.GetCurrentDirectory();
            WebRootDirectory = string.Empty;
            Provider = new ProviderSettings();
            Llm = new LlmSettings();
            WriteMode = WriteMode.Sandbox;
            PreferredPort = 8773;
            AutoStop = true;
            MaxSyncItems = 2000;
            ProtectedRepositories = new List<string>();
        }

        /// <summary>Non-secret view for the UI and diagnostics.</summary>
        public Dictionary<string, object> ToPublicDictionary()
        {
            return new Dictionary<string, object>
            {
                { "provider", Provider.Kind.ToString().ToLowerInvariant() },
                { "baseUrl", Provider.EffectiveBaseUrl },
                { "sourceRepository", Provider.SourceRepository },
                { "sourceRepositories", Provider.SourceRepositories },
                { "sandboxRepository", Provider.SandboxRepository },
                { "hasProviderToken", Provider.HasToken },
                { "writeMode", WriteMode.ToString().ToLowerInvariant() },
                { "allowProduction", AllowProduction },
                { "labelFilter", LabelFilter },
                { "preferredPort", PreferredPort },
                { "autoStop", AutoStop },
                { "maxSyncItems", MaxSyncItems },
                { "protectedRepositories", ProtectedRepositories },
                { "llmModel", Llm.Model() },
                { "llmUrl", Llm.Url() },
                { "llmKeyTarget", Llm.KeyTarget },
                { "hasLlmToken", Llm.IsConfigured() },
                { "llmMissing", Llm.MissingFor() },
                { "envFile", EnvFilePath },
                { "envFileExists", File.Exists(EnvFilePath) },
                { "rootDirectory", RootDirectory },
                { "outputDirectory", OutputDirectory },
            };
        }
    }
}
