using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Planning;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Failure paths: a second operator, a refusing provider, files written the way Windows tools write them.</summary>
    public sealed class FailurePathTests : IDisposable
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitLab, "5678");
        private static readonly RepositoryRef Production = new RepositoryRef(ProviderKind.GitLab, "1234");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-failure-" + Guid.NewGuid().ToString("N"));
        private readonly FakeGitLabProject _gitlab = new FakeGitLabProject(5678, "platform/sandbox");
        private readonly HttpClient _client = new HttpClient();
        private HalyardHost? _host;
        private HostInfo? _info;

        public FailurePathTests() { Directory.CreateDirectory(_root); }

        public void Dispose()
        {
            _host?.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void StartHost()
        {
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitLab;
            settings.Provider.BaseUrl = "https://gitlab.example.com";
            settings.Provider.Token = "t";
            settings.Provider.SandboxRepository = "5678";
            settings.Provider.SourceRepository = "1234";
            settings.Provider.WriteDelayMs = 0;
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null, Transport = _gitlab.Http });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        [Fact]
        public async Task ARollbackWhileAnotherOperatorIsWritingIsRefusedWithWhoHoldsIt()
        {
            StartHost();
            var title = "STORY :: login";
            var iid = _gitlab.Add(title, "Applied text", "opened", "type::story");
            var run = new ApplyRun { Target = Sandbox.Key };
            run.Entries.Add(new ApplyJournalEntry { ItemNumber = iid, ItemKey = Sandbox.Key + ":" + iid, ItemCanonicalId = "STORY-login", ItemTitle = title, PreviousBody = "Earlier text", AppliedBody = "Applied text" });
            _host!.Journal.Save(run);
            // another operator's engine holds the lease (live process on this machine, different holder)
            var lease = new JObject { { "holder", "bo@other-pc:77" }, { "reason", "apply" }, { "host", Environment.MachineName }, { "pid", Process.GetCurrentProcess().Id }, { "acquiredAt", DateTimeOffset.UtcNow.ToString("o") }, { "expiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("o") } };
            File.WriteAllText(Path.Combine(_host.Settings.OutputDirectory, "write.lease"), lease.ToString());

            var reply = await _client.PostAsync(_info!.Url + "api/applies/" + run.Id + "/rollback", new StringContent("{}"));
            var body = await reply.Content.ReadAsStringAsync();

            Assert.Equal(409, (int)reply.StatusCode);
            Assert.Contains("bo@other-pc:77", body);
            Assert.Empty(_gitlab.Http.Writes());
            Assert.Equal("Applied text", _gitlab.BodyOf(iid));
            Assert.True(_host.Journal.Load(run.Id)!.CanRollBack);
        }

        [Fact]
        public async Task APreviewOfARollbackNeverNeedsTheLease()
        {
            StartHost();
            var iid = _gitlab.Add("STORY :: login", "Applied text", "opened", "type::story");
            var run = new ApplyRun { Target = Sandbox.Key };
            run.Entries.Add(new ApplyJournalEntry { ItemNumber = iid, ItemKey = Sandbox.Key + ":" + iid, ItemCanonicalId = "STORY-login", ItemTitle = "STORY :: login", PreviousBody = "Earlier text", AppliedBody = "Applied text" });
            _host!.Journal.Save(run);
            var lease = new JObject { { "holder", "bo@other-pc:77" }, { "reason", "apply" }, { "host", Environment.MachineName }, { "pid", Process.GetCurrentProcess().Id }, { "acquiredAt", DateTimeOffset.UtcNow.ToString("o") }, { "expiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("o") } };
            File.WriteAllText(Path.Combine(_host.Settings.OutputDirectory, "write.lease"), lease.ToString());
            var reply = await _client.PostAsync(_info!.Url + "api/applies/" + run.Id + "/rollback", new StringContent("{\"dryRun\":true}"));
            Assert.Equal(200, (int)reply.StatusCode);
        }

        [Fact]
        public async Task APromoteStopsAtTheFirstRefusalAndLeavesTheRestUntouched()
        {
            var prod = new FakeGitLabProject(1234, "platform/production");
            prod.Http.Rules.Insert(0, new FakeTransport.Rule { Method = "POST", UrlContains = "/issues", Status = 403, Body = "{\"message\":\"403 Forbidden\"}" });
            var provider = new GitLabProvider(prod.Http, "https://gitlab.example.com", "t", Production, 0);
            var policy = new WritePolicy { Mode = WriteMode.Production, AllowProduction = true, ProductionTarget = Production };
            policy.SandboxTargets.Add(Sandbox);
            var sandboxItems = Enumerable.Range(1, 3).Select(n =>
            {
                var meta = new HalyardMeta { Type = WorkItemType.Story, Id = "STORY-" + n };
                return new WorkItem(Sandbox, n) { Title = "STORY :: s" + n, Body = "Text.\n\n" + meta.ToFence() };
            }).ToList();
            var plan = Promoter.Plan(sandboxItems, new WorkItem[0], Sandbox, Production);

            var result = await Promoter.RunAsync(plan, plan.ConfirmPhrase, provider, new WriteGateway(policy), Production, null, CancellationToken.None);

            Assert.Equal(0, result.Created);
            Assert.Equal(1, result.Failed);
            Assert.Single(result.Rows);
            Assert.Equal(1, prod.Http.Calls.Count(c => c.Method == "POST" && c.Url.EndsWith("/issues")));
            Assert.Equal(0, prod.Count);
        }

        [Theory]
        [InlineData("utf8bom")]
        [InlineData("utf16")]
        public void AShellbackExportWrittenByWindowsToolsIsStillRead(string encoding)
        {
            var card = new JObject { ["format"] = "synapse-peer-card", ["id"] = "ann", ["name"] = "Ann Example", ["consent"] = true, ["skills"] = new JArray("sql-pipelines") }.ToString();
            var export = Path.Combine(_root, "export.json");
            File.WriteAllText(export, "[" + card + "]", encoding == "utf16" ? Encoding.Unicode : new UTF8Encoding(true));
            var live = Path.Combine(_root, "live");
            var result = ShellbackFeed.RefreshFromFile(export, live);
            Assert.Equal(1, result.Written);
            Assert.True(TeamConfig.Load(Path.Combine(_root, "none.json"), new[] { live }).FindPerson("Ann Example")!.Has("sql-pipelines"));
        }
    }
}
