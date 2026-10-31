using System;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Post the leadership summary to the configured Teams/Slack webhook. The destination is never taken from a request.</summary>
    public sealed partial class HalyardHost
    {
        private void RegisterWebhookRoutes()
        {
            Router.Post("/api/leadership/notify", async r =>
            {
                var body = Json.Body(r);
                var title = Json.Read<string>(body, "title") ?? "Halyard leadership summary";
                var report = BuildDelivery(Json.Read<string>(body, "label") ?? Settings.LabelFilter);
                var text = WebhookNotifier.Summarise(report, title);
                var url = Settings.ReportWebhookUrl;
                var configured = !string.IsNullOrWhiteSpace(url);
                if (Json.Read<bool?>(body, "dryRun") ?? false)
                    return Json.Ok(new { sent = false, configured, format = configured ? WebhookNotifier.FormatFor(url).ToString().ToLowerInvariant() : null, text });
                if (!configured) throw new BadRequestException("Set HALYARD_REPORT_WEBHOOK (an https Teams or Slack incoming webhook) in config/halyard.env.");
                try { await WebhookNotifier.SendAsync(Options.Transport ?? new HttpClientTransport(), url, title, text, CancellationToken.None).ConfigureAwait(false); }
                catch (ProviderHttpException ex) { throw new BadRequestException("The webhook refused the message: " + ex.Code + "."); }
                catch (ArgumentException ex) { throw new BadRequestException(ex.Message); }
                Log.Info("REPORT_WEBHOOK_SENT", WebhookNotifier.FormatFor(url) + ", " + text.Length + " characters");
                return Json.Ok(new { sent = true, configured, text });
            });
        }
    }
}
