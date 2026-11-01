using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Schema;
using Halyard.Core.Serialization;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>The ready goal with a model: draft and size steps, run against a scripted chat endpoint (no network).</summary>
    [Collection("CredentialReader")]
    public sealed class GoalModelTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-goalmodel-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly FakeTransport _http = new FakeTransport();
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private int _chatCalls;
        private readonly FakeCredentialStore _creds = new FakeCredentialStore("fl-halyard-client-key-0123456789");

        public GoalModelTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            _http.On("POST", "127.0.0.1:8776", call =>
            {
                _chatCalls++;
                var user = (string)JObject.Parse(call.JsonBody!)["messages"]![1]!["content"]!;
                string content;
                if (user.Contains("### id:"))
                {
                    var ids = Regex.Matches(user, @"### id: (\S+)").Cast<Match>().Select(m => m.Groups[1].Value);
                    content = new JArray(ids.Select(id => new JObject { ["id"] = id, ["points"] = 3, ["confidence"] = "high", ["reason"] = "r" })).ToString();
                }
                else content = new JObject { ["narrative"] = "", ["note"] = "nothing to add" }.ToString();
                return new HttpReply(200, new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = content } }) }.ToString(), new System.Collections.Generic.Dictionary<string, string>());
            });
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null, Transport = _http });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            _creds.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void SnapshotWithUnsizedStories()
        {
            var items = SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(40)), Items.Repo);
            foreach (var it in items.Where(i => i.Type == WorkItemType.Story))
            {
                var meta = it.Meta!.Clone();
                meta.Points = "TBD";
                it.Body = Descriptions.Rewrite(it.Body, meta);
                it.InvalidateMeta();
            }
            _host.Snapshots.Save(Items.Repo, items, null);
        }

        private async Task<JObject> Run(string body)
        {
            var reply = await _client.PostAsync(_info.Url + "api/goals/ready", new StringContent(body));
            var json = JObject.Parse(await reply.Content.ReadAsStringAsync());
            json["httpStatus"] = (int)reply.StatusCode;
            return json;
        }

        private static string[] Ids(JObject run) { return ((JArray)run["steps"]!).Select(s => (string)s["id"]!).ToArray(); }
        private static string Status(JObject run, string id) { return (string)((JArray)run["steps"]!).Single(s => (string)s["id"]! == id)["status"]!; }

        [Fact]
        public async Task WithoutUseModelDraftAndSizeAreSkippedAndNoModelIsCalled()
        {
            SnapshotWithUnsizedStories();
            var run = await Run("{}");
            Assert.Equal(new[] { "metadata", "parents", "draft", "size", "skills", "plan", "report" }, Ids(run));
            Assert.Equal("skipped", Status(run, "draft"));
            Assert.Equal("skipped", Status(run, "size"));
            Assert.Equal(0, _chatCalls);
        }

        [Fact]
        public async Task WithUseModelTheSizeStepProposesPointsAndLaterStepsWait()
        {
            SnapshotWithUnsizedStories();
            var run = await Run("{\"useModel\":true}");
            Assert.Equal(200, (int)run["httpStatus"]!);
            Assert.True(_chatCalls > 0);
            var steps = ((JArray)run["steps"]!).Select(s => (id: (string)s["id"]!, status: (string)s["status"]!, batch: (string?)s["batchId"])).ToList();
            var proposed = steps.Where(s => s.status == "proposed").ToList();
            Assert.True(proposed.Count == 1, "exactly one step stops the run: " + string.Join(",", steps.Select(s => s.id + "=" + s.status)));
            var stopAt = steps.FindIndex(s => s.status == "proposed");
            Assert.All(steps.Skip(stopAt + 1).Where(s => s.id != "report"), s => Assert.Equal("waiting", s.status));
            var batch = _host.Proposals.Load(proposed[0].batch!)!;
            Assert.All(batch.Proposals, p => Assert.Equal(ProposalDecision.Pending, p.Decision));
            if (proposed[0].id == "size") Assert.All(batch.Proposals, p => Assert.Equal("3", p.ProposedValue));
        }

        [Fact]
        public async Task AModelFailureStopsThatStepWithTheReasonAndTheRestWait()
        {
            SnapshotWithUnsizedStories();
            _http.Rules.Clear();
            _http.On("POST", "127.0.0.1:8776", 401, "{\"error\":{\"message\":\"bad key\"}}");
            var run = await Run("{\"useModel\":true}");
            Assert.Equal(200, (int)run["httpStatus"]!);
            var failed = ((JArray)run["steps"]!).FirstOrDefault(s => (string)s["status"]! == "failed");
            if (failed != null)
            {
                Assert.Contains("call failed", (string)failed["note"]!, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("failed", (string)run["next"]!);
                var after = ((JArray)run["steps"]!).SkipWhile(s => (string)s["status"]! != "failed").Skip(1).Where(s => (string)s["id"]! != "report");
                Assert.All(after, s => Assert.Equal("waiting", (string)s["status"]!));
            }
            else Assert.Contains(((JArray)run["steps"]!), s => (string)s["status"]! == "proposed"); // an earlier step stopped the run before the model was needed
        }

        [Fact]
        public async Task UseModelWithoutAKeyIsABadGatewayStyleErrorNotACrash()
        {
            CredentialReader.Reader = target => null;
            SnapshotWithUnsizedStories();
            var run = await Run("{\"useModel\":true}");
            Assert.NotEqual(500, (int)run["httpStatus"]!);
            Assert.True((int)run["httpStatus"]! >= 400);
        }
    }
}
