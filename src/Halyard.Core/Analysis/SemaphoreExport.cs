using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Halyard.Core.Model;

namespace Halyard.Core.Analysis
{
    /// <summary>
    /// Backlog health as CSV tables for Semaphore (it loads CSV files as data sources). Four tables with fixed, documented columns
    /// (docs/SEMAPHORE-EXPORT.md): <c>health</c> (one row), <c>items</c>, <c>features</c> and <c>velocity</c>. Read-only: built from the
    /// current snapshot, written to a local folder, sent nowhere. Text cells that a spreadsheet would read as a formula are prefixed with
    /// an apostrophe.
    /// </summary>
    public static class SemaphoreExport
    {
        public const int SchemaVersion = 1;
        public static readonly string[] TableNames = { "health", "items", "features", "velocity" };

        public static readonly Dictionary<string, string[]> Columns = new Dictionary<string, string[]>
        {
            { "health", new[] { "generated_at", "repository", "total_items", "needs_review", "missing_parents", "stories", "stories_with_points", "features", "features_with_effort", "features_active", "features_on_track", "average_velocity", "ready_stories", "ready_points", "blockers" } },
            { "items", new[] { "key", "number", "type", "id", "title", "state", "parent_id", "parent_key", "theme", "area", "points", "effort", "sprint", "labels", "needs_review", "deficiencies", "missing_meta", "updated_at", "url" } },
            { "features", new[] { "id", "title", "epic", "theme", "owner", "target", "stories", "stories_done", "points", "points_done", "percent", "blocked", "status", "reason" } },
            { "velocity", new[] { "sprint", "number", "points_planned", "points_done", "stories_done", "is_current" } },
        };

        public static Dictionary<string, string> Tables(ReviewReport review, DeliveryReport delivery)
        {
            var tables = new Dictionary<string, string>();
            tables["health"] = Csv(Columns["health"], new[]
            {
                new[]
                {
                    Date(delivery.GeneratedAt), delivery.Repository, review.Summary.Total.ToString(CultureInfo.InvariantCulture), review.Summary.NeedsReview.ToString(CultureInfo.InvariantCulture),
                    review.Summary.MissingParents.ToString(CultureInfo.InvariantCulture), review.Summary.Stories.ToString(CultureInfo.InvariantCulture), review.Summary.StoriesWithPoints.ToString(CultureInfo.InvariantCulture),
                    review.Summary.Features.ToString(CultureInfo.InvariantCulture), review.Summary.FeaturesWithEffort.ToString(CultureInfo.InvariantCulture), delivery.FeaturesActive.ToString(CultureInfo.InvariantCulture),
                    delivery.FeaturesOnTrack.ToString(CultureInfo.InvariantCulture), delivery.AverageVelocity.ToString("0.0", CultureInfo.InvariantCulture), delivery.Ready.Stories.ToString(CultureInfo.InvariantCulture),
                    delivery.Ready.Points.ToString(CultureInfo.InvariantCulture), delivery.Blockers.Count.ToString(CultureInfo.InvariantCulture),
                },
            });
            tables["items"] = Csv(Columns["items"], review.Items.Select(i => new[]
            {
                i.Key, i.Number.ToString(CultureInfo.InvariantCulture), WorkItemTypes.ToToken(i.Type), i.CanonicalId, i.Title, i.State == WorkItemState.Closed ? "closed" : "open",
                i.ParentId, i.ResolvedParentKey, i.ThemeId, i.Area, i.Points, i.Effort, i.Sprint, string.Join(";", i.Labels), i.NeedsReview ? "true" : "false",
                string.Join(";", i.Deficiencies), string.Join(";", i.MissingMeta), i.UpdatedAt == null ? string.Empty : Date(i.UpdatedAt.Value), i.Url,
            }));
            tables["features"] = Csv(Columns["features"], delivery.Features.Select(f => new[]
            {
                f.CanonicalId, f.Title, f.Epic, f.Theme, f.Owner, f.Target, f.Stories.ToString(CultureInfo.InvariantCulture), f.StoriesDone.ToString(CultureInfo.InvariantCulture),
                f.Points.ToString(CultureInfo.InvariantCulture), f.PointsDone.ToString(CultureInfo.InvariantCulture), f.Percent.ToString(CultureInfo.InvariantCulture),
                f.Blocked.ToString(CultureInfo.InvariantCulture), f.Status.ToString().ToLowerInvariant(), f.Reason,
            }));
            tables["velocity"] = Csv(Columns["velocity"], delivery.Velocity.Select(v => new[]
            {
                v.Sprint, v.Number.ToString(CultureInfo.InvariantCulture), v.PointsPlanned.ToString(CultureInfo.InvariantCulture), v.PointsDone.ToString(CultureInfo.InvariantCulture),
                v.StoriesDone.ToString(CultureInfo.InvariantCulture), v.IsCurrent ? "true" : "false",
            }));
            return tables;
        }

        private static string Date(DateTimeOffset d)
        {
            return d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>RFC 4180: CRLF line ends, quotes doubled, a cell quoted when it holds a comma, quote or line break.</summary>
        public static string Csv(string[] header, IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", header.Select(Cell))).Append("\r\n");
            foreach (var row in rows) sb.Append(string.Join(",", row.Select(Cell))).Append("\r\n");
            return sb.ToString();
        }

        public static string Cell(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var v = value;
            if ("=+-@\t\r".IndexOf(v[0]) >= 0 && !IsNumber(v)) v = "'" + v;
            if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        private static bool IsNumber(string v)
        {
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
    }
}
