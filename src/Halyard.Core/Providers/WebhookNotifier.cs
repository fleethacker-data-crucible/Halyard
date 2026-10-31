using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Serialization;

namespace Halyard.Core.Providers
{
    public enum WebhookFormat
    {
        /// <summary>{ "text": "..." }: Slack and most generic receivers.</summary>
        Text = 0,
        /// <summary>Teams incoming webhook (MessageCard).</summary>
        Teams = 1,
    }

    /// <summary>
    /// Report delivery beyond email: posts a short plain-text summary of the leadership view to a Teams or Slack incoming webhook.
    /// The URL comes only from configuration (HALYARD_REPORT_WEBHOOK), never from a request, and is never echoed back. The message
    /// holds counts and titles from the backlog, nothing else.
    /// </summary>
    public static class WebhookNotifier
    {
        public static WebhookFormat FormatFor(string url)
        {
            var u = (url ?? string.Empty).ToLowerInvariant();
            return u.Contains("webhook.office.com") || u.Contains(".logic.azure.com") || u.Contains("outlook.office.com/webhook") ? WebhookFormat.Teams : WebhookFormat.Text;
        }

        public static string Summarise(DeliveryReport report, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title + " - " + report.CurrentSprint + (string.IsNullOrEmpty(report.Repository) ? string.Empty : " (" + report.Repository + ")"));
            sb.AppendLine("Features: " + report.FeaturesOnTrack + " of " + report.Features.Count + " on track or done; " + report.FeaturesActive + " still active.");
            sb.AppendLine("Velocity: " + report.AverageVelocity + " points per sprint (last three). Ready backlog: " + report.Ready.Stories + " stories, " + report.Ready.Points + " points.");
            if (report.Capacity.Configured) sb.AppendLine("Capacity: " + report.Capacity.PlannedPoints + " of " + report.Capacity.TeamCapacity + " points planned (" + report.Capacity.UsedPercent + "%).");
            if (report.Blockers.Count == 0) sb.AppendLine("Blockers: none.");
            else
            {
                sb.AppendLine("Blockers: " + report.Blockers.Count + (report.Blockers.Any(b => b.Escalated) ? " (" + report.Blockers.Count(b => b.Escalated) + " escalated)" : string.Empty) + ".");
                foreach (var b in report.Blockers.OrderByDescending(x => x.AgeDays).Take(5)) sb.AppendLine("- " + b.Title + " (" + b.AgeDays + " days)");
            }
            return sb.ToString().TrimEnd();
        }

        public static string Payload(WebhookFormat format, string title, string text)
        {
            if (format == WebhookFormat.Teams)
                return new JObject { { "@type", "MessageCard" }, { "@context", "https://schema.org/extensions" }, { "summary", title }, { "title", title }, { "text", text.Replace("\n", "\n\n") } }.ToString(Formatting.None);
            return new JObject { { "text", text } }.ToString(Formatting.None);
        }

        public static async Task SendAsync(IHttpTransport http, string url, string title, string text, CancellationToken cancellation)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("The report webhook must be an https URL.");
            var call = new HttpCall("POST", url);
            call.Headers["Accept"] = "application/json";
            call.Headers["User-Agent"] = "Halyard/1.0 (+delivery-intelligence)";
            call.JsonBody = Payload(FormatFor(url), title, text);
            var reply = await http.SendAsync(call, cancellation).ConfigureAwait(false);
            if (!reply.IsSuccess) throw new ProviderHttpException(reply.Status, "POST " + uri.Host + " (webhook) failed with HTTP " + reply.Status, reply.Body);
        }
    }
}
