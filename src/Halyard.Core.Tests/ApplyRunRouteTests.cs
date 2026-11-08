using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>
    /// The whole host over HTTP with a fake GitLab project as its transport: apply a batch, list the apply run, preview and
    /// perform the rollback. Nothing leaves the process.
    /// </summary>
    public sealed class ApplyRunRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-applyroutes-" + Guid.NewGuid().ToString("N"));
        private readonly FakeGitLabProject _gitlab = new FakeGitLabProject(5678, "platform/sandbox");
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitLab, "5678");

        public ApplyRunRouteTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitLab;
            settings.Provider.BaseUrl = "https://gitlab.example.com";
            settings.Provider.Token = "glpat-test";
            settings.Provider.SandboxRepository = "5678";
            settings.Provider.SourceRepository = "1234";
            settings.Provider.WriteDelayMs = 0;
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null, Transport = _gitlab.Http });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private async Task<JObject> Post(string path, string body = "{}")
        {
            var reply = await _client.PostAsync(_info.Url + path, new StringContent(body));
            var text = await reply.Content.ReadAsStringAsync();
            var json = JObject.Parse(text);
            json["httpStatus"] = (int)reply.StatusCode;
            return json;
        }

        private async Task<JObject> WaitJob(JObject accepted)
        {
            var id = (string)accepted["job"]!["id"]!;
            for (var i = 0; i < 200; i++)
            {
                var job = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/jobs/" + id));
                var state = (string)job["state"]!;
                if (state == "succeeded" || state == "failed" || state == "cancelled") return job;
                await Task.Delay(25);
            }
            throw new TimeoutException("job did not finish");
        }

        private (long iid, ProposalBatch batch) ApprovedPointsBatch(string body = "Original text.")
        {
            var title = "STORY :: login";
            var iid = _gitlab.Add(title, body, "opened", "type::story");
            var batch = new ProposalBatch { Kind = ProposalKind.StoryPoints, Target = Sandbox.Key };
            batch.Proposals.Add(new Proposal { BatchId = batch.Id, Kind = ProposalKind.StoryPoints, ItemKey = Sandbox.Key + ":" + iid, ItemNumber = iid, ItemCanonicalId = "STORY-login", ItemTitle = title, ProposedValue = "5", Decision = ProposalDecision.Approved });
            _host.Proposals.Save(batch);
            return (iid, batch);
        }

        [Fact]
        public async Task NothingToUndoIsAClearError()
        {
            Assert.Equal("[]", (await _client.GetStringAsync(_info.Url + "api/applies")).Trim());
            var reply = await Post("api/applies/rollback-last");
            Assert.Equal(400, (int)reply["httpStatus"]!);
            Assert.Contains("no applied change", ((string)reply["error"]!).ToLowerInvariant());
            Assert.Equal(404, (int)(await _client.GetAsync(_info.Url + "api/applies/nope")).StatusCode);
            Assert.Equal(404, (int)(await Post("api/applies/nope/rollback"))["httpStatus"]!);
        }

        [Fact]
        public async Task ApplyThenPreviewThenUndoTheLastApply()
        {
            var (iid, batch) = ApprovedPointsBatch();
            var applied = await WaitJob(await Post("api/proposals/" + batch.Id + "/apply"));
            Assert.Equal("succeeded", (string)applied["state"]!);
            var runId = (string)applied["result"]!["applyRunId"]!;
            Assert.False(string.IsNullOrEmpty(runId));
            var written = _gitlab.BodyOf(iid);
            Assert.NotEqual("Original text.", written);

            var list = JArray.Parse(await _client.GetStringAsync(_info.Url + "api/applies"));
            var summary = Assert.Single(list);
            Assert.Equal(runId, (string)summary["id"]!);
            Assert.Equal(1, (int)summary["pending"]!);
            Assert.True((bool)summary["canRollBack"]!);
            var detail = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/applies/" + runId));
            Assert.Equal("Original text.", (string)detail["entries"]![0]!["previousBody"]!);

            var preview = await Post("api/applies/rollback-last", "{\"dryRun\":true}");
            Assert.Equal(200, (int)preview["httpStatus"]!);
            Assert.Equal(1, (int)preview["preview"]!["restored"]!);
            Assert.True((bool)preview["preview"]!["dryRun"]!);
            Assert.Equal(written, _gitlab.BodyOf(iid));

            var job = await WaitJob(await Post("api/applies/rollback-last"));
            Assert.Equal("succeeded", (string)job["state"]!);
            Assert.Equal(1, (int)job["result"]!["restored"]!);
            Assert.Equal("Original text.", _gitlab.BodyOf(iid));
            Assert.False((bool)JArray.Parse(await _client.GetStringAsync(_info.Url + "api/applies"))[0]!["canRollBack"]!);

            var again = await Post("api/applies/" + runId + "/rollback");
            Assert.Equal(400, (int)again["httpStatus"]!);
            Assert.Contains("already been undone", (string)again["error"]!);
            Assert.Equal(ProposalDecision.Pending, _host.Proposals.Load(batch.Id)!.Proposals.Single().Decision);
        }

        [Fact]
        public async Task AnEditedItemIsNotOverwrittenWithoutForce()
        {
            var (iid, batch) = ApprovedPointsBatch();
            var applied = await WaitJob(await Post("api/proposals/" + batch.Id + "/apply"));
            var runId = (string)applied["result"]!["applyRunId"]!;
            await _host.GetProvider().OpenWriter(Sandbox).UpdateAsync(iid, new WorkItemPatch { Body = "edited by a person" }, System.Threading.CancellationToken.None);

            var job = await WaitJob(await Post("api/applies/" + runId + "/rollback"));
            Assert.Equal(1, (int)job["result"]!["conflicts"]!);
            Assert.Equal("edited by a person", _gitlab.BodyOf(iid));

            var forced = await WaitJob(await Post("api/applies/" + runId + "/rollback", "{\"force\":true}"));
            Assert.Equal(1, (int)forced["result"]!["restored"]!);
            Assert.Equal("Original text.", _gitlab.BodyOf(iid));
        }

        [Fact]
        public async Task ApplyAllMakesOneRunThatUndoesEveryBatch()
        {
            var (a, batchA) = ApprovedPointsBatch("Text A.");
            var (b, batchB) = ApprovedPointsBatch("Text B.");
            var job = await WaitJob(await Post("api/proposals/apply-all"));
            Assert.Equal(2, (int)job["result"]!["applied"]!);
            var run = Assert.Single(JArray.Parse(await _client.GetStringAsync(_info.Url + "api/applies")));
            Assert.Equal(2, (int)run["entries"]!);
            Assert.Equal(2, ((JArray)run["batchIds"]!).Count);

            var undo = await WaitJob(await Post("api/applies/rollback-last"));
            Assert.Equal(2, (int)undo["result"]!["restored"]!);
            Assert.Equal("Text A.", _gitlab.BodyOf(a));
            Assert.Equal("Text B.", _gitlab.BodyOf(b));
            Assert.All(new[] { batchA.Id, batchB.Id }, id => Assert.Equal(ProposalDecision.Pending, _host.Proposals.Load(id)!.Proposals.Single().Decision));
        }

        [Fact]
        public async Task ARunFromAnotherTargetCannotBeUndoneHere()
        {
            var run = new ApplyRun { Target = "gitlab:1234" };
            run.Entries.Add(new ApplyJournalEntry { ItemNumber = 1, ItemKey = "gitlab:1234:1", PreviousBody = "a", AppliedBody = "b" });
            _host.Journal.Save(run);
            var reply = await Post("api/applies/" + run.Id + "/rollback");
            Assert.Equal(403, (int)reply["httpStatus"]!);
            Assert.Empty(_gitlab.Http.Writes());
        }
    }
}
