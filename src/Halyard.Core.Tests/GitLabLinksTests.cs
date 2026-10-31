using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    public class GitLabServerVersionTests
    {
        [Theory]
        [InlineData("16.9", 16, 9, true)]
        [InlineData("v16.9.2", 16, 9, true)]
        [InlineData("17.0.1-ee", 17, 0, true)]
        [InlineData(" 13.4 ", 13, 4, true)]
        [InlineData("latest", 0, 0, false)]
        [InlineData("16", 0, 0, false)]
        [InlineData("", 0, 0, false)]
        [InlineData(null, 0, 0, false)]
        public void ParsesTheVersionsPeopleWrite(string? text, int major, int minor, bool ok)
        {
            int ma, mi;
            Assert.Equal(ok, GitLabServerVersion.TryParse(text!, out ma, out mi));
            if (ok) { Assert.Equal(major, ma); Assert.Equal(minor, mi); }
        }

        [Theory]
        [InlineData("13.4", true)]
        [InlineData("13.3.9", false)]
        [InlineData("12.10", false)]
        [InlineData("14.0", true)]
        [InlineData("16.9", true)]
        [InlineData("garbage", false)]
        [InlineData(null, false)]
        public void AUnknownOrOldServerNeverSwitchesLinksOn(string? version, bool expected)
        {
            Assert.Equal(expected, GitLabServerVersion.AtLeast(version!, 13, 4));
            var provider = new GitLabProvider(new FakeTransport(), "https://gitlab.example.com", "t", new RepositoryRef(ProviderKind.GitLab, "5678"), 0) { ServerVersion = version };
            Assert.Equal(expected, provider.NativeLinksAvailable);
        }
    }

    public sealed class GitLabLinksTests : IDisposable
    {
        private static readonly RepositoryRef Project = new RepositoryRef(ProviderKind.GitLab, "5678");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-links-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly FakeTransport _http = new FakeTransport().On("GET", "/projects/5678/issues/7/links", 200,
            "[{\"iid\":9,\"project_id\":5678,\"title\":\"Blocked by this\",\"state\":\"opened\",\"web_url\":\"https://gitlab.example.com/x/-/issues/9\",\"link_type\":\"is_blocked_by\"}," +
            "{\"iid\":11,\"project_id\":99,\"title\":\"Related\",\"state\":\"closed\",\"link_type\":\"relates_to\"},{\"title\":\"no iid\"}]");
        private HalyardHost? _host;
        private HostInfo? _info;

        public void Dispose()
        {
            _host?.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void Start(ProviderKind kind, string? version)
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = kind;
            settings.Provider.BaseUrl = "https://gitlab.example.com";
            settings.Provider.Token = "t";
            settings.Provider.SandboxRepository = kind == ProviderKind.GitLab ? "5678" : "o/sandbox";
            settings.Provider.SourceRepository = kind == ProviderKind.GitLab ? "1234" : "o/source";
            settings.Provider.GitLabVersion = version;
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null, Transport = _http });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        [Fact]
        public async Task ReadsLinksWithTheirTypesAndSkipsEntriesWithoutAnIid()
        {
            var provider = new GitLabProvider(_http, "https://gitlab.example.com", "t", Project, 0) { ServerVersion = "16.9" };
            var links = await provider.ListLinksAsync(7, CancellationToken.None);
            Assert.Equal(2, links.Count);
            Assert.Equal("is_blocked_by", links[0].Type);
            Assert.Equal(9, links[0].TargetNumber);
            Assert.Equal("5678", links[0].TargetProject);
            Assert.Equal(WorkItemState.Closed, links[1].State);
            Assert.All(_http.Calls, c => Assert.Equal("GET", c.Method));
        }

        [Fact]
        public async Task RefusesToCallTheApiWhenTheVersionIsNotConfirmed()
        {
            var provider = new GitLabProvider(_http, "https://gitlab.example.com", "t", Project, 0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListLinksAsync(7, CancellationToken.None));
            Assert.Empty(_http.Calls);
        }

        [Fact]
        public void TheSettingComesFromTheEnvironmentOnlyForGitLab()
        {
            var vars = new System.Collections.Generic.Dictionary<string, string> { { "HALYARD_PROVIDER", "gitlab" }, { "GITLAB_URL", "https://gitlab.example.com" }, { "HALYARD_GITLAB_VERSION", "16.9" } };
            Assert.Equal("16.9", HalyardSettings.FromEnvironment(_root, n => vars.ContainsKey(n) ? vars[n] : null).Provider.GitLabVersion);
            Assert.Null(HalyardSettings.FromEnvironment(_root, n => n == "HALYARD_GITLAB_VERSION" ? "16.9" : null).Provider.GitLabVersion);
            Assert.Null(HalyardSettings.FromEnvironment(_root, n => n == "HALYARD_PROVIDER" ? "gitlab" : null).Provider.GitLabVersion);
        }

        [Fact]
        public async Task TheRouteIsOffWithAReasonUntilTheVersionIsSet()
        {
            Start(ProviderKind.GitLab, null);
            var json = JObject.Parse(await _client.GetStringAsync(_info!.Url + "api/items/7/links"));
            Assert.False((bool)json["enabled"]!);
            Assert.Contains("HALYARD_GITLAB_VERSION", (string)json["reason"]!);
            Assert.Empty(_http.Calls);
        }

        [Fact]
        public async Task TheRouteReturnsLinksOnAConfirmedServer()
        {
            Start(ProviderKind.GitLab, "16.9");
            var json = JObject.Parse(await _client.GetStringAsync(_info!.Url + "api/items/7/links"));
            Assert.True((bool)json["enabled"]!);
            Assert.Equal("16.9", (string)json["serverVersion"]!);
            Assert.Equal(2, ((JArray)json["links"]!).Count);
            Assert.Equal("is_blocked_by", (string)json["links"]![0]!["type"]!);
            Assert.Equal("closed", (string)json["links"]![1]!["state"]!);
        }

        [Fact]
        public async Task GitHubHasNoNativeLinkReadAndAnOldVersionStaysOff()
        {
            Start(ProviderKind.GitHub, null);
            var gh = JObject.Parse(await _client.GetStringAsync(_info!.Url + "api/items/7/links"));
            Assert.False((bool)gh["enabled"]!);
            Assert.Contains("GitLab only", (string)gh["reason"]!);
        }

        [Fact]
        public async Task ABadNumberIsABadRequest()
        {
            Start(ProviderKind.GitLab, "16.9");
            Assert.Equal(400, (int)(await _client.GetAsync(_info!.Url + "api/items/abc/links")).StatusCode);
        }
    }
}
