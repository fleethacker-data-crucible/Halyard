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
        public string[] ShellbackDirectories { get { return new[] { ShellbackDirectory }; } }
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
        public string ProposalsDirectory { get { return Path.Combine(OutputDirectory, "proposals"); } }
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
            // HALYARD_* first. The former CASCADE_* names are read as a fallback for one release after the rename, then removed.
            Func<string, string> app = suffix => env("HALYARD_" + suffix) ?? env("CASCADE_" + suffix);

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
            }
            else
            {
                s.Provider.BaseUrl = env("GITHUB_API_URL") ?? string.Empty;
                s.Provider.SourceRepository = app("SOURCE_REPO") ?? string.Empty;
                s.Provider.SandboxRepository = app("SANDBOX_REPO") ?? string.Empty;
                s.Provider.Token = env("GITHUB_TOKEN") ?? env("GH_TOKEN");
            }
            // A config that still names the sandbox by its former name is pointed at the renamed repository (see RenamedRepositories).
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

            s.Llm.Profile = LlmSettings.ParseProfile(app("LLM_PROFILE")) ?? LlmProfile.Auto;
            s.Llm.OpenAiUrl = env("LLM_URL") ?? env("OPENAI_BASE_URL");
            s.Llm.OpenAiModel = env("LLM_MODEL") ?? env("OPENAI_MODEL");
            s.Llm.OpenAiToken = env("LLM_TOKEN") ?? env("OPENAI_API_KEY");
            s.Llm.DatabricksUrl = env("DATABRICKS_URL");
            s.Llm.DatabricksModel = env("DATABRICKS_MODEL");
            s.Llm.DatabricksToken = env("DATABRICKS_TOKEN");
            // The endpoint set from a pasted curl command (Settings). The token is stored DPAPI-protected ("dpapi:...").
            s.Llm.CustomUrl = app("LLM_CUSTOM_URL");
            s.Llm.CustomModel = app("LLM_CUSTOM_MODEL");
            s.Llm.CustomFormat = LlmSettings.ParseFormat(app("LLM_CUSTOM_FORMAT")) ?? LlmWireFormat.Chat;
            s.Llm.CustomHeaders = ParseHeaders(app("LLM_CUSTOM_HEADERS"));
            var customToken = app("LLM_CUSTOM_TOKEN");
            s.CustomTokenRemembered = SecretProtector.IsProtected(customToken);
            s.Llm.CustomToken = SecretProtector.IsProtected(customToken) ? SecretProtector.Unprotect(customToken) : customToken;
            int maxTokens;
            if (int.TryParse(app("LLM_MAX_TOKENS"), out maxTokens) && maxTokens > 0) s.Llm.MaxTokens = maxTokens;
            s.Llm.TranscriptDirectory = app("LLM_TRANSCRIPTS") == "0" ? null : s.TranscriptsDirectory;
            return s;
        }

        /// <summary>Env names for the custom (curl) endpoint, as written by <see cref="CustomEndpointEnv"/>.</summary>
        public const string CustomUrlKey = "HALYARD_LLM_CUSTOM_URL";
        public const string CustomModelKey = "HALYARD_LLM_CUSTOM_MODEL";
        public const string CustomFormatKey = "HALYARD_LLM_CUSTOM_FORMAT";
        public const string CustomHeadersKey = "HALYARD_LLM_CUSTOM_HEADERS";
        public const string CustomTokenKey = "HALYARD_LLM_CUSTOM_TOKEN";

        /// <summary>
        /// The env-file lines that remember the custom endpoint. The token is included only DPAPI-protected
        /// (<paramref name="protectedToken"/> is already "dpapi:..."); null leaves the stored token as it is.
        /// </summary>
        public static Dictionary<string, string> CustomEndpointEnv(LlmSettings llm, string protectedToken)
        {
            var values = new Dictionary<string, string>
            {
                { "HALYARD_LLM_PROFILE", LlmSettings.ProfileToken(LlmProfile.Custom) },
                { CustomUrlKey, llm.CustomUrl },
                { CustomModelKey, string.IsNullOrWhiteSpace(llm.CustomModel) ? null : llm.CustomModel },
                { CustomFormatKey, LlmSettings.FormatToken(llm.CustomFormat) },
                { CustomHeadersKey, llm.CustomHeaders == null || llm.CustomHeaders.Count == 0 ? null : FormatHeaders(llm.CustomHeaders) },
            };
            if (protectedToken != null) values[CustomTokenKey] = protectedToken;
            return values;
        }

        /// <summary>Extra headers as one env-file line: a JSON object of name to value.</summary>
        public static string FormatHeaders(IDictionary<string, string> headers)
        {
            var o = new Halyard.Core.Serialization.JObject();
            foreach (var kv in headers) o[kv.Key] = kv.Value;
            return o.ToString(Halyard.Core.Serialization.Formatting.None);
        }

        public static Dictionary<string, string> ParseHeaders(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;
            try
            {
                var o = Halyard.Core.Serialization.JObject.Parse(text);
                foreach (var p in o.Properties())
                {
                    if (p.Value != null && p.Value.Type == Halyard.Core.Serialization.JTokenType.String) result[p.Name] = (string)p.Value;
                }
            }
            catch (Halyard.Core.Serialization.JsonException) { }
            return result;
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
        /// <summary>The env file name before the rename. Read only by <see cref="MigrateLegacyEnvFile"/>.</summary>
        public const string LegacyEnvFileName = "cascade.env";

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

        /// <summary>
        /// First launch after the rename: when <c>config/halyard.env</c> does not exist and <c>config/cascade.env</c> does,
        /// writes a copy as <c>halyard.env</c> with the variable names updated (CASCADE_ARC_ITEM_* to HALYARD_LAZARETTE_ITEM_*,
        /// CASCADE_USE_ARC to HALYARD_USE_LAZARETTE, other CASCADE_* to HALYARD_*). The original file is left untouched, and
        /// nothing happens once <c>halyard.env</c> exists, so this runs once and is safe to call on every launch.
        /// Returns true when a copy was written.
        /// </summary>
        public static bool MigrateLegacyEnvFile(string configDirectory)
        {
            var current = Path.Combine(configDirectory, EnvFileName);
            var legacy = Path.Combine(configDirectory, LegacyEnvFileName);
            if (File.Exists(current) || !File.Exists(legacy)) return false;
            var lines = File.ReadAllLines(legacy).Select(RenameLegacyEnvLine).ToList();
            lines.Insert(0, "# Copied from config/cascade.env on the first launch after the rename to Halyard; that file was left unchanged.");
            var temp = current + ".tmp";
            File.WriteAllLines(temp, lines);
            try { File.Move(temp, current); }
            catch (IOException) { if (File.Exists(temp)) File.Delete(temp); if (!File.Exists(current)) throw; return false; }
            return true;
        }

        /// <summary>Renames the variable on one KEY=VALUE line from its former CASCADE_* name; comments and other lines are unchanged.</summary>
        public static string RenameLegacyEnvLine(string line)
        {
            var m = Regex.Match(line, @"^(\s*(?:export\s+)?)CASCADE_([A-Za-z0-9_]*)(\s*=.*)$");
            if (!m.Success) return line;
            var suffix = m.Groups[2].Value;
            string name;
            if (suffix.StartsWith("ARC_ITEM_", StringComparison.Ordinal)) name = "HALYARD_LAZARETTE_ITEM_" + suffix.Substring("ARC_ITEM_".Length);
            else if (suffix == "USE_ARC") name = "HALYARD_USE_LAZARETTE";
            else name = "HALYARD_" + suffix;
            var rest = m.Groups[3].Value;
            foreach (var renamed in RenamedRepositories.GitHubRenames) rest = Regex.Replace(rest, Regex.Escape(renamed.Key), renamed.Value, RegexOptions.IgnoreCase);
            return m.Groups[1].Value + name + rest;
        }

        /// <summary>True when the custom endpoint's token is stored DPAPI-protected in the env file.</summary>
        public bool CustomTokenRemembered { get; set; }

        /// <summary>Non-secret view for the UI and diagnostics.</summary>
        public Dictionary<string, object> ToPublicDictionary()
        {
            var resolved = Llm.Resolve();
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
                { "llmProfile", LlmSettings.ProfileToken(Llm.Profile) },
                { "llmResolvedProfile", LlmSettings.ProfileToken(resolved) },
                { "llmModel", Llm.Model(resolved) },
                { "llmUrl", Llm.Url(resolved) },
                { "hasLlmToken", Llm.IsConfigured(resolved) },
                { "llmMissing", Llm.MissingFor(resolved) },
                { "llmProfiles", LlmSettings.Concrete.Select(p => new Dictionary<string, object> { { "profile", LlmSettings.ProfileToken(p) }, { "model", Llm.Model(p) }, { "url", Llm.Url(p) }, { "configured", Llm.IsConfigured(p) }, { "missing", Llm.MissingFor(p) }, { "format", LlmSettings.FormatToken(Llm.Format(p)) } }).ToList() },
                { "llmCustom", new Dictionary<string, object> { { "url", Llm.Url(LlmProfile.Custom) }, { "model", Llm.Model(LlmProfile.Custom) }, { "format", LlmSettings.FormatToken(Llm.CustomFormat) }, { "headers", Llm.CustomHeaders == null ? new List<string>() : Llm.CustomHeaders.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList() }, { "tokenSet", !string.IsNullOrWhiteSpace(Llm.Token(LlmProfile.Custom)) }, { "tokenRemembered", CustomTokenRemembered } } },
                { "envFile", EnvFilePath },
                { "envFileExists", File.Exists(EnvFilePath) },
                { "rootDirectory", RootDirectory },
                { "outputDirectory", OutputDirectory },
            };
        }
    }
}
