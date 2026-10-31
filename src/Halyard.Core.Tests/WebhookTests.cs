using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    public class WebhookNotifierTests
    {
        private static DeliveryReport Report()
        {
            var items = SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(100)), Items.Repo);
            return Delivery.Build(items, TeamFixture.Load(), TeamFixture.Now, "example-org/synthetic");
        }

        [Theory]
        [InlineData("https://hooks.slack.com/services/T0/B0/xyz", WebhookFormat.Text)]
        [InlineData("https://example.webhook.office.com/webhookb2/abc", WebhookFormat.Teams)]
        [InlineData("https://prod-12.westeurope.logic.azure.com/workflows/abc", WebhookFormat.Teams)]
        [InlineData("https://chat.example.org/hook", WebhookFormat.Text)]
        public void PicksTheFormatFromTheUrl(string url, WebhookFormat expected)
        {
            Assert.Equal(expected, WebhookNotifier.FormatFor(url));
        }

        [Fact]
        public void SummaryHasCountsAndBlockersAndNothingElse()
        {
            var report = Report();
            var text = WebhookNotifier.Summarise(report, "Weekly");
            Assert.StartsWith("Weekly - " + report.CurrentSprint, text);
            Assert.Contains("Features: " + report.FeaturesOnTrack + " of " + report.Features.Count, text);
            Assert.Contains("Velocity: " + report.AverageVelocity, text);
            Assert.Contains("Blockers: " + report.Blockers.Count, text);
            Assert.DoesNotContain("<", text);
        }

        [Fact]
        public void PayloadsFitSlackAndTeams()
        {
            var slack = JObject.Parse(WebhookNotifier.Payload(WebhookFormat.Text, "T", "line1\nline2"));
            Assert.Equal("line1\nline2", (string)slack["text"]!);
            var teams = JObject.Parse(WebhookNotifier.Payload(WebhookFormat.Teams, "T", "line1\nline2"));
            Assert.Equal("MessageCard", (string)teams["@type"]!);
            Assert.Equal("T", (string)teams["title"]!);
            Assert.Equal("line1\n\nline2", (string)teams["text"]!);
        }

        [Fact]
        public async Task PostsJsonToTheWebhookAndOnlyOverHttps()
        {
            var http = new FakeTransport().On("POST", "hooks.slack.com", 200, "ok");
            await WebhookNotifier.SendAsync(http, "https://hooks.slack.com/services/T0/B0/xyz", "T", "hello", CancellationToken.None);
            var call = Assert.Single(http.Calls);
            Assert.Equal("POST", call.Method);
            Assert.Equal("hello", (string)JObject.Parse(call.JsonBody!)["text"]!);
            await Assert.ThrowsAsync<ArgumentException>(() => WebhookNotifier.SendAsync(http, "http://hooks.slack.com/x", "T", "hello", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => WebhookNotifier.SendAsync(http, "file:///c:/x", "T", "hello", CancellationToken.None));
            Assert.Single(http.Calls);
        }

        [Fact]
        public async Task ARefusalIsAnErrorWithoutTheUrlInIt()
        {
            var http = new FakeTransport().On("POST", "hooks.slack.com", 404, "no_service");
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => WebhookNotifier.SendAsync(http, "https://hooks.slack.com/services/T0/B0/secretpart", "T", "hello", CancellationToken.None));
            Assert.Equal("NOT_FOUND", ex.Code);
            Assert.DoesNotContain("secretpart", ex.Message);
        }
    }

    public sealed class WebhookRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-webhook-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly FakeTransport _http = new FakeTransport().On("POST", "hooks.slack.com", 200, "ok");
        private readonly HalyardHost _host;
        private readonly HostInfo _info;

        public WebhookRouteTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null, Transport = _http });
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30)), Items.Repo), null);
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private Task<HttpResponseMessage> Notify(string body) { return _client.PostAsync(_info.Url + "api/leadership/notify", new StringContent(body)); }

        [Fact]
        public async Task NothingIsSentWithoutAConfiguredWebhookAndTheRequestCannotChooseOne()
        {
            var reply = await Notify("{\"url\":\"https://hooks.slack.com/services/evil\"}");
            Assert.Equal(400, (int)reply.StatusCode);
            Assert.Contains("HALYARD_REPORT_WEBHOOK", await reply.Content.ReadAsStringAsync());
            Assert.Empty(_http.Calls);
        }

        [Fact]
        public async Task DryRunShowsTheMessageAndSendsNothing()
        {
            _host.Settings.ReportWebhookUrl = "https://hooks.slack.com/services/T0/B0/secretpart";
            var reply = await Notify("{\"dryRun\":true,\"title\":\"Weekly\"}");
            var body = await reply.Content.ReadAsStringAsync();
            Assert.Equal(200, (int)reply.StatusCode);
            var json = JObject.Parse(body);
            Assert.False((bool)json["sent"]!);
            Assert.True((bool)json["configured"]!);
            Assert.StartsWith("Weekly", (string)json["text"]!);
            Assert.DoesNotContain("secretpart", body);
            Assert.Empty(_http.Calls);
        }

        [Fact]
        public async Task SendsTheSummaryToTheConfiguredWebhook()
        {
            _host.Settings.ReportWebhookUrl = "https://hooks.slack.com/services/T0/B0/secretpart";
            var reply = await Notify("{\"title\":\"Weekly\"}");
            var body = await reply.Content.ReadAsStringAsync();
            Assert.Equal(200, (int)reply.StatusCode);
            Assert.True((bool)JObject.Parse(body)["sent"]!);
            Assert.DoesNotContain("secretpart", body);
            var call = Assert.Single(_http.Calls);
            Assert.Equal("https://hooks.slack.com/services/T0/B0/secretpart", call.Url);
            Assert.StartsWith("Weekly", (string)JObject.Parse(call.JsonBody!)["text"]!);
        }

        [Fact]
        public void SettingsReadTheWebhookAndKeepItOutOfThePublicConfig()
        {
            var s = HalyardSettings.FromEnvironment(_root, n => n == "HALYARD_REPORT_WEBHOOK" ? "https://hooks.slack.com/services/T0/B0/secretpart" : null);
            Assert.Equal("https://hooks.slack.com/services/T0/B0/secretpart", s.ReportWebhookUrl);
            Assert.DoesNotContain("secretpart", JsonConvert.SerializeObject(s.ToPublicDictionary()));
        }
    }
}
