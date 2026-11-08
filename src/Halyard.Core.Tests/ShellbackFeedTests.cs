using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Planning;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Live Shellback cards from an export file/folder or an endpoint (fake transport), cached for the team loader.</summary>
    public sealed class ShellbackFeedTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "halyard-shellback-" + Guid.NewGuid().ToString("N"));
        private string Live { get { return Path.Combine(_dir, "live"); } }

        public ShellbackFeedTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static string Card(string id, string name, bool consent = true, string skill = "sql-pipelines", int confidence = 3)
        {
            return new JObject
            {
                ["format"] = "synapse-peer-card", ["version"] = 1, ["id"] = id, ["name"] = name, ["consent"] = consent,
                ["skills"] = new JArray(skill), ["surveys"] = new JObject { [skill] = new JObject { ["confidence"] = confidence } },
            }.ToString();
        }

        private string Write(string name, string text)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        [Fact]
        public void ReadsASingleCardAnArrayAndABundle()
        {
            var warnings = new System.Collections.Generic.List<string>();
            Assert.Single(ShellbackFeed.Parse(Card("a", "Ann"), warnings, "x"));
            Assert.Equal(2, ShellbackFeed.Parse("[" + Card("a", "Ann") + "," + Card("b", "Bo") + "]", warnings, "x").Count);
            Assert.Equal(2, ShellbackFeed.Parse("{\"cards\":[" + Card("a", "Ann") + "," + Card("b", "Bo") + "]}", warnings, "x").Count);
            Assert.Empty(warnings);
            Assert.Throws<InvalidDataException>(() => ShellbackFeed.Parse("not json", warnings, "x"));
        }

        [Fact]
        public void ABundleFileFeedsTheTeamAndOnlyConsentedCardsAreKept()
        {
            var export = Write("export.json", "{\"cards\":[" + Card("ann", "Ann Example") + "," + Card("bo", "Bo Example", consent: false) + ",{\"name\":\"junk\"}]}");
            var result = ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Equal(3, result.Fetched);
            Assert.Equal(1, result.Written);
            Assert.Equal(1, result.SkippedNoConsent);
            Assert.Equal(1, result.SkippedInvalid);
            Assert.Equal(new[] { "ann.json" }, Directory.GetFiles(Live).Select(Path.GetFileName).ToArray());

            var team = TeamConfig.Load(Path.Combine(_dir, "none.json"), new[] { Live });
            Assert.Equal(1, team.CardsImported);
            var ann = team.FindPerson("Ann Example")!;
            Assert.True(ann.Has("sql-pipelines"));
            Assert.Equal(3, ann.Confidence("sql-pipelines"));
        }

        [Fact]
        public void RefreshingAgainWritesNothingNewAndPicksUpChanges()
        {
            var export = Write("export.json", "[" + Card("ann", "Ann", confidence: 2) + "]");
            ShellbackFeed.RefreshFromFile(export, Live);
            var again = ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Equal(0, again.Written);
            Assert.Equal(1, again.Unchanged);

            File.WriteAllText(export, "[" + Card("ann", "Ann", confidence: 4) + "]");
            var changed = ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Equal(1, changed.Written);
            Assert.Equal(4, TeamConfig.Load(Path.Combine(_dir, "none.json"), new[] { Live }).FindPerson("Ann")!.Confidence("sql-pipelines"));
        }

        [Fact]
        public void WithdrawnConsentAndLeaversAreRemovedFromTheCache()
        {
            var export = Write("export.json", "[" + Card("ann", "Ann") + "," + Card("bo", "Bo") + "]");
            ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Equal(2, Directory.GetFiles(Live).Length);

            File.WriteAllText(export, "[" + Card("ann", "Ann", consent: false) + "]");
            var result = ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Equal(2, result.Removed);
            Assert.Empty(Directory.GetFiles(Live));
        }

        [Fact]
        public void AFolderOfCardsIsAnExportToo()
        {
            var folder = Path.Combine(_dir, "export");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.json"), Card("a", "Ann"));
            File.WriteAllText(Path.Combine(folder, "b.json"), Card("b", "Bo"));
            Assert.Equal(2, ShellbackFeed.RefreshFromFile(folder, Live).Written);
        }

        [Fact]
        public void AMissingOrBrokenExportChangesNothing()
        {
            var export = Write("export.json", "[" + Card("ann", "Ann") + "]");
            ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Throws<FileNotFoundException>(() => ShellbackFeed.RefreshFromFile(Path.Combine(_dir, "gone.json"), Live));
            File.WriteAllText(export, "{ broken");
            Assert.Throws<InvalidDataException>(() => ShellbackFeed.RefreshFromFile(export, Live));
            Assert.Single(Directory.GetFiles(Live));
        }

        [Fact]
        public void CardIdsBecomeSafeFileNames()
        {
            var export = Write("export.json", "[" + Card("../../Evil Name!", "Eve") + "]");
            ShellbackFeed.RefreshFromFile(export, Live);
            Assert.Equal(new[] { "evil-name.json" }, Directory.GetFiles(Live).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public async Task AnEndpointIsReadWithGetAndABearerTokenOnly()
        {
            var http = new FakeTransport().On("GET", "shellback.example.org/cards", 200, "{\"cards\":[" + Card("ann", "Ann") + "]}");
            var result = await ShellbackFeed.RefreshFromUrlAsync(http, "https://shellback.example.org/cards?token=abc", "tok-123", Live, CancellationToken.None);
            Assert.Equal(1, result.Written);
            var call = Assert.Single(http.Calls);
            Assert.Equal("GET", call.Method);
            Assert.Equal("Bearer tok-123", call.Headers["Authorization"]);
            Assert.Empty(http.Writes());
            Assert.DoesNotContain("abc", result.Source);
            Assert.DoesNotContain("tok-123", result.Source);
        }

        [Fact]
        public async Task AnEndpointFailureOrBadUrlLeavesTheCacheAlone()
        {
            ShellbackFeed.RefreshFromFile(Write("export.json", "[" + Card("ann", "Ann") + "]"), Live);
            var down = new FakeTransport().On("GET", "/cards", 503, "unavailable");
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => ShellbackFeed.RefreshFromUrlAsync(down, "https://shellback.example.org/cards", null, Live, CancellationToken.None));
            Assert.Equal("SERVER_ERROR", ex.Code);
            await Assert.ThrowsAsync<ArgumentException>(() => ShellbackFeed.RefreshFromUrlAsync(down, "file:///c:/secrets.json", null, Live, CancellationToken.None));
            Assert.Single(Directory.GetFiles(Live));
        }

        [Fact]
        public void LiveCardsWinOverHandDroppedOnesAndAreOffUntilConfigured()
        {
            var settings = new HalyardSettings { RootDirectory = _dir };
            Assert.Equal(new[] { settings.ShellbackDirectory }, settings.ShellbackDirectories);
            Directory.CreateDirectory(settings.ShellbackLiveDirectory);
            Assert.Equal(new[] { settings.ShellbackLiveDirectory, settings.ShellbackDirectory }, settings.ShellbackDirectories);
        }

        [Fact]
        public void SettingsReadTheExportVariablesWithoutExposingTheToken()
        {
            var vars = new System.Collections.Generic.Dictionary<string, string>
            {
                { "HALYARD_SHELLBACK_EXPORT", "C:\\shared\\shellback" }, { "HALYARD_SHELLBACK_URL", "https://shellback.example.org/cards" }, { "HALYARD_SHELLBACK_TOKEN", "tok-secret" },
            };
            var s = HalyardSettings.FromEnvironment(_dir, n => vars.ContainsKey(n) ? vars[n] : null);
            Assert.Equal("C:\\shared\\shellback", s.ShellbackExportPath);
            Assert.Equal("https://shellback.example.org/cards", s.ShellbackUrl);
            Assert.Equal("tok-secret", s.ShellbackToken);
            Assert.DoesNotContain("tok-secret", JsonConvert.SerializeObject(s.ToPublicDictionary()));
        }
    }

    public sealed class ShellbackRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-shellroute-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private HalyardHost? _host;
        private HostInfo? _info;

        public void Dispose()
        {
            _host?.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void Start(Action<HalyardSettings> configure, IHttpTransport? transport = null)
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            configure(settings);
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null, Transport = transport });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        private static string Card(string id, string name)
        {
            return new JObject { ["format"] = "synapse-peer-card", ["id"] = id, ["name"] = name, ["consent"] = true, ["skills"] = new JArray("sql-pipelines") }.ToString();
        }

        [Fact]
        public async Task WithNothingConfiguredRefreshSaysWhatToSet()
        {
            Start(s => { });
            var reply = await _client.PostAsync(_info!.Url + "api/team/shellback/refresh", new StringContent("{}"));
            Assert.Equal(400, (int)reply.StatusCode);
            Assert.Contains("HALYARD_SHELLBACK_EXPORT", await reply.Content.ReadAsStringAsync());
            Assert.Equal("folder", (string)JObject.Parse(await _client.GetStringAsync(_info.Url + "api/team"))["shellbackSource"]!);
        }

        [Fact]
        public async Task AFileExportFeedsTheTeamWithoutAnExplicitRefresh()
        {
            var export = Path.Combine(_root, "export.json");
            Directory.CreateDirectory(_root);
            File.WriteAllText(export, "[" + Card("ann", "Ann Example") + "]");
            Start(s => s.ShellbackExportPath = export);
            var team = JObject.Parse(await _client.GetStringAsync(_info!.Url + "api/team"));
            Assert.Equal("export", (string)team["shellbackSource"]!);
            Assert.Contains(team["people"]!, p => (string)p["name"]! == "Ann Example" && (bool)p["fromShellback"]!);

            File.WriteAllText(export, "[" + Card("bo", "Bo Example") + "]");
            var after = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/team"));
            Assert.DoesNotContain(after["people"]!, p => (string)p["name"]! == "Ann Example");
            Assert.Contains(after["people"]!, p => (string)p["name"]! == "Bo Example");
        }

        [Fact]
        public async Task AnEndpointRefreshGoesThroughTheTransportAndReportsTheResult()
        {
            var http = new FakeTransport().On("GET", "shellback.example.org", 200, "[" + Card("ann", "Ann Example") + "]");
            Start(s => { s.ShellbackUrl = "https://shellback.example.org/cards"; s.ShellbackToken = "tok-secret"; }, http);
            var reply = await _client.PostAsync(_info!.Url + "api/team/shellback/refresh", new StringContent("{}"));
            var body = await reply.Content.ReadAsStringAsync();
            Assert.Equal(200, (int)reply.StatusCode);
            Assert.DoesNotContain("tok-secret", body);
            var json = JObject.Parse(body);
            Assert.Equal(1, (int)json["result"]!["written"]!);
            Assert.Contains(json["team"]!["people"]!, p => (string)p["name"]! == "Ann Example");
            Assert.Equal("Bearer tok-secret", http.Calls.Single().Headers["Authorization"]);
        }

        [Fact]
        public async Task AnEndpointErrorIsABadRequestNotACrash()
        {
            var http = new FakeTransport().On("GET", "shellback.example.org", 401, "{\"message\":\"no\"}");
            Start(s => s.ShellbackUrl = "https://shellback.example.org/cards", http);
            var reply = await _client.PostAsync(_info!.Url + "api/team/shellback/refresh", new StringContent("{}"));
            Assert.Equal(400, (int)reply.StatusCode);
            Assert.Contains("Shellback endpoint", await reply.Content.ReadAsStringAsync());
        }
    }
}
