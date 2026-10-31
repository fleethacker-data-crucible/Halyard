using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Native GitLab issue links for one item, read live. Off (with the reason) unless the provider is GitLab and the server version is confirmed.</summary>
    public sealed partial class HalyardHost
    {
        private async Task<HttpResponse> GetItemLinks(HttpRequest r)
        {
            long number;
            if (!long.TryParse(r.RouteValues["number"], out number)) throw new BadRequestException("The item number must be a number.");
            if (Settings.Provider.Kind != Model.ProviderKind.GitLab)
                return Json.Ok(new { enabled = false, reason = "Native links are read from GitLab only; GitHub sub-issues are not read.", serverVersion = (string)null, links = new object[0] });
            var version = Settings.Provider.GitLabVersion;
            if (!GitLabServerVersion.AtLeast(version, 13, 4))
                return Json.Ok(new { enabled = false, reason = "Native links need a confirmed GitLab 13.4 or newer. Set HALYARD_GITLAB_VERSION (for example 16.9) in config/halyard.env once the server version is known.", serverVersion = version, links = new object[0] });
            var provider = GetProvider() as GitLabProvider;
            if (provider == null) throw new BadRequestException("The provider is not GitLab.");
            var sandbox = SandboxOrThrow();
            var links = await ((GitLabProvider)provider.ForRepository(sandbox)).ListLinksAsync(number, CancellationToken.None).ConfigureAwait(false);
            return Json.Ok(new { enabled = true, reason = (string)null, serverVersion = version, links = links.Select(l => new { l.Type, number = l.TargetNumber, project = l.TargetProject, l.Title, state = l.State == Model.WorkItemState.Closed ? "closed" : "open", l.Url }).ToList() });
        }
    }
}
