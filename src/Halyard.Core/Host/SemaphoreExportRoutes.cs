using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Model;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Backlog health as CSV for Semaphore: read one table, or write all of them to a local folder. Nothing leaves the machine.</summary>
    public sealed partial class HalyardHost
    {
        public string SemaphoreExportDirectory { get { return Path.Combine(Settings.OutputDirectory, "exports", "semaphore"); } }

        private void RegisterSemaphoreExportRoutes()
        {
            Router.Get("/api/export/semaphore/{table}", r =>
            {
                var table = r.RouteValues["table"];
                if (Array.IndexOf(SemaphoreExport.TableNames, table) < 0) throw new NotFoundException("Tables: " + string.Join(", ", SemaphoreExport.TableNames) + ".");
                return Task.FromResult(HttpResponse.Text(200, SemaphoreTables(Query(r, "label"))[table], "text/csv; charset=utf-8"));
            });
            Router.Post("/api/export/semaphore", r =>
            {
                var body = Json.Body(r);
                var tables = SemaphoreTables(Json.Read<string>(body, "label"));
                var dir = SemaphoreExportDirectory;
                Directory.CreateDirectory(dir);
                var files = tables.Select(t =>
                {
                    var path = Path.Combine(dir, t.Key + ".csv");
                    File.WriteAllText(path, t.Value, new UTF8Encoding(false));
                    return new { table = t.Key, path, bytes = Encoding.UTF8.GetByteCount(t.Value) };
                }).ToList();
                Log.Info("SEMAPHORE_EXPORT", files.Count + " tables written to " + dir);
                return Task.FromResult(Json.Ok(new { schemaVersion = SemaphoreExport.SchemaVersion, directory = dir, files }));
            });
        }

        private System.Collections.Generic.Dictionary<string, string> SemaphoreTables(string label)
        {
            if (Snapshots.Current == null) throw new BadRequestException("Synchronize a repository first.");
            var effective = label ?? Settings.LabelFilter;
            System.Collections.Generic.List<WorkItem> all;
            var visible = VisibleItems(effective, out all);
            return SemaphoreExport.Tables(Review.Run(visible, Taxonomy, effective, all), BuildDelivery(effective));
        }
    }
}
