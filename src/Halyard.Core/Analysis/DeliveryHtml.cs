using System;
using System.Linq;
using System.Net;
using System.Text;

namespace Halyard.Core.Analysis
{
    /// <summary>
    /// Renders the leadership view as one self-contained HTML page: inline styles, tables, no script, so it
    /// reads the same in a browser, in Outlook and as a PDF. Mirrors the dashboard's layout.
    /// </summary>
    public static class DeliveryHtml
    {
        public static string Render(DeliveryReport r, string title = "Halyard Leadership Dashboard")
        {
            return Render(r, title, null);
        }

        /// <param name="logoPngBase64">The brand mark as a base64 PNG (Outlook does not render SVG); omitted when null.</param>
        public static string Render(DeliveryReport r, string title, string logoPngBase64)
        {
            var sb = new StringBuilder();
            Func<string, string> h = s => WebUtility.HtmlEncode(s ?? string.Empty);
            var velocityText = string.Join(" → ", r.Velocity.Where(v => v.IsPast).Skip(Math.Max(0, r.Velocity.Count(v => v.IsPast) - 3)).Select(v => v.PointsDone.ToString()));
            var escalated = r.Blockers.Count(b => b.Escalated);
            var avgAge = r.Blockers.Count == 0 ? 0 : Math.Round(r.Blockers.Average(b => b.AgeBusinessDays), 1);

            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>").Append(h(title)).Append("</title></head>");
            sb.Append("<body style=\"margin:0;background:#eef1f4;font-family:Segoe UI,Arial,sans-serif;color:#16202a;font-size:13px\">");
            sb.Append("<div style=\"max-width:980px;margin:0 auto;padding:16px\">");
            // header
            sb.Append("<div style=\"background:#1e3a5f;color:#fff;padding:12px 16px;border-radius:6px 6px 0 0\"><table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            sb.Append("<td style=\"font-size:16px;font-weight:600\">");
            if (!string.IsNullOrEmpty(logoPngBase64)) sb.Append("<img src=\"data:image/png;base64,").Append(logoPngBase64).Append("\" width=\"28\" height=\"28\" alt=\"\" style=\"vertical-align:middle;margin-right:10px;border:0\">");
            sb.Append(h(title)).Append(" <span style=\"font-weight:400;opacity:.8\">| Feature Progress • Velocity • Capacity • Blockers</span></td>");
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

            // the picture book: four charts drawn with tables and background colours, which Outlook renders
            sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:18px\"><tr>");
            sb.Append("<td width=\"50%\" valign=\"top\" style=\"padding-right:8px\">");
            sb.Append("<h3 style=\"margin:0 0 6px;font-size:14px\">Feature status by sprint <span style=\"font-weight:400;color:#7b8895\">(past sprints reconstructed)</span></h3>");
            StatusColumns(sb, r);
            sb.Append("</td><td width=\"50%\" valign=\"top\" style=\"padding-left:8px\">");
            sb.Append("<h3 style=\"margin:0 0 6px;font-size:14px\">Velocity <span style=\"font-weight:400;color:#7b8895\">(story points closed; avg of last 3: ").Append(r.AverageVelocity).Append(")</span></h3>");
            VelocityColumns(sb, r);
            sb.Append("</td></tr><tr>");
            sb.Append("<td width=\"50%\" valign=\"top\" style=\"padding-right:8px;padding-top:16px\">");
            sb.Append("<h3 style=\"margin:0 0 6px;font-size:14px\">Sprint ").Append(h(r.CurrentSprint)).Append(" capacity by team</h3>");
            CapacityMeters(sb, r);
            sb.Append("</td><td width=\"50%\" valign=\"top\" style=\"padding-left:8px;padding-top:16px\">");
            sb.Append("<h3 style=\"margin:0 0 6px;font-size:14px\">Points by theme</h3>");
            ThemeBars(sb, r);
            sb.Append("</td></tr></table>");

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

            sb.Append("<p style=\"color:#7b8895;font-size:11px;margin-top:16px\">Primary object: feature % complete. Secondary: epic status, velocity trend, ready-backlog health, capacity split, blocker age. Story checklists and full proposals for work are deliberately not shown. Generated by Halyard from a read-only snapshot");
            if (!string.IsNullOrEmpty(r.Repository)) sb.Append(" of ").Append(h(r.Repository));
            sb.Append(".");
            foreach (var n in r.Notes) sb.Append(" ").Append(h(n));
            sb.Append("</p></div></div></body></html>");
            return sb.ToString();
        }

        // ---- chart helpers (tables + background colours only) ----

        private const string Series1 = "#2a78d6";
        private const string Grid = "#e5eaef";
        private const string Muted = "#7b8895";
        private static readonly string[][] StatusSeries =
        {
            new[] { "Done", "#1e3a5f", "#ffffff" }, new[] { "On track", "#0ca30c", "#ffffff" }, new[] { "At risk", "#fab219", "#16202a" },
            new[] { "Parked", "#9aa0a6", "#16202a" }, new[] { "Not started", "#d7dadf", "#16202a" },
        };

        private static int[] Counts(SprintStatusCount c) { return new[] { c.Done, c.OnTrack, c.AtRisk, c.Parked, c.NotStarted }; }

        private static string ShortSprint(string s)
        {
            var i = s.LastIndexOf('S');
            return i >= 0 && i < s.Length - 1 && s.Substring(i + 1).All(char.IsDigit) ? "S" + s.Substring(i + 1) : s;
        }

        /// <summary>Stacked columns, one per sprint: an outer table with bottom-aligned cells; each segment is a row with a fixed height.</summary>
        private static void StatusColumns(StringBuilder sb, DeliveryReport r)
        {
            var h = new Func<string, string>(x => WebUtility.HtmlEncode(x ?? string.Empty));
            if (r.StatusHistory.Count == 0) { sb.Append("<div style=\"color:").Append(Muted).Append("\">No sprints to show.</div>"); return; }
            const int plotHeight = 150;
            var max = Math.Max(1, r.StatusHistory.Max(c => c.Total));
            sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:11px\"><tr>");
            foreach (var c in r.StatusHistory)
            {
                sb.Append("<td valign=\"bottom\" align=\"center\" style=\"height:").Append(plotHeight + 16).Append("px;padding:0 10px;border-bottom:1px solid ").Append(Grid).Append("\">");
                sb.Append("<div style=\"color:").Append(Muted).Append(";margin-bottom:2px\">").Append(c.Total).Append("</div>");
                sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" width=\"34\" style=\"border-collapse:collapse\">");
                var counts = Counts(c);
                for (var i = counts.Length - 1; i >= 0; i--) // top of the stack first in document order
                {
                    if (counts[i] == 0) continue;
                    var px = Math.Max(2, (int)Math.Round(plotHeight * (double)counts[i] / max));
                    sb.Append("<tr><td height=\"").Append(px).Append("\" bgcolor=\"").Append(StatusSeries[i][1]).Append("\" align=\"center\" style=\"height:").Append(px).Append("px;line-height:").Append(px).Append("px;color:").Append(StatusSeries[i][2]).Append(";font-size:11px;border-bottom:2px solid #fff\">").Append(px >= 14 ? counts[i].ToString() : "&nbsp;").Append("</td></tr>");
                }
                sb.Append("</table></td>");
            }
            sb.Append("</tr><tr>");
            foreach (var c in r.StatusHistory) sb.Append("<td align=\"center\" style=\"padding:4px 10px 0;color:").Append(c.IsCurrent ? "#16202a;font-weight:600" : Muted).Append("\">").Append(h(ShortSprint(c.Sprint))).Append(c.IsCurrent ? " (now)" : string.Empty).Append("</td>");
            sb.Append("</tr></table>");
            LegendRow(sb, StatusSeries.Select(x => new[] { x[0], x[1] }).ToArray());
        }

        private static void VelocityColumns(StringBuilder sb, DeliveryReport r)
        {
            var h = new Func<string, string>(x => WebUtility.HtmlEncode(x ?? string.Empty));
            if (r.Velocity.Count == 0) { sb.Append("<div style=\"color:").Append(Muted).Append("\">No sprints to show.</div>"); return; }
            const int plotHeight = 150;
            var max = Math.Max(1.0, Math.Max(r.AverageVelocity, r.Velocity.Max(v => Math.Max(v.PointsDone, v.PointsPlanned))));
            sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:11px\"><tr>");
            foreach (var v in r.Velocity)
            {
                var donePx = (int)Math.Round(plotHeight * v.PointsDone / max);
                var plannedPx = v.IsCurrent ? (int)Math.Round(plotHeight * v.PointsPlanned / max) : 0;
                sb.Append("<td valign=\"bottom\" align=\"center\" style=\"height:").Append(plotHeight + 16).Append("px;padding:0 10px;border-bottom:1px solid ").Append(Grid).Append("\">");
                sb.Append("<div style=\"color:").Append(Muted).Append(";margin-bottom:2px\">").Append(v.PointsDone).Append(v.IsCurrent && v.PointsPlanned > v.PointsDone ? "/" + v.PointsPlanned : string.Empty).Append("</div>");
                sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" width=\"34\" style=\"border-collapse:collapse\">");
                if (plannedPx > donePx) sb.Append("<tr><td height=\"").Append(plannedPx - donePx).Append("\" style=\"height:").Append(plannedPx - donePx).Append("px;border:1px dashed ").Append(Series1).Append(";border-bottom:0\"></td></tr>");
                if (donePx > 0) sb.Append("<tr><td height=\"").Append(donePx).Append("\" bgcolor=\"").Append(Series1).Append("\" style=\"height:").Append(donePx).Append("px\"></td></tr>");
                sb.Append("</table></td>");
            }
            sb.Append("</tr><tr>");
            foreach (var v in r.Velocity) sb.Append("<td align=\"center\" style=\"padding:4px 10px 0;color:").Append(v.IsCurrent ? "#16202a;font-weight:600" : Muted).Append("\">").Append(h(ShortSprint(v.Sprint))).Append(v.IsCurrent ? " (now)" : string.Empty).Append("</td>");
            sb.Append("</tr></table>");
            sb.Append("<div style=\"font-size:11px;color:").Append(Muted).Append(";margin-top:6px\"><span style=\"display:inline-block;width:10px;height:10px;background:").Append(Series1).Append(";border-radius:3px;vertical-align:middle\"></span> points closed &nbsp; <span style=\"display:inline-block;width:10px;height:10px;border:1px dashed ").Append(Series1).Append(";border-radius:3px;vertical-align:middle\"></span> planned this sprint &nbsp; 3-sprint average ").Append(r.AverageVelocity).Append(" pts</div>");
        }

        private static void CapacityMeters(StringBuilder sb, DeliveryReport r)
        {
            var h = new Func<string, string>(x => WebUtility.HtmlEncode(x ?? string.Empty));
            if (!r.Capacity.Configured || r.TeamCapacity.Count == 0) { sb.Append("<div style=\"color:").Append(Muted).Append("\">No team configuration.</div>"); return; }
            var max = Math.Max(1, r.TeamCapacity.Max(t => Math.Max(t.Capacity, t.Planned)));
            const int trackWidth = 300;
            foreach (var t in r.TeamCapacity)
            {
                var over = t.Capacity > 0 && t.Planned > t.Capacity;
                var fill = over ? "#e34948" : Series1;
                var capPx = (int)Math.Round(trackWidth * (double)t.Capacity / max);
                var planPx = (int)Math.Round(trackWidth * (double)t.Planned / max);
                var donePx = (int)Math.Round(trackWidth * (double)t.Done / max);
                sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:12px;margin-bottom:10px\"><tr><td><b>").Append(h(t.Name)).Append("</b>").Append(t.People > 0 ? " <span style=\"color:" + Muted + "\">" + t.People + " people</span>" : string.Empty).Append("</td>");
                sb.Append("<td align=\"right\" style=\"color:").Append(over ? "#b91c1c" : "#4b5a69").Append(";font-variant-numeric:tabular-nums\">").Append(t.Planned).Append(t.Capacity > 0 ? " / " + t.Capacity : string.Empty).Append(" pts").Append(t.Capacity > 0 ? " · " + t.UsedPercent + "%" : string.Empty).Append("</td></tr>");
                sb.Append("<tr><td colspan=\"2\" style=\"padding-top:3px\"><table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\"><tr>");
                // the meter: done (solid) | planned beyond done (tint) | free capacity (track) | planned beyond capacity (red tint)
                var doneW = Math.Min(donePx, Math.Max(planPx, donePx));
                var planInsideW = Math.Max(0, Math.Min(planPx, capPx) - doneW);
                var freeW = Math.Max(0, capPx - doneW - planInsideW);
                var overW = Math.Max(0, planPx - Math.Max(capPx, doneW));
                if (doneW > 0) sb.Append("<td width=\"").Append(doneW).Append("\" height=\"12\" bgcolor=\"").Append(fill).Append("\" style=\"width:").Append(doneW).Append("px;height:12px\"></td>");
                if (planInsideW > 0) sb.Append("<td width=\"").Append(planInsideW).Append("\" height=\"12\" bgcolor=\"#bfd6f2\" style=\"width:").Append(planInsideW).Append("px;height:12px\"></td>");
                if (freeW > 0) sb.Append("<td width=\"").Append(freeW).Append("\" height=\"12\" bgcolor=\"#f5f7f9\" style=\"width:").Append(freeW).Append("px;height:12px;border:1px solid #d3dae1\"></td>");
                if (overW > 0) sb.Append("<td width=\"2\" height=\"12\" bgcolor=\"#16202a\" style=\"width:2px;height:12px\"></td><td width=\"").Append(overW).Append("\" height=\"12\" bgcolor=\"#f5c2c2\" style=\"width:").Append(overW).Append("px;height:12px\"></td>");
                sb.Append("</tr></table></td></tr>");
                sb.Append("<tr><td style=\"font-size:11px;color:").Append(Muted).Append("\">").Append(t.Capacity > 0 ? "committed " + Pct(t.Committed) + " · allocated " + Pct(t.Allocated) + " · experiment " + Pct(t.Experiment) : "owners not on any team").Append("</td><td align=\"right\" style=\"font-size:11px;color:").Append(Muted).Append("\">").Append(t.Done).Append(" done</td></tr></table>");
            }
            LegendRow(sb, new[] { new[] { "Done", Series1 }, new[] { "Planned", "#bfd6f2" }, new[] { "Free capacity", "#f5f7f9" }, new[] { "Over capacity", "#f5c2c2" } });
        }

        private static void ThemeBars(StringBuilder sb, DeliveryReport r)
        {
            var h = new Func<string, string>(x => WebUtility.HtmlEncode(x ?? string.Empty));
            if (r.Themes.Count == 0) { sb.Append("<div style=\"color:").Append(Muted).Append("\">No themes in the snapshot.</div>"); return; }
            var max = Math.Max(1, r.Themes.Max(t => t.Points));
            const int trackWidth = 300;
            foreach (var t in r.Themes.OrderByDescending(t => t.Points))
            {
                var totalPx = (int)Math.Round(trackWidth * (double)t.Points / max);
                var donePx = (int)Math.Round(trackWidth * (double)t.PointsDone / max);
                sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:12px;margin-bottom:8px\"><tr><td>").Append(h(t.Title)).Append("</td><td align=\"right\" style=\"color:#4b5a69;font-variant-numeric:tabular-nums\">").Append(t.PointsDone).Append("/").Append(t.Points).Append(" pts · ").Append(t.Percent).Append("%</td></tr>");
                sb.Append("<tr><td colspan=\"2\" style=\"padding-top:3px\"><table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\"><tr>");
                if (donePx > 0) sb.Append("<td width=\"").Append(donePx).Append("\" height=\"10\" bgcolor=\"").Append(Series1).Append("\" style=\"width:").Append(donePx).Append("px;height:10px\"></td>");
                if (totalPx - donePx > 0) sb.Append("<td width=\"").Append(totalPx - donePx).Append("\" height=\"10\" bgcolor=\"#bfd6f2\" style=\"width:").Append(totalPx - donePx).Append("px;height:10px\"></td>");
                sb.Append("</tr></table></td></tr>");
                sb.Append("<tr><td colspan=\"2\" style=\"font-size:11px;color:").Append(Muted).Append("\">").Append(t.FeaturesDone).Append(" of ").Append(t.Features).Append(" feature").Append(t.Features == 1 ? "" : "s").Append(" done</td></tr></table>");
            }
            LegendRow(sb, new[] { new[] { "Done", Series1 }, new[] { "Remaining", "#bfd6f2" } });
        }

        private static void LegendRow(StringBuilder sb, string[][] items)
        {
            sb.Append("<div style=\"font-size:11px;color:").Append(Muted).Append(";margin-top:6px\">");
            foreach (var it in items) sb.Append("<span style=\"display:inline-block;width:10px;height:10px;background:").Append(it[1]).Append(";border-radius:3px;vertical-align:middle;border:1px solid #d3dae1\"></span> ").Append(WebUtility.HtmlEncode(it[0])).Append(" &nbsp; ");
            sb.Append("</div>");
        }

        private static void Tile(StringBuilder sb, string label, string value, string hint, string color)
        {
            sb.Append("<td valign=\"top\" style=\"padding:4px\"><div style=\"border:1px solid #d3dae1;border-radius:6px;padding:10px 12px;background:#f5f7f9\"><div style=\"color:#4b5a69;font-size:12px\">").Append(WebUtility.HtmlEncode(label)).Append("</div><div style=\"font-size:20px;font-weight:600;color:").Append(color).Append(";font-variant-numeric:tabular-nums\">").Append(WebUtility.HtmlEncode(value)).Append("</div><div style=\"color:#7b8895;font-size:11px\">").Append(WebUtility.HtmlEncode(hint)).Append("</div></div></td>");
        }


        private static string Pct(double share)
        {
            return (int)Math.Round(share * 100) + "%";
        }
    }
}
