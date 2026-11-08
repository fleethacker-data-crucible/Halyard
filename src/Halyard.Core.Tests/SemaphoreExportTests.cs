using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Model;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    public class SemaphoreExportTests
    {
        private static readonly string[] HealthColumns = { "generated_at", "repository", "total_items", "needs_review", "missing_parents", "stories", "stories_with_points", "features", "features_with_effort", "features_active", "features_on_track", "average_velocity", "ready_stories", "ready_points", "blockers" };
        private static readonly string[] ItemColumns = { "key", "number", "type", "id", "title", "state", "parent_id", "parent_key", "theme", "area", "points", "effort", "sprint", "labels", "needs_review", "deficiencies", "missing_meta", "updated_at", "url" };
        private static readonly string[] FeatureColumns = { "id", "title", "epic", "theme", "owner", "target", "stories", "stories_done", "points", "points_done", "percent", "blocked", "status", "reason" };
        private static readonly string[] VelocityColumns = { "sprint", "number", "points_planned", "points_done", "stories_done", "is_current" };

        /// <summary>Minimal RFC 4180 reader so the tests check the file as a consumer would, not the writer's own idea of it.</summary>
        private static string[][] Parse(string csv)
        {
            var rows = new System.Collections.Generic.List<string[]>();
            var row = new System.Collections.Generic.List<string>();
            var cell = new System.Text.StringBuilder();
            var quoted = false;
            for (var i = 0; i < csv.Length; i++)
            {
                var c = csv[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); i++; }
                else cell.Append(c);
            }
            Assert.Empty(row);
            Assert.Equal(0, cell.Length);
            return rows.ToArray();
        }

        private static System.Collections.Generic.Dictionary<string, string> Tables(out ReviewReport review, out DeliveryReport delivery)
        {
            var options = SyntheticOptions.ForTotal(100);
            options.IncludeDefects = true;
            var items = SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(options), Items.Repo);
            review = Review.Run(items, Schema.Taxonomy.Default());
            delivery = Delivery.Build(items, TeamFixture.Load(), TeamFixture.Now, "example-org/synthetic");
            return SemaphoreExport.Tables(review, delivery);
        }

        [Fact]
        public void ColumnsArePinned()
        {
            Assert.Equal(HealthColumns, SemaphoreExport.Columns["health"]);
            Assert.Equal(ItemColumns, SemaphoreExport.Columns["items"]);
            Assert.Equal(FeatureColumns, SemaphoreExport.Columns["features"]);
            Assert.Equal(VelocityColumns, SemaphoreExport.Columns["velocity"]);
            Assert.Equal(new[] { "health", "items", "features", "velocity" }, SemaphoreExport.TableNames);
        }

        [Fact]
        public void EveryTableHasItsHeaderAndRectangularRows()
        {
            ReviewReport review; DeliveryReport delivery;
            var tables = Tables(out review, out delivery);
            Assert.Equal(SemaphoreExport.TableNames.OrderBy(x => x), tables.Keys.OrderBy(x => x));
            foreach (var name in SemaphoreExport.TableNames)
            {
                var rows = Parse(tables[name]);
                Assert.Equal(SemaphoreExport.Columns[name], rows[0]);
                Assert.All(rows, r => Assert.Equal(rows[0].Length, r.Length));
                Assert.EndsWith("\r\n", tables[name]);
            }
        }

        [Fact]
        public void TheRowsMatchTheReviewAndTheDeliveryReport()
        {
            ReviewReport review; DeliveryReport delivery;
            var tables = Tables(out review, out delivery);
            var health = Parse(tables["health"]);
            Assert.Equal(2, health.Length);
            Assert.Equal(review.Summary.Total.ToString(), health[1][2]);
            Assert.Equal(review.Summary.NeedsReview.ToString(), health[1][3]);
            Assert.Equal(delivery.Blockers.Count.ToString(), health[1][14]);
            Assert.Equal("example-org/synthetic", health[1][1]);

            var items = Parse(tables["items"]);
            Assert.Equal(review.Items.Count + 1, items.Length);
            Assert.Equal(review.Summary.NeedsReview, items.Skip(1).Count(r => r[14] == "true"));
            Assert.Contains(items.Skip(1), r => r[15].Length > 0); // the defect catalogue has deficiencies
            Assert.All(items.Skip(1), r => Assert.Matches("^(STORY|FEATURE|EPIC|THEME|UNCLASSIFIED|[A-Z]+)$", r[2]));

            var features = Parse(tables["features"]);
            Assert.Equal(delivery.Features.Count + 1, features.Length);
            Assert.All(features.Skip(1), r => Assert.Contains(r[12], new[] { "done", "ontrack", "atrisk", "parked" }));

            var velocity = Parse(tables["velocity"]);
            Assert.Equal(delivery.Velocity.Count + 1, velocity.Length);
            Assert.Equal(1, velocity.Skip(1).Count(r => r[5] == "true"));
        }

        [Fact]
        public void QuotingFollowsRfc4180AndFormulasAreDefused()
        {
            Assert.Equal("plain", SemaphoreExport.Cell("plain"));
            Assert.Equal("\"a,b\"", SemaphoreExport.Cell("a,b"));
            Assert.Equal("\"say \"\"hi\"\"\"", SemaphoreExport.Cell("say \"hi\""));
            Assert.Equal("\"two\nlines\"", SemaphoreExport.Cell("two\nlines"));
            Assert.Equal("'=SUM(A1:A9)", SemaphoreExport.Cell("=SUM(A1:A9)"));
            Assert.Equal("'@cmd", SemaphoreExport.Cell("@cmd"));
            Assert.Equal("'+1 call", SemaphoreExport.Cell("+1 call"));
            Assert.Equal("-3", SemaphoreExport.Cell("-3"));
            Assert.Equal(string.Empty, SemaphoreExport.Cell(null!));
            var round = Parse(SemaphoreExport.Csv(new[] { "a", "b" }, new[] { new[] { "x,\"y\"", "line1\r\nline2" } }));
            Assert.Equal("x,\"y\"", round[1][0]);
            Assert.Equal("line1\r\nline2", round[1][1]);
        }

        [Fact]
        public void ATitleThatLooksLikeAFormulaIsDefusedInTheItemsTable()
        {
            var w = new WorkItem(Items.Repo, 1) { Title = "=HYPERLINK(\"http://example.invalid\")" };
            var review = Review.Run(new[] { w }, Schema.Taxonomy.Default());
            var delivery = Delivery.Build(new[] { w }, TeamFixture.Load(), TeamFixture.Now, "r");
            var items = Parse(SemaphoreExport.Tables(review, delivery)["items"]);
            Assert.StartsWith("'=", items[1][4]);
        }
    }

    public sealed class SemaphoreExportRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-semexport-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly HalyardHost _host;
        private readonly HostInfo _info;

        public SemaphoreExportRouteTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        [Fact]
        public async Task NeedsASnapshotAndKnowsOnlyItsTables()
        {
            Assert.Equal(400, (int)(await _client.GetAsync(_info.Url + "api/export/semaphore/items")).StatusCode);
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30)), Items.Repo), null);
            Assert.Equal(404, (int)(await _client.GetAsync(_info.Url + "api/export/semaphore/secrets")).StatusCode);
        }

        [Fact]
        public async Task ServesATableAsCsvAndWritesAllFourWithoutABom()
        {
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30)), Items.Repo), null);
            var reply = await _client.GetAsync(_info.Url + "api/export/semaphore/velocity");
            Assert.Equal("text/csv", reply.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("sprint,number,points_planned", await reply.Content.ReadAsStringAsync());

            var post = await _client.PostAsync(_info.Url + "api/export/semaphore", new StringContent("{}"));
            Assert.Equal(200, (int)post.StatusCode);
            foreach (var t in SemaphoreExport.TableNames)
            {
                var path = Path.Combine(_host.SemaphoreExportDirectory, t + ".csv");
                var bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 20);
                Assert.NotEqual(0xEF, bytes[0]);
            }
            Assert.StartsWith("key,number,type", File.ReadAllText(Path.Combine(_host.SemaphoreExportDirectory, "items.csv")));
        }
    }
}
