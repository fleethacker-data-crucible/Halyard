using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Model;
using Halyard.Core.Serialization;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>The "get this backlog ready" goal: deterministic steps in order, stopping at the first one with proposals. Fakes only; nothing is written.</summary>
    public sealed class GoalTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-goal-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly HalyardHost _host;
        private readonly HostInfo _info;

        public GoalTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private void Snapshot(bool defects)
        {
            var options = SyntheticOptions.ForTotal(60);
            options.IncludeDefects = defects;
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(options), Items.Repo), null);
        }

        private async Task<JObject> Run()
        {
            var reply = await _client.PostAsync(_info.Url + "api/goals/ready", new StringContent("{}"));
            var json = JObject.Parse(await reply.Content.ReadAsStringAsync());
            json["httpStatus"] = (int)reply.StatusCode;
            return json;
        }

        [Fact]
        public async Task NeedsASnapshot()
        {
            var r = await Run();
            Assert.Equal(400, (int)r["httpStatus"]!);
        }

        [Fact]
        public async Task StepsRunInOrderAndStopAtTheFirstOneWithProposals()
        {
            Snapshot(defects: true);
            var run = await Run();
            var steps = ((JArray)run["steps"]!).Select(s => (id: (string)s["id"]!, status: (string)s["status"]!, batch: (string?)s["batchId"])).ToList();
            Assert.Equal(new[] { "metadata", "parents", "skills", "size", "plan", "report" }, steps.Select(s => s.id).ToArray());
            var firstProposed = steps.FindIndex(s => s.status == "proposed");
            Assert.True(firstProposed >= 0, "the defect catalogue gives something to propose");
            Assert.All(steps.Take(firstProposed), s => Assert.Equal("clean", s.status));
            Assert.All(steps.Skip(firstProposed + 1).Where(s => s.id != "size" && s.id != "report"), s => Assert.Equal("waiting", s.status));
            Assert.Equal("skipped", steps.Single(s => s.id == "size").status);
            Assert.Equal("done", steps.Single(s => s.id == "report").status);

            var batch = _host.Proposals.Load(steps[firstProposed].batch!)!;
            Assert.NotEmpty(batch.Proposals);
            Assert.All(batch.Proposals, p => Assert.Equal(Halyard.Core.Intelligence.ProposalDecision.Pending, p.Decision));
            Assert.Contains(steps[firstProposed].batch!, (string)run["next"]!);
            Assert.Equal(1, _host.Proposals.LoadAll().Count);
        }

        [Fact]
        public async Task OnlyOneBatchIsMadePerRunAndNothingIsApproved()
        {
            Snapshot(defects: true);
            await Run();
            await Run();
            var batches = _host.Proposals.LoadAll();
            Assert.Equal(2, batches.Count); // one per run
            Assert.All(batches, b => Assert.All(b.Proposals, p => Assert.NotEqual(Halyard.Core.Intelligence.ProposalDecision.Approved, p.Decision)));
        }

        [Fact]
        public async Task ARunIsRecordedAndListed()
        {
            Snapshot(defects: false);
            var run = await Run();
            Assert.True(File.Exists(Path.Combine(_host.GoalsDirectory, "goal-" + (string)run["id"]! + ".json")));
            var list = JArray.Parse(await _client.GetStringAsync(_info.Url + "api/goals"));
            Assert.Equal((string)run["id"]!, (string)list[0]["id"]!);
            Assert.Equal(6, ((JArray)list[0]["steps"]!).Count);
        }

        [Fact]
        public async Task WithNoProposalsAnywhereTheSummaryIsIncluded()
        {
            Snapshot(defects: false);
            var run = await Run();
            var steps = ((JArray)run["steps"]!).ToList();
            if (steps.All(s => (string)s["status"]! != "proposed"))
            {
                Assert.Contains("Every automatic step is clean", (string)run["next"]!);
                Assert.Contains("Features:", (string)steps.Single(s => (string)s["id"]! == "report")["note"]!);
            }
            else Assert.Contains("run this goal again", (string)run["next"]!);
        }
    }
}
