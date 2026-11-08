using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    public class PromoterTests
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitLab, "5678");
        private static readonly RepositoryRef Production = new RepositoryRef(ProviderKind.GitLab, "1234");

        private static string BodyWithId(string id, string text = "Narrative.")
        {
            var meta = new HalyardMeta();
            meta.Type = WorkItemType.Story;
            meta.Id = id;
            return text + "\n\n" + meta.ToFence();
        }

        private static WorkItem Item(RepositoryRef repo, long number, string id, string title, string text = "Narrative.", params string[] labels)
        {
            var w = new WorkItem(repo, number) { Title = title, Body = id == null ? text : BodyWithId(id, text) };
            w.Labels.AddRange(labels);
            return w;
        }

        [Fact]
        public void PlansCreatesUpdatesAndUnchangedByMetaId()
        {
            var sandbox = new[]
            {
                Item(Sandbox, 1, "STORY-new", "STORY :: new", "Fresh.", "type::story", "halyard-run::r1", "refine::sandbox-review"),
                Item(Sandbox, 2, "STORY-edit", "STORY :: edit", "Improved."),
                Item(Sandbox, 3, "STORY-same", "STORY :: same", "Same."),
                Item(Sandbox, 4, "STORY-fake", "STORY :: fake", "x", "halyard-synthetic"),
                Item(Sandbox, 5, null, "STORY :: no meta"),
            };
            var production = new[]
            {
                Item(Production, 90, "STORY-edit", "STORY :: edit", "Old."),
                Item(Production, 91, "STORY-same", "STORY :: same", "Same."),
                Item(Sandbox, 92, "STORY-new", "elsewhere"), // a different repository never matches
            };
            var plan = Promoter.Plan(sandbox, production, Sandbox, Production);
            Assert.Equal(1, plan.Creates);
            Assert.Equal(1, plan.Updates);
            Assert.Equal(1, plan.Unchanged);
            Assert.Equal(2, plan.Warnings.Count);
            Assert.Contains(plan.Warnings, w => w.Contains("synthetic"));
            Assert.Contains(plan.Warnings, w => w.Contains("no halyard-meta id"));
            var create = plan.Rows.Single(r => r.Action == "create");
            Assert.Equal(new[] { "type::story" }, create.Labels.ToArray()); // run and review-queue labels stay in the sandbox
            var update = plan.Rows.Single(r => r.Action == "update");
            Assert.Equal(90, update.TargetNumber);
            Assert.Contains("Old.", update.CurrentBody);
            Assert.Contains("Improved.", update.NewBody);
            Assert.Equal("PROMOTE 2 ITEMS TO gitlab:1234", plan.ConfirmPhrase);
        }

        // ---- running against a fake production project ----

        private static WriteGateway Gateway(WriteMode mode, bool allow)
        {
            var policy = new WritePolicy { Mode = mode, AllowProduction = allow, ProductionTarget = Production };
            policy.SandboxTargets.Add(Sandbox);
            return new WriteGateway(policy);
        }

        private static (GitLabProvider provider, FakeGitLabProject prod, PromotionPlan plan, long editNumber) Scenario()
        {
            var prod = new FakeGitLabProject(1234, "platform/production");
            var editNumber = prod.Add("STORY :: edit", BodyWithId("STORY-edit", "Old."), "opened", "type::story");
            var provider = new GitLabProvider(prod.Http, "https://gitlab.example.com", "t", Production, 0);
            var plan = Promoter.Plan(new[]
            {
                Item(Sandbox, 1, "STORY-new", "STORY :: new", "Fresh.", "type::story", "halyard-run::r1"),
                Item(Sandbox, 2, "STORY-edit", "STORY :: edit", "Improved.", "type::story"),
            }, new[] { Item(Production, editNumber, "STORY-edit", "STORY :: edit", "Old.") }, Sandbox, Production);
            return (provider, prod, plan, editNumber);
        }

        [Fact]
        public async Task PromotesThroughTheFullGateWhenConfirmed()
        {
            var (provider, prod, plan, editNumber) = Scenario();
            var gateway = Gateway(WriteMode.Production, true);
            var result = await Promoter.RunAsync(plan, plan.ConfirmPhrase, provider, gateway, Production, null, CancellationToken.None);

            Assert.Equal(1, result.Created);
            Assert.Equal(1, result.Updated);
            Assert.Equal(0, result.Failed);
            Assert.Equal(2, prod.Count);
            Assert.Contains("Improved.", prod.BodyOf(editNumber));
            var created = result.Rows.Single(r => r.Action == "create");
            Assert.Contains("Fresh.", prod.BodyOf(created.TargetNumber!.Value));
            Assert.Contains("halyard", prod.LabelsOf(created.TargetNumber.Value));
            Assert.DoesNotContain("halyard-run::r1", prod.LabelsOf(created.TargetNumber.Value));
            Assert.All(gateway.Audit, a => Assert.Equal(Production.Key, a.Target));
            Assert.Equal(2, gateway.Audit.Count);
            Assert.StartsWith("promote-", result.RunId);
        }

        [Fact]
        public async Task WrongConfirmationWritesNothing()
        {
            var (provider, prod, plan, _) = Scenario();
            var before = prod.Http.Writes().Count();
            await Assert.ThrowsAsync<WriteBlockedException>(() => Promoter.RunAsync(plan, "yes", provider, Gateway(WriteMode.Production, true), Production, null, CancellationToken.None));
            Assert.Equal(before, prod.Http.Writes().Count());
        }

        [Theory]
        [InlineData(WriteMode.Sandbox, true)]
        [InlineData(WriteMode.Production, false)]
        public async Task TheProductionGateRefusesWithoutProductionModeAndTheEnvironmentFlag(WriteMode mode, bool allow)
        {
            var (provider, prod, plan, _) = Scenario();
            await Assert.ThrowsAsync<WriteBlockedException>(() => Promoter.RunAsync(plan, plan.ConfirmPhrase, provider, Gateway(mode, allow), Production, null, CancellationToken.None));
            Assert.Empty(prod.Http.Writes());
        }

        [Fact]
        public async Task APlanForAnotherTargetIsRefused()
        {
            var (provider, prod, plan, _) = Scenario();
            plan.Target = "gitlab:9999";
            await Assert.ThrowsAsync<WriteBlockedException>(() => Promoter.RunAsync(plan, plan.ConfirmPhrase, provider, Gateway(WriteMode.Production, true), Production, null, CancellationToken.None));
            Assert.Empty(prod.Http.Writes());
        }

        [Fact]
        public async Task AnItemChangedInProductionSinceThePreviewIsNotOverwritten()
        {
            var (provider, prod, plan, editNumber) = Scenario();
            await provider.OpenWriter(Production).UpdateAsync(editNumber, new WorkItemPatch { Body = BodyWithId("STORY-edit", "Someone edited this.") }, CancellationToken.None);
            var result = await Promoter.RunAsync(plan, plan.ConfirmPhrase, provider, Gateway(WriteMode.Production, true), Production, null, CancellationToken.None);
            Assert.Equal(1, result.Created);
            Assert.Equal(0, result.Updated);
            Assert.Equal(1, result.Failed);
            Assert.Contains("changed in production", Assert.Single(result.Messages));
            Assert.Contains("Someone edited this.", prod.BodyOf(editNumber));
        }

        [Fact]
        public async Task NothingToPromoteIsNotAnError()
        {
            var (provider, prod, _, _) = Scenario();
            var plan = new PromotionPlan { Source = Sandbox.Key, Target = Production.Key };
            var result = await Promoter.RunAsync(plan, plan.ConfirmPhrase, provider, Gateway(WriteMode.Production, true), Production, null, CancellationToken.None);
            Assert.Equal("Nothing to promote.", Assert.Single(result.Messages));
            Assert.Empty(prod.Http.Writes());
        }
    }

    public sealed class PromoteRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-promote-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new HttpClient();
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly FakeGitLabProject _prod = new FakeGitLabProject(1234, "platform/production");

        public PromoteRouteTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitLab;
            settings.Provider.BaseUrl = "https://gitlab.example.com";
            settings.Provider.Token = "t";
            settings.Provider.SourceRepository = "1234";
            settings.Provider.SandboxRepository = "5678";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null, Transport = _prod.Http });
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
        public async Task PromoteIsOffByDefaultAndSaysHowToTurnItOn()
        {
            var status = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/promote/status"));
            Assert.False((bool)status["enabled"]!);
            Assert.Equal("sandbox", (string)status["mode"]!);
            Assert.Contains("HALYARD_ALLOW_PRODUCTION=1", (string)status["reason"]!);
        }

        [Fact]
        public async Task RunIsRefusedInSandboxModeWithoutAnyWrite()
        {
            var reply = await _client.PostAsync(_info.Url + "api/promote/run", new StringContent("{\"confirmText\":\"PROMOTE 0 ITEMS TO gitlab:1234\"}"));
            Assert.Equal(403, (int)reply.StatusCode);
            Assert.Contains("PROMOTE BLOCKED", await reply.Content.ReadAsStringAsync());
            Assert.Empty(_prod.Http.Writes());
        }
    }
}
