using System;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Planning;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Live Shellback cards: refresh the consented-card cache from the configured export or endpoint.</summary>
    public sealed partial class HalyardHost
    {
        private void RegisterShellbackRoutes()
        {
            Router.Post("/api/team/shellback/refresh", async r =>
            {
                try
                {
                    ShellbackRefreshResult result;
                    if (!string.IsNullOrWhiteSpace(Settings.ShellbackUrl))
                        result = await ShellbackFeed.RefreshFromUrlAsync(Options.Transport ?? new Providers.HttpClientTransport(), Settings.ShellbackUrl, Settings.ShellbackToken, Settings.ShellbackLiveDirectory, CancellationToken.None).ConfigureAwait(false);
                    else if (!string.IsNullOrWhiteSpace(Settings.ShellbackExportPath))
                        result = ShellbackFeed.RefreshFromFile(Settings.ShellbackExportPath, Settings.ShellbackLiveDirectory);
                    else throw new BadRequestException("Set HALYARD_SHELLBACK_EXPORT (a file or folder) or HALYARD_SHELLBACK_URL in config/halyard.env.");
                    Log.Info("SHELLBACK_REFRESHED", result.Source + ": " + result.Written + " written, " + result.Unchanged + " unchanged, " + result.Removed + " removed, " + result.SkippedNoConsent + " without consent");
                    return Json.Ok(new { result, team = TeamPayload() });
                }
                catch (Providers.ProviderHttpException ex) { throw new BadRequestException("Shellback endpoint: " + ex.Message); }
                catch (System.IO.IOException ex) { throw new BadRequestException(ex.Message); }
                catch (ArgumentException ex) { throw new BadRequestException(ex.Message); }
            });
        }

        /// <summary>A file export is cheap to re-read, so it is refreshed whenever the team is loaded; a failure leaves the last good cache.</summary>
        private void RefreshShellbackFile()
        {
            if (string.IsNullOrWhiteSpace(Settings.ShellbackExportPath) || !string.IsNullOrWhiteSpace(Settings.ShellbackUrl)) return;
            try { ShellbackFeed.RefreshFromFile(Settings.ShellbackExportPath, Settings.ShellbackLiveDirectory); }
            catch (System.IO.IOException ex) { Log.Warn("SHELLBACK_EXPORT_UNREADABLE", ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warn("SHELLBACK_EXPORT_UNREADABLE", ex.Message); }
        }
    }
}
