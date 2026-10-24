using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cascade.Core.Llm;
using Cascade.Core.Model;
using Cascade.Core.Providers;
using Cascade.Core.Safety;

namespace Cascade.Core.Config
{
    /// <summary>
    /// Everything the host needs, resolved from environment variables (optionally pre-loaded from
    /// <c>config/cascade.env</c>). Secrets stay in memory; <see cref="ToPublicDictionary"/> never includes them.
    /// </summary>
    public sealed class CascadeSettings
    {
        public string RootDirectory { get; set; } = Directory.GetCurrentDirectory();
        public string OutputDirectory { get { return Path.Combine(RootDirectory, "output"); } }
        public string LogDirectory { get { return Path.Combine(RootDirectory, "logs"); } }
        public string ConfigDirectory { get { return Path.Combine(RootDirectory, "config"); } }
        public string WebRootDirectory { get; set; } = string.Empty;

        public ProviderSettings Provider { get; set; } = new ProviderSettings();
        public LlmSettings Llm { get; set; } = new LlmSettings();
        public WriteMode WriteMode { get; set; } = WriteMode.Sandbox;
        public bool AllowProduction { get; set; }
        public string? LabelFilter { get; set; }
        public int PreferredPort { get; set; } = 8773;
        public int MaxSyncItems { get; set; } = 2000;

        public string TaxonomyPath { get { return Path.Combine(ConfigDirectory, "taxonomy.json"); } }
        public string ProposalsDirectory { get { return Path.Combine(OutputDirectory, "proposals"); } }
        public string SnapshotsDirectory { get { return Path.Combine(OutputDirectory, "snapshots"); } }
        public string SeedsDirectory { get { return Path.Combine(OutputDirectory, "seeds"); } }
        public string TranscriptsDirectory { get { return Path.Combine(OutputDirectory, "llm_transcripts"); } }

        public WritePolicy BuildWritePolicy()
        {
            var policy = new WritePolicy { Mode = WriteMode, AllowProduction = AllowProduction };
            var sandbox = Provider.Sandbox;
            if (sandbox != null) policy.SandboxTargets.Add(sandbox);
            var source = Provider.Source;
            if (source != null)
            {
                if (WriteMode == WriteMode.Production) policy.ProductionTarget = source;
                else if (sandbox == null || !sandbox.Equals(source)) policy.ProtectedTargets.Add(source);
            }
            return policy;
        }

        /// <summary>Reads the process environment. Call <see cref="LoadEnvFile"/> first to merge config/cascade.env.</summary>
        public static CascadeSettings FromEnvironment(string rootDirectory)
        {
            var s = new CascadeSettings { RootDirectory = rootDirectory };
            Func<string, string?> env = name =>
            {
                var v = Environment.GetEnvironmentVariable(name);
                return string.IsNullOrWhiteSpace(v) ? null : v!.Trim();
            };

            var kindText = env("CASCADE_PROVIDER") ?? "github";
            s.Provider.Kind = kindText.Equals("gitlab", StringComparison.OrdinalIgnoreCase) ? ProviderKind.GitLab : ProviderKind.GitHub;
            if (s.Provider.Kind == ProviderKind.GitLab)
            {
                s.Provider.BaseUrl = env("GITLAB_URL") ?? string.Empty;
                s.Provider.SourceRepository = env("CASCADE_SOURCE_REPO") ?? string.Empty;
                // The write target comes only from CASCADE_SANDBOX_REPO; generic names such as GITLAB_PROJECT_ID
                // are often set to a real project and must never become writable by accident.
                s.Provider.SandboxRepository = env("CASCADE_SANDBOX_REPO") ?? string.Empty;
                s.Provider.Token = env("GITLAB_TOKEN");
            }
            else
            {
                s.Provider.BaseUrl = env("GITHUB_API_URL") ?? string.Empty;
                s.Provider.SourceRepository = env("CASCADE_SOURCE_REPO") ?? string.Empty;
                s.Provider.SandboxRepository = env("CASCADE_SANDBOX_REPO") ?? string.Empty;
                s.Provider.Token = env("GITHUB_TOKEN") ?? env("GH_TOKEN");
            }
            int delay;
            if (int.TryParse(env("CASCADE_WRITE_DELAY_MS"), out delay)) s.Provider.WriteDelayMs = delay;

            s.WriteMode = string.Equals(env("CASCADE_WRITE_MODE"), "production", StringComparison.OrdinalIgnoreCase) ? WriteMode.Production : WriteMode.Sandbox;
            s.AllowProduction = env("CASCADE_ALLOW_PRODUCTION") == "1" || string.Equals(env("CASCADE_ALLOW_PRODUCTION"), "true", StringComparison.OrdinalIgnoreCase);
            s.LabelFilter = env("CASCADE_LABEL_FILTER");
            int port;
            if (int.TryParse(env("CASCADE_PORT"), out port) && port >= 1024 && port <= 65535) s.PreferredPort = port;
            int max;
            if (int.TryParse(env("CASCADE_MAX_SYNC_ITEMS"), out max) && max > 0) s.MaxSyncItems = max;
            s.WebRootDirectory = env("CASCADE_WEBROOT") ?? string.Empty;

            var profile = (env("CASCADE_LLM_PROFILE") ?? "auto").ToLowerInvariant();
            s.Llm.Profile = profile == "genai" ? LlmProfile.GenAi : profile == "grok" ? LlmProfile.Grok : profile == "openai" ? LlmProfile.OpenAi : LlmProfile.Auto;
            s.Llm.GenAiUrl = env("GENAI_URL");
            s.Llm.GenAiModel = env("GENAI_MODEL");
            s.Llm.GenAiToken = env("GENAI_TOKEN");
            s.Llm.GrokUrl = env("DATABRICKS_URL") ?? env("ADVANA_URL");
            s.Llm.GrokModel = env("DATABRICKS_MODEL") ?? env("ADVANA_MODEL");
            s.Llm.GrokToken = env("DATABRICKS_TOKEN");
            s.Llm.OpenAiUrl = env("LLM_URL") ?? env("OPENAI_BASE_URL");
            s.Llm.OpenAiModel = env("LLM_MODEL") ?? env("OPENAI_MODEL");
            s.Llm.OpenAiToken = env("LLM_TOKEN") ?? env("OPENAI_API_KEY");
            int maxTokens;
            if (int.TryParse(env("CASCADE_LLM_MAX_TOKENS"), out maxTokens) && maxTokens > 0) s.Llm.MaxTokens = maxTokens;
            s.Llm.TranscriptDirectory = env("CASCADE_LLM_TRANSCRIPTS") == "0" ? null : s.TranscriptsDirectory;
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

        /// <summary>Non-secret view for the UI and diagnostics.</summary>
        public Dictionary<string, object?> ToPublicDictionary()
        {
            var resolved = Llm.Resolve();
            return new Dictionary<string, object?>
            {
                { "provider", Provider.Kind.ToString().ToLowerInvariant() },
                { "baseUrl", Provider.EffectiveBaseUrl },
                { "sourceRepository", Provider.SourceRepository },
                { "sandboxRepository", Provider.SandboxRepository },
                { "hasProviderToken", Provider.HasToken },
                { "writeMode", WriteMode.ToString().ToLowerInvariant() },
                { "allowProduction", AllowProduction },
                { "labelFilter", LabelFilter },
                { "preferredPort", PreferredPort },
                { "maxSyncItems", MaxSyncItems },
                { "llmProfile", Llm.Profile.ToString().ToLowerInvariant() },
                { "llmResolvedProfile", resolved.ToString().ToLowerInvariant() },
                { "llmModel", Llm.Model(resolved) },
                { "llmUrl", Llm.Url(resolved) },
                { "hasLlmToken", Llm.IsConfigured(resolved) },
                { "llmProfiles", new[] { LlmProfile.GenAi, LlmProfile.Grok, LlmProfile.OpenAi }.Select(p => new Dictionary<string, object?> { { "profile", p.ToString().ToLowerInvariant() }, { "model", Llm.Model(p) }, { "configured", Llm.IsConfigured(p) } }).ToList() },
                { "rootDirectory", RootDirectory },
                { "outputDirectory", OutputDirectory },
            };
        }
    }
}
