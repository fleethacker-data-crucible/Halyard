using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Synthetic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Fails every call as if nothing listened on the port (connection refused).</summary>
    public sealed class RefusingTransport : IHttpTransport
    {
        public List<HttpCall> Calls = new List<HttpCall>();
        public Task<HttpReply> SendAsync(HttpCall call, CancellationToken cancellation)
        {
            Calls.Add(call);
            throw new ProviderHttpException(0, "No connection could be made because the target machine actively refused it.", null);
        }
    }

    /// <summary>Before a model job Halyard checks that Fairlead answers, and it can start Fairlead. Fakes only: nothing is launched.</summary>
    public class FairleadPreflightTests
    {
        private static LlmSettings Llm(string? url = null) { return new LlmSettings { FairleadUrl = url! }; }

        [Theory]
        [InlineData(null, "http://127.0.0.1:8776/v1/health")]
        [InlineData("http://127.0.0.1:8776/v1/chat/completions", "http://127.0.0.1:8776/v1/health")]
        [InlineData("http://localhost:9000/v1", "http://localhost:9000/v1/health")]
        public void TheHealthAddressIsOnTheChatAddressHostAndPort(string? chat, string expected)
        {
            Assert.Equal(expected, Fairlead.HealthUrl(chat!));
        }

        [Fact]
        public async Task AnyAnswerCountsAsRunningAndNoAnswerAsDown()
        {
            var answering = new FakeTransport().On("GET", "/v1/health", 200, "{\"ok\":true}");
            Assert.True(await new Fairlead(Llm(), answering, null).IsUpAsync(CancellationToken.None));
            Assert.Equal("http://127.0.0.1:8776/v1/health", answering.Calls[0].Url);
            Assert.False(answering.Calls[0].Headers.ContainsKey("Authorization"));   // the health check carries no key

            // A 404 or 401 is still Fairlead answering; the call itself reports key and route problems.
            Assert.True(await new Fairlead(Llm(), new FakeTransport(), null).IsUpAsync(CancellationToken.None));

            var refused = new RefusingTransport();
            var f = new Fairlead(Llm(), refused, null);
            Assert.False(await f.IsUpAsync(CancellationToken.None));
            var ex = await Assert.ThrowsAsync<FairleadOfflineException>(() => f.EnsureUpAsync(CancellationToken.None));
            Assert.Contains("Fairlead is not running at http://127.0.0.1:8776", ex.Message);
        }

        [Fact]
        public void TheFolderIsFoundByItsStartScript()
        {
            var root = Path.Combine(Path.GetTempPath(), "halyard-fl-" + Guid.NewGuid().ToString("N"));
            try
            {
                var halyard = Path.Combine(root, "Halyard");
                var fairlead = Path.Combine(root, "Fairlead");
                Directory.CreateDirectory(halyard);
                Directory.CreateDirectory(Path.Combine(fairlead, "scripts"));
                Assert.NotEqual(fairlead, Fairlead.Find(null!, halyard));   // a folder without the script does not count
                File.WriteAllText(Path.Combine(fairlead, "scripts", "Start-Fairlead.ps1"), "# test");
                Assert.Equal(fairlead, Fairlead.Find(null!, halyard));
                Assert.Equal(fairlead, Fairlead.Find(fairlead, Path.Combine(root, "elsewhere")));
                Assert.Null(Fairlead.Find(Path.Combine(root, "missing"), halyard));   // the setting wins, even when wrong
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Fact]
        public async Task StartRunsFairleadsOwnScriptWithoutABrowserOnlyWhenDown()
        {
            var folder = Path.Combine(Path.GetTempPath(), "halyard-fl-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "scripts"));
                File.WriteAllText(Path.Combine(folder, "scripts", "Start-Fairlead.ps1"), "# test");
                var launched = new List<ProcessStartInfo>();

                var down = new Fairlead(Llm(), new RefusingTransport(), folder) { Launch = i => { launched.Add(i); return true; } };
                var state = await down.StartAsync(CancellationToken.None);
                Assert.Equal("starting", state.State);
                Assert.Single(launched);
                Assert.EndsWith("powershell.exe", launched[0].FileName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(Path.Combine(folder, "scripts", "Start-Fairlead.ps1"), launched[0].Arguments);
                Assert.EndsWith("--no-browser", launched[0].Arguments);
                Assert.True(launched[0].CreateNoWindow);

                var up = new Fairlead(Llm(), new FakeTransport().On("GET", "/v1/health", 200, "{}"), folder) { Launch = i => { launched.Add(i); return true; } };
                Assert.Equal("ready", (await up.StartAsync(CancellationToken.None)).State);
                Assert.Single(launched);   // already running: nothing started

                var nowhere = new Fairlead(Llm(), new RefusingTransport(), null) { Launch = i => { launched.Add(i); return true; } };
                var missing = await nowhere.StartAsync(CancellationToken.None);
                Assert.Equal("missing", missing.State);
                Assert.Contains("HALYARD_FAIRLEAD_PATH", missing.Message);
                Assert.Single(launched);
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }
    }

    /// <summary>A model job asked for while Fairlead is down fails at once with FAIRLEAD_OFFLINE, before any job starts.</summary>
    [Collection("CredentialReader")]
    public sealed class FairleadPreflightHostTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-flh-" + Guid.NewGuid().ToString("N"));
        private readonly RefusingTransport _http = new RefusingTransport();
        private readonly FakeCredentialStore _key = new FakeCredentialStore("fl-halyard-client-key-0123456789");
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();

        public FairleadPreflightHostTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root, FairleadPath = Path.Combine(_root, "no-fairlead") };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null, Transport = _http });
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(20));
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(catalog, Items.Repo), null);
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        [Fact]
        public async Task ModelTestFailsOnceWithACodeThePageCanActOn()
        {
            var reply = await _client.PostAsync(_info.Url + "api/llm/test", new StringContent("{}"));
            Assert.Equal(503, (int)reply.StatusCode);
            var json = JObject.Parse(await reply.Content.ReadAsStringAsync());
            Assert.Equal("FAIRLEAD_OFFLINE", (string?)json["code"]);
            Assert.All(_http.Calls, c => Assert.EndsWith("/v1/health", c.Url));   // only the health check went out
        }

        [Fact]
        public async Task StatusSaysOfflineAndThatItCannotStartAMissingFairlead()
        {
            var json = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/fairlead"));
            Assert.Equal("offline", (string?)json["state"]);
            Assert.False((bool)json["canStart"]!);
            var start = JObject.Parse(await (await _client.PostAsync(_info.Url + "api/fairlead/start", new StringContent("{}"))).Content.ReadAsStringAsync());
            Assert.Equal("missing", (string?)start["state"]);
        }

        public void Dispose()
        {
            _host.Dispose();
            _client.Dispose();
            _key.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}
