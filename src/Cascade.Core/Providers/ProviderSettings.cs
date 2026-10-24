using System;
using Cascade.Core.Model;

namespace Cascade.Core.Providers
{
    public sealed class ProviderSettings
    {
        public ProviderKind Kind { get; set; } = ProviderKind.GitHub;
        /// <summary>GitHub: https://api.github.com by default. GitLab: the instance URL (GITLAB_URL, required; /api/v4 is added).</summary>
        public string BaseUrl { get; set; } = string.Empty;
        /// <summary>Repository to read from (the production backlog / source of truth). Never written in sandbox mode.</summary>
        public string SourceRepository { get; set; } = string.Empty;
        /// <summary>Repository that may be written to (sandbox).</summary>
        public string SandboxRepository { get; set; } = string.Empty;
        /// <summary>Held in memory only. Never persisted by the core.</summary>
        public string? Token { get; set; }
        public int WriteDelayMs { get; set; } = -1;

        public string EffectiveBaseUrl
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(BaseUrl)) return BaseUrl.Trim().TrimEnd('/');
                return Kind == ProviderKind.GitHub ? "https://api.github.com" : string.Empty;
            }
        }

        public RepositoryRef? Source { get { return string.IsNullOrWhiteSpace(SourceRepository) ? null : new RepositoryRef(Kind, SourceRepository); } }
        public RepositoryRef? Sandbox { get { return string.IsNullOrWhiteSpace(SandboxRepository) ? null : new RepositoryRef(Kind, SandboxRepository); } }
        public bool HasToken { get { return !string.IsNullOrWhiteSpace(Token); } }

        public ProviderSettings Clone()
        {
            return new ProviderSettings
            {
                Kind = Kind, BaseUrl = BaseUrl, SourceRepository = SourceRepository, SandboxRepository = SandboxRepository, Token = Token, WriteDelayMs = WriteDelayMs,
            };
        }
    }

    public static class ProviderFactory
    {
        public static IWorkItemProvider Create(ProviderSettings settings, IHttpTransport? transport = null)
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
