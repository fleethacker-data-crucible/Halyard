using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Model;

namespace Halyard.Core.Providers
{
    public sealed class ProviderSettings
    {
        public ProviderKind Kind { get; set; }
        /// <summary>GitHub: https://api.github.com by default. GitLab: the instance URL (GITLAB_URL, required; /api/v4 is added).</summary>
        public string BaseUrl { get; set; }
        /// <summary>
        /// Repositories to read from (the production backlog / source of truth), comma-separated when the
        /// backlog spans several. Never written in sandbox mode.
        /// </summary>
        public string SourceRepository { get; set; }

        /// <summary>Every configured source repository, in order, de-duplicated.</summary>
        public List<string> SourceRepositories
        {
            get
            {
                var list = new List<string>();
                foreach (var part in (SourceRepository ?? string.Empty).Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var id = part.Trim();
                    if (id.Length > 0 && !list.Contains(id, StringComparer.OrdinalIgnoreCase)) list.Add(id);
                }
                return list;
            }
        }
        /// <summary>Repository that may be written to (sandbox).</summary>
        public string SandboxRepository { get; set; }
        /// <summary>Held in memory only. Never persisted by the core.</summary>
        public string Token { get; set; }
        public int WriteDelayMs { get; set; }

        public string EffectiveBaseUrl
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(BaseUrl)) return BaseUrl.Trim().TrimEnd('/');
                return Kind == ProviderKind.GitHub ? "https://api.github.com" : string.Empty;
            }
        }

        /// <summary>The first source repository (most callers), or null.</summary>
        public RepositoryRef Source { get { var all = Sources; return all.Count == 0 ? null : all[0]; } }
        /// <summary>All source repositories as references.</summary>
        public List<RepositoryRef> Sources { get { return SourceRepositories.Select(id => new RepositoryRef(Kind, id)).ToList(); } }
        public RepositoryRef Sandbox { get { return string.IsNullOrWhiteSpace(SandboxRepository) ? null : new RepositoryRef(Kind, SandboxRepository); } }
        public bool HasToken { get { return !string.IsNullOrWhiteSpace(Token); } }

        public ProviderSettings()
        {
            Kind = ProviderKind.GitHub;
            BaseUrl = string.Empty;
            SourceRepository = string.Empty;
            SandboxRepository = string.Empty;
            WriteDelayMs = -1;
        }

        public ProviderSettings Clone()
        {
            return new ProviderSettings
            {
                Kind = Kind, BaseUrl = BaseUrl, SourceRepository = SourceRepository, SandboxRepository = SandboxRepository, Token = Token, WriteDelayMs = WriteDelayMs,
            };
        }
    }

    /// <summary>
    /// Repositories the owner renamed or moved on the host. GitHub redirects an old name, but the transport refuses
    /// redirects on purpose (a redirected write could go somewhere nobody configured), so a config that still
    /// names the old repository is mapped to the new name when settings load. The write policy then compares
    /// against the new name, and an existing config keeps working.
    /// </summary>
    public static class RenamedRepositories
    {
        private static readonly Dictionary<string, string> GitHub = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Kept for one release with the CASCADE_* fallback: an installed config/cascade.env, or a CASCADE_SANDBOX_REPO variable, can still name the old repository.
            { "sun-of-anton/cascade-sandbox", "fleethacker-data-crucible/Halyard-Ballast" },
            // The sandbox moved to the organization. The old address is now a personal fork with issues turned off, and
            // config/halyard.env files copied forward from cascade.env name it.
            { "sun-of-anton/Halyard-Ballast", "fleethacker-data-crucible/Halyard-Ballast" },
        };

        /// <summary>Former GitHub identifier to current identifier.</summary>
        public static IEnumerable<KeyValuePair<string, string>> GitHubRenames { get { return GitHub; } }

        /// <summary>The current identifier for <paramref name="identifier"/>, or the identifier itself when it was never renamed.</summary>
        public static string Canonical(ProviderKind kind, string identifier)
        {
            var id = (identifier ?? string.Empty).Trim().Trim('/');
            string renamed;
            if (kind == ProviderKind.GitHub && GitHub.TryGetValue(id, out renamed)) return renamed;
            return id;
        }

        /// <summary>Applies <see cref="Canonical"/> to each entry of a comma/semicolon/newline separated list, keeping the separators simple (comma).</summary>
        public static string CanonicalList(ProviderKind kind, string list)
        {
            if (string.IsNullOrWhiteSpace(list)) return string.Empty;
            var parts = list.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Canonical(kind, x)).Where(x => x.Length > 0);
            return string.Join(",", parts);
        }
    }

    public static class ProviderFactory
    {
        public static IWorkItemProvider Create(ProviderSettings settings, IHttpTransport transport = null)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            var repo = settings.Source ?? settings.Sandbox;
            if (repo == null) throw new InvalidOperationException("Configure a source or sandbox repository before connecting.");
            if (settings.Kind == ProviderKind.GitLab && string.IsNullOrWhiteSpace(settings.EffectiveBaseUrl))
                throw new InvalidOperationException("GITLAB_URL is required for the gitlab provider (for example https://gitlab.example.com).");
            var http = transport ?? new HttpClientTransport();
            switch (settings.Kind)
            {
                case ProviderKind.GitHub:
                    return new GitHubProvider(http, settings.EffectiveBaseUrl, settings.Token, repo, settings.WriteDelayMs);
                case ProviderKind.GitLab:
                    return new GitLabProvider(http, settings.EffectiveBaseUrl, settings.Token, repo, settings.WriteDelayMs);
                default:
                    throw new NotSupportedException("Unknown provider " + settings.Kind);
            }
        }
    }
}
