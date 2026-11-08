using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Planning;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>The crew (agentic product owner and scrum master), the scrum brief, the weekly watch and the speech relay. Fakes only.</summary>
    public sealed class CrewTests : IDisposable
    {
        private readonly string _root = SafePath.Under(Path.GetTempPath(), "halyard-crew-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly HalyardHost _host;
        private readonly HostInfo _info;

        public CrewTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.TalkForgePath = SafePath.Under(_root, "no-talkforge");
            // Not loopback, so model calls are never usable here, whatever key this machine has installed.
            settings.Llm.FairleadUrl = "http://fairlead.invalid/v1";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            TestDirs.TryDelete(_root);
        }

        private static List<WorkItem> Catalog(bool defects)
        {
            var options = SyntheticOptions.ForTotal(60);
            options.IncludeDefects = defects;
            return SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(options), Items.Repo);
        }

        private void Snapshot(bool defects) { _host.Snapshots.Save(Items.Repo, Catalog(defects), null); }

        private async Task<(int status, JObject json)> Post(string path, string body)
        {
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var reply = await _client.PostAsync(_info.Url + path, content))
                return ((int)reply.StatusCode, JObject.Parse(await reply.Content.ReadAsStringAsync()));
        }

        private sealed class ScriptedLlm : ILlmClient
        {
            private readonly Queue<string> _replies;
            public readonly List<IReadOnlyList<LlmMessage>> Seen = new List<IReadOnlyList<LlmMessage>>();
            public ScriptedLlm(params string[] replies) { _replies = new Queue<string>(replies); }
            public string Name { get { return "fairlead"; } }
            public string Model { get { return "scripted"; } }
            public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation)
            {
                Seen.Add(messages.ToList());
                return Task.FromResult(new LlmResponse { Text = _replies.Count > 0 ? _replies.Dequeue() : "{\"answer\":\"done\"}" });
            }
            public Task<LlmResponse> TestAsync(CancellationToken cancellation) { return Task.FromResult(new LlmResponse { Text = "OK" }); }
        }

        // ---- scrum brief ----

        [Fact]
        public void BriefCoversTheSprintAndNamesNextActions()
        {
            var items = Catalog(defects: true);
            var team = TeamConfig.Default();
            var brief = ScrumMaster.Build(items, Review.Run(items, Schema.Taxonomy.Default()), team, DateTimeOffset.UtcNow);
            Assert.False(string.IsNullOrEmpty(brief.CurrentSprint));
            Assert.Equal(brief.DaysElapsed + brief.DaysLeft, (int)Math.Round((brief.SprintEnd - brief.SprintStart).TotalDays));
            Assert.True(brief.PointsDone <= brief.PointsPlanned);
            Assert.NotEmpty(brief.Actions);
            Assert.Contains(brief.Actions, a => a.Command == "halyard goal ready");   // defects make items fail their definition of ready
            Assert.NotEmpty(brief.Refinement);
            Assert.Equal(new[] { "standup", "review", "retro", "planning" }, brief.Ceremonies.Select(c => c.Id).Where(id => id != "refinement").Distinct().OrderBy(id => Array.IndexOf(new[] { "standup", "review", "retro", "planning" }, id)).ToArray());
            Assert.True(brief.Ceremonies.Zip(brief.Ceremonies.Skip(1), (a, b) => a.At <= b.At).All(x => x), "ceremonies are in date order");
            Assert.DoesNotContain("|", brief.Spoken);
            Assert.All(brief.Actions.Select(a => a.Command), c => Assert.Contains(CommandRegistry.All, x => x.Name == c));
        }

        [Fact]
        public void StaleWorkIsRaisedAtStandup()
        {
            var items = Catalog(defects: false);
            var team = TeamConfig.Default();
            var now = DateTimeOffset.UtcNow;
            var current = team.Sprint.Name(team.Sprint.Current(now));
            var story = items.First(i => i.Type == WorkItemType.Story && i.State == WorkItemState.Open && !i.Labels.Any(l => l.StartsWith("blocked")));
            var meta = story.Meta!.Clone();
            meta.Sprint = current;
            story.Body = Schema.Descriptions.Rewrite(story.Body, meta);
            story.InvalidateMeta();
            story.UpdatedAt = now.AddDays(-9);
            var brief = ScrumMaster.Build(items, null, team, now);
            Assert.Contains(brief.Stale, s => s.Key == story.Key && s.AgeDays >= 9);
        }

        [Fact]
        public async Task BriefRouteNeedsASnapshotThenAnswers()
        {
            var empty = await _client.GetAsync(_info.Url + "api/scrum/brief");
            Assert.Equal(400, (int)empty.StatusCode);
            Snapshot(defects: true);
            var ok = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/scrum/brief"));
            Assert.False(string.IsNullOrEmpty((string)ok["spoken"]));
            Assert.True(((JArray)ok["actions"]!).Count > 0);
        }

        // ---- the crew agent ----

        [Fact]
        public void ToolsNeverIncludeAWritingOrAsyncCommand()
        {
            var tools = CrewAgent.Tools(CommandRegistry.All).ToList();
            Assert.NotEmpty(tools);
            Assert.All(tools, t => Assert.False(t.Writes, t.Name));
            Assert.All(tools, t => Assert.False(t.Async, t.Name));
            foreach (var name in new[] { "halyard proposal apply", "halyard proposal decide", "halyard apply rollback", "halyard promote run", "halyard sandbox seed", "halyard config set", "halyard agent ask" })
                Assert.DoesNotContain(tools, t => t.Name == name);
            Assert.Contains(tools, t => t.Name == "halyard goal ready");
            Assert.Contains(tools, t => t.Name == "halyard scrum brief");
        }

        [Fact]
        public async Task CrewRefusesWritesBeforeTheyReachTheRouter()
        {
            var dispatched = new List<string>();
            var llm = new ScriptedLlm(
                "{\"call\":{\"command\":\"halyard proposal apply-all\",\"body\":{}}}",
                "{\"call\":{\"command\":\"halyard proposal decide\",\"params\":{\"batchId\":\"x\"}}}",
                "{\"answer\":\"I cannot apply; review the batch.\"}");
            var crew = new CrewAgent(llm, (m, p, q, b) => { dispatched.Add(m + " " + p); return Task.FromResult(new CrewCallResult { Status = 200, Body = "{}" }); }, CommandRegistry.All);
            var turn = await crew.AskAsync("Apply everything", null, null, null, CancellationToken.None);
            Assert.Empty(dispatched);
            Assert.All(turn.Calls, c => Assert.Equal("refused", c.Outcome));
            Assert.Equal("I cannot apply; review the batch.", turn.Answer);
            Assert.Contains(llm.Seen[1], m => m.Content.StartsWith("REFUSED"));
        }

        [Fact]
        public async Task CrewCallsCommandsFillsParamsAndCollectsBatches()
        {
            var dispatched = new List<string>();
            var llm = new ScriptedLlm(
                "Sure. {\"call\":{\"command\":\"halyard item get\",\"params\":{\"number\":\"12\"}}}",
                "{\"call\":{\"command\":\"halyard review show\",\"query\":{\"label\":\"team a\"}}}",
                "{\"call\":{\"command\":\"halyard goal ready\",\"body\":{}}}",
                "{\"answer\":\"Review batch b-1.\"}");
            CrewDispatch dispatch = (m, p, q, b) =>
            {
                dispatched.Add(m + " " + p + (string.IsNullOrEmpty(q) ? "" : "?" + q));
                var body = p == "/api/goals/ready" ? "{\"steps\":[{\"id\":\"metadata\",\"batchId\":\"b-1\"}]}" : "{\"ok\":true}";
                return Task.FromResult(new CrewCallResult { Status = 200, Body = body });
            };
            var turn = await new CrewAgent(llm, dispatch, CommandRegistry.All).AskAsync("Get the backlog ready", null, null, "repo", CancellationToken.None);
            Assert.Equal(new[] { "GET /api/items/12", "GET /api/review?label=team%20a", "POST /api/goals/ready" }, dispatched.ToArray());
            Assert.Equal(new[] { "b-1" }, turn.Batches.ToArray());
            Assert.Equal("Review batch b-1.", turn.Answer);
            Assert.Equal("fairlead", turn.Source);
        }

        [Fact]
        public async Task CrewStopsAfterTheStepLimit()
        {
            var replies = Enumerable.Repeat("{\"call\":{\"command\":\"halyard status show\"}}", CrewAgent.MaxSteps + 3).ToArray();
            var calls = 0;
            var turn = await new CrewAgent(new ScriptedLlm(replies), (m, p, q, b) => { calls++; return Task.FromResult(new CrewCallResult { Status = 200, Body = "{}" }); }, CommandRegistry.All)
                .AskAsync("loop", null, null, null, CancellationToken.None);
            Assert.Equal(CrewAgent.MaxSteps, calls);
            Assert.Contains("ran out of steps", turn.Answer);
        }

        [Fact]
        public void ParseTakesPlainTextAsAnAnswer()
        {
            Assert.Equal("Just words.", CrewAgent.Parse("Just words.").Answer);
            Assert.Equal("hi", CrewAgent.Parse("```json\n{\"answer\":\"hi\"}\n```").Answer);
            Assert.NotNull(CrewAgent.Parse("{\"call\":{\"command\":\"halyard status show\"}}").Call);
            Assert.Equal(new[] { "20261108-1200-ab12" }, CrewAgent.BatchIds("{\"id\":\"20261108-1200-ab12\",\"proposals\":[{}]}").ToArray());
        }

        [Fact]
        public async Task AskWithoutAModelAnswersFromTheBrief()
        {
            Snapshot(defects: true);
            var (status, json) = await Post("api/agent/ask", "{\"message\":\"Any blockers?\"}");
            Assert.Equal(200, status);
            Assert.Equal("brief", (string)json["source"]);
            Assert.Contains("No model is configured", (string)json["answer"]);
        }

        [Fact]
        public async Task DispatchRunsARouteWithTheSessionToken()
        {
            Snapshot(defects: false);
            var r = await _host.DispatchAsync("GET", "/api/scrum/brief", null, null);
            Assert.Equal(200, r.Status);
            Assert.Contains("\"spoken\"", r.Body);
        }

        // ---- weekly watch ----

        [Fact]
        public async Task WatchProposesAndWritesItsReportButNothingToTheSandbox()
        {
            Snapshot(defects: true);
            var (status, json) = await Post("api/agent/watch", "{}");
            Assert.Equal(200, status);
            var md = (string)json["markdownPath"]!;
            Assert.True(File.Exists(md));
            var text = File.ReadAllText(md);
            Assert.Contains("# Weekly watch", text);
            Assert.Contains("## Proposed for review", text);
            Assert.Contains(((JArray)json["goal"]!["steps"]!), s => (string)s["status"] == "proposed");
            Assert.False(Directory.Exists(SafePath.Under(_root, "output", "applied")), "a watch never applies");
            var list = JArray.Parse(await _client.GetStringAsync(_info.Url + "api/agent/watches"));
            Assert.Single(list);
        }

        // ---- voice ----

        [Fact]
        public async Task VoiceStatusSaysWhatIsMissing()
        {
            var json = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/voice/status"));
            Assert.False((bool)json["dictation"]!["ready"]!);
            Assert.Contains("TalkForge was not found", (string)json["dictation"]!["message"]);
        }

        [Fact]
        public async Task SttServesOnlyListedFiles()
        {
            var tf = SafePath.Under(_root, "no-talkforge", "assets", "stt");
            Directory.CreateDirectory(tf);
            File.WriteAllText(SafePath.Under(tf, "transformers.min.js"), "export const x = 1;");
            File.WriteAllText(SafePath.Under(tf, "secret.txt"), "no");
            using (var http = new HttpClient())   // no session header: a worker fetches these files
            {
                var ok = await http.GetAsync(_info.Url + "stt/transformers.min.js");
                Assert.Equal(200, (int)ok.StatusCode);
                Assert.StartsWith("text/javascript", ok.Content.Headers.ContentType!.ToString());
                var refused = await http.GetAsync(_info.Url + "stt/secret.txt");
                Assert.Equal(404, (int)refused.StatusCode);
                var traversal = await http.GetAsync(_info.Url + "stt/..%2F..%2Fconfig%2Fhalyard.env");
                Assert.Equal(404, (int)traversal.StatusCode);
            }
        }
    }
}
