using System;

namespace Cascade.Core.Model
{
    public enum ProviderKind
    {
        GitHub = 1,
        GitLab = 2,
    }

    /// <summary>
    /// Identifies a repository (GitHub) or project (GitLab) independently of the provider's API.
    /// <see cref="Identifier"/> is <c>owner/repo</c> for GitHub and either a numeric project id or a
    /// <c>group/project</c> path for GitLab. Comparison is case-insensitive on the identifier.
    /// </summary>
    public sealed class RepositoryRef : IEquatable<RepositoryRef>
    {
        public RepositoryRef(ProviderKind provider, string identifier, string? displayName = null, string? url = null)
        {
            if (string.IsNullOrWhiteSpace(identifier)) throw new ArgumentException("Repository identifier is required.", "identifier");
            Provider = provider;
            Identifier = identifier.Trim().Trim('/');
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Identifier : displayName!.Trim();
            Url = url;
        }

        public ProviderKind Provider { get; private set; }
        public string Identifier { get; private set; }
        public string DisplayName { get; private set; }
        public string? Url { get; private set; }

        public string ProviderToken { get { return Provider.ToString().ToLowerInvariant(); } }

        /// <summary>Stable key used inside work-item keys: <c>github:owner/repo</c> or <c>gitlab:52999</c>.</summary>
        public string Key { get { return ProviderToken + ":" + Identifier; } }

        public static RepositoryRef Parse(ProviderKind provider, string identifier)
        {
            return new RepositoryRef(provider, identifier);
        }

        public bool Equals(RepositoryRef? other)
        {
            if (other == null) return false;
            return Provider == other.Provider && string.Equals(Identifier, other.Identifier, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as RepositoryRef);
        }

        public override int GetHashCode()
        {
            return (int)Provider * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Identifier);
        }

        public override string ToString()
        {
            return Key;
        }
    }
}
