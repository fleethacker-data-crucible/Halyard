using System;
using System.Linq;
using System.Net;
using System.Text;

namespace Cascade.Core.Analysis
{
    /// <summary>
    /// Renders the leadership view as one self-contained HTML page: inline styles, tables, no script, so it
    /// reads the same in a browser, in Outlook and as a PDF. Mirrors the dashboard's layout.
    /// </summary>
    public static class DeliveryHtml
    {
        public static string Render(DeliveryReport r, string title = "Cascade Leadership Dashboard")
        {
            var sb = new StringBuilder();
            Func<string?, string> h = s => WebUtility.HtmlEncode(s ?? string.Empty);
            var velocityText = string.Join(" → ", r.Velocity.Where(v => v.IsPast).Skip(Math.Max(0, r.Velocity.Count(v => v.IsPast) - 3)).Select(v => v.PointsDone.ToString()));
            var escalated = r.Blockers.Count(b => b.Escalated);
            var avgAge = r.Blockers.Count == 0 ? 0 : Math.Round(r.Blockers.Average(b => b.AgeBusinessDays), 1);

            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>").Append(h(title)).Append("</title></head>");
            sb.Append("<body style=\"margin:0;background:#eef1f4;font-family:Segoe UI,Arial,sans-serif;color:#16202a;font-size:13px\">");
            sb.Append("<div style=\"max-width:980px;margin:0 auto;padding:16px\">");
            // header
            sb.Append("<div style=\"background:#134e4a;color:#fff;padding:12px 16px;border-radius:6px 6px 0 0\"><table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            sb.Append("<td style=\"font-size:16px;font-weight:600\">").Append(h(title)).Append(" <span style=\"font-weight:400;opacity:.8\">| Feature Progress • Velocity • Capacity • Blockers</span></td>");
            sb.Append("<td align=\"right\" style=\"font-size:12px;opacity:.85\">Sprint ").Append(h(r.CurrentSprint)).Append(" • ").Append(r.GeneratedAt.ToString("dd MMM yyyy")).Append("</td></tr></table></div>");
            sb.Append("<div style=\"background:#fff;border:1px solid #d3dae1;border-top:0;padding:16px\">");

            // tiles
            sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            Tile(sb, "Features on track", r.FeaturesOnTrack + " / " + r.Features.Count, r.FeaturesActive + " active", r.Features.Count > 0 && r.FeaturesOnTrack * 1.0 / r.Features.Count < 0.6 ? "#b45309" : "#15803d");
            Tile(sb, "Velocity (last 3)", velocityText.Length == 0 ? "—" : velocityText, "pts / sprint, avg " + r.AverageVelocity, "#16202a");
            Tile(sb, "Ready backlog", r.Ready.Stories + " stories", r.Ready.Points + " pts" + (r.Ready.Healthy ? " · healthy" : " · thin (< 1.5× velocity)"), r.Ready.Healthy ? "#15803d" : "#b45309");
            Tile(sb, "Capacity used", r.Capacity.Configured ? r.Capacity.UsedPercent + "%" : "n/a", r.Capacity.Configured ? r.Capacity.PlannedPoints + " of " + r.Capacity.TeamCapacity + " pts · " + Pct(r.Capacity.Committed) + "/" + Pct(r.Capacity.Allocated) + "/" + Pct(r.Capacity.Experiment) + " commit/alloc/exp" : "no team config", r.Capacity.UsedPercent > 100 ? "#b91c1c" : "#16202a");
            Tile(sb, "Open blockers", r.Blockers.Count.ToString(), escalated > 0 ? escalated + " escalated (> " + Delivery.EscalationBusinessDays + " business days), avg " + avgAge + "d" : r.Blockers.Count == 0 ? "none" : "avg " + avgAge + " business days", r.Blockers.Count == 0 ? "#15803d" : escalated > 0 ? "#b91c1c" : "#b45309");
            sb.Append("</tr></table>");

            // feature table
            sb.Append("<h3 style=\"margin:18px 0 6px;font-size:14px\">Feature % complete <span style=\"font-weight:400;color:#7b8895\">(primary leadership view)</span></h3>");
            sb.Append("<table width=\"100%\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:12.5px\">");
            sb.Append("<tr style=\"background:#1e3a8a;color:#fff\"><th align=\"left\">Feature</th><th align=\"left\">Epic</th><th align=\"left\" width=\"160\">% done</th><th align=\"left\">Status</th><th align=\"left\">Target</th><th align=\"left\">Owner</th></tr>");
            foreach (var f in r.Features)
            {
                var color = f.Status == FeatureStatus.Done ? "#15803d" : f.Status == FeatureStatus.OnTrack ? "#15803d" : f.Status == FeatureStatus.AtRisk ? "#b45309" : "#7b8895";
                var label = f.Status == FeatureStatus.OnTrack ? "On Track" : f.Status == FeatureStatus.AtRisk ? "At Risk" : f.Status == FeatureStatus.Parked ? "Parked" : "Done";
                sb.Append("<tr style=\"border-bottom:1px solid #e5eaef\"><td>").Append(h(f.Title)).Append("<div style=\"color:#7b8895;font-size:11px\">").Append(f.StoriesDone).Append("/").Append(f.Stories).Append(" stories · ").Append(f.PointsDone).Append("/").Append(f.Points).Append(" pts</div></td>");
                sb.Append("<td>").Append(h(f.Epic)).Append("</td>");
                sb.Append("<td><div style=\"background:#e5eaef;height:9px;border-radius:5px;width:120px;display:inline-block;vertical-align:middle\"><div style=\"width:").Append(f.Percent).Append("%;height:9px;border-radius:5px;background:").Append(color).Append("\"></div></div> <span style=\"font-variant-numeric:tabular-nums\">").Append(f.Percent).Append("%</span></td>");
                sb.Append("<td><span style=\"color:").Append(color).Append(";font-weight:600\">").Append(label).Append("</span><div style=\"color:#7b8895;font-size:11px\">").Append(h(f.Reason)).Append("</div></td>");
                sb.Append("<td>").Append(h(f.Target ?? "—")).Append("</td><td>").Append(h(f.Owner ?? "—")).Append("</td></tr>");
            }
            if (r.Features.Count == 0) sb.Append("<tr><td colspan=\"6\" style=\"color:#7b8895\">No features in the snapshot.</td></tr>");
            sb.Append("</table>");

            // velocity + capacity
            sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:18px\"><tr><td width=\"55%\" valign=\"top\">");
            sb.Append("<h3 style=\"margin:0 0 6px;font-size:14px\">Velocity (story points completed)</h3>");
            var max = Math.Max(1, r.Velocity.Count == 0 ? 1 : r.Velocity.Max(v => Math.Max(v.PointsDone, v.PointsPlanned)));
            sb.Append("<table cellpadding=\"4\" cellspacing=\"0\" style=\"font-size:12px\">");
            foreach (var v in r.Velocity)
            {
                sb.Append("<tr><td style=\"width:70px;color:#4b5a69\">").Append(h(v.Sprint)).Append(v.IsCurrent ? "<span style=\"color:#7b8895\"> (now)</span>" : string.Empty).Append("</td>");
                sb.Append("<td><div style=\"background:#e5eaef;height:12px;width:260px;border-radius:3px\"><div style=\"width:").Append((int)Math.Round(100.0 * v.PointsDone / max)).Append("%;height:12px;background:#1e3a8a;border-radius:3px\"></div></div></td>");
                sb.Append("<td style=\"font-variant-numeric:tabular-nums\">").Append(v.PointsDone).Append(v.IsCurrent ? " / " + v.PointsPlanned + " planned" : string.Empty).Append("</td></tr>");
            }
            sb.Append("</table></td><td valign=\"top\">");
            sb.Append("<h3 style=\"margin:0 0 6px;font-size:14px\">Sprint ").Append(h(r.CurrentSprint)).Append(" capacity allocation</h3>");
            if (r.Capacity.Configured)
            {
                sb.Append("<div style=\"display:flex;height:22px;border-radius:4px;overflow:hidden;font-size:11px;color:#fff;max-width:360px\">");
                Seg(sb, "Committed " + Pct(r.Capacity.Committed), r.Capacity.Committed, "#15803d");
                Seg(sb, "Allocated " + Pct(r.Capacity.Allocated), r.Capacity.Allocated, "#b45309");
                Seg(sb, "Experiment " + Pct(r.Capacity.Experiment), r.Capacity.Experiment, "#1d4ed8");
                sb.Append("</div><div style=\"color:#7b8895;font-size:11px;margin-top:6px\">").Append(r.Capacity.PlannedPoints).Append(" pts planned of ").Append(r.Capacity.TeamCapacity).Append(" capacity across ").Append(r.Capacity.Teams).Append(" team(s), ").Append(r.Capacity.People).Append(" people; ").Append(r.Capacity.DonePoints).Append(" pts done so far.</div>");
            }
            else sb.Append("<div style=\"color:#7b8895\">No team configuration.</div>");
            sb.Append("<h3 style=\"margin:14px 0 6px;font-size:14px\">Themes</h3><table cellpadding=\"3\" cellspacing=\"0\" style=\"font-size:12px\">");
            foreach (var t in r.Themes) sb.Append("<tr><td style=\"color:#4b5a69\">").Append(h(t.Title)).Append("</td><td style=\"font-variant-numeric:tabular-nums\">").Append(t.PointsDone).Append("/").Append(t.Points).Append(" pts · ").Append(t.Percent).Append("%</td></tr>");
            sb.Append("</table></td></tr></table>");

            // blockers
            sb.Append("<h3 style=\"margin:18px 0 6px;font-size:14px\">Open blockers (OPS / dependencies) <span style=\"font-weight:400;color:#7b8895\">— escalated when age > ").Append(Delivery.EscalationBusinessDays).Append(" business days</span></h3>");
            sb.Append("<table width=\"100%\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:12.5px\">");
            sb.Append("<tr style=\"background:#1e3a8a;color:#fff\"><th align=\"left\">Item</th><th align=\"left\">Description</th><th align=\"left\">Type</th><th align=\"left\">Age</th><th align=\"left\">Owner</th><th align=\"left\">Impact</th></tr>");
            foreach (var b in r.Blockers)
            {
                sb.Append("<tr style=\"border-bottom:1px solid #e5eaef\"><td style=\"white-space:nowrap\">").Append(b.Url != null ? "<a href=\"" + h(b.Url) + "\" style=\"color:#1d4ed8\">#" + b.Number + "</a>" : "#" + b.Number).Append("</td>");
                sb.Append("<td>").Append(h(b.Title)).Append(b.Reason != null ? "<div style=\"color:#7b8895;font-size:11px\">" + h(b.Reason) + "</div>" : string.Empty).Append("</td>");
                sb.Append("<td>").Append(h(b.Type.ToUpperInvariant())).Append("</td><td style=\"color:").Append(b.Escalated ? "#b91c1c;font-weight:600" : "#16202a").Append("\">").Append(b.AgeBusinessDays).Append("d</td>");
                sb.Append("<td>").Append(h(b.Owner ?? "—")).Append("</td><td>").Append(h(b.Impact ?? "—")).Append("</td></tr>");
            }
            if (r.Blockers.Count == 0) sb.Append("<tr><td colspan=\"6\" style=\"color:#15803d\">No open blockers.</td></tr>");
            sb.Append("</table>");

            sb.Append("<p style=\"color:#7b8895;font-size:11px;margin-top:16px\">Primary object: feature % complete. Secondary: epic status, velocity trend, ready-backlog health, capacity split, blocker age. Story checklists and full proposals for work are deliberately not shown. Generated by Cascade from a read-only snapshot");
            if (!string.IsNullOrEmpty(r.Repository)) sb.Append(" of ").Append(h(r.Repository));
            sb.Append(".");
            foreach (var n in r.Notes) sb.Append(" ").Append(h(n));
            sb.Append("</p></div></div></body></html>");
            return sb.ToString();
        }

        private static void Tile(StringBuilder sb, string label, string value, string hint, string color)
        {
            sb.Append("<td valign=\"top\" style=\"padding:4px\"><div style=\"border:1px solid #d3dae1;border-radius:6px;padding:10px 12px;background:#f5f7f9\"><div style=\"color:#4b5a69;font-size:12px\">").Append(WebUtility.HtmlEncode(label)).Append("</div><div style=\"font-size:20px;font-weight:600;color:").Append(color).Append(";font-variant-numeric:tabular-nums\">").Append(WebUtility.HtmlEncode(value)).Append("</div><div style=\"color:#7b8895;font-size:11px\">").Append(WebUtility.HtmlEncode(hint)).Append("</div></div></td>");
        }

        private static void Seg(StringBuilder sb, string label, double share, string color)
        {
            if (share <= 0) return;
            sb.Append("<div style=\"width:").Append((int)Math.Round(share * 100)).Append("%;background:").Append(color).Append(";text-align:center;line-height:22px;white-space:nowrap;overflow:hidden\">").Append(WebUtility.HtmlEncode(label)).Append("</div>");
        }

        private static string Pct(double share)
        {
            return (int)Math.Round(share * 100) + "%";
        }
    }
}
