using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Synthetic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Halyard.Core.Tests
{
    public class WritePolicyTests
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitLab, "5678");
        private static readonly RepositoryRef Source = new RepositoryRef(ProviderKind.GitLab, "1234");

        private static WritePolicy SandboxPolicy()
        {
            var p = new WritePolicy { Mode = WriteMode.Sandbox };
            p.SandboxTargets.Add(Sandbox);
            p.ProtectedTargets.Add(Source);
            return p;
        }

        [Fact]
        public void SandboxModeAllowsOnlySandbox()
        {
            var p = SandboxPolicy();
            p.Assert(Sandbox, new WriteIntent("t", "r"));
            var ex = Assert.Throws<WriteBlockedException>(() => p.Assert(Source, new WriteIntent("t", "r")));
            Assert.Contains("1234", ex.Message);
            Assert.Throws<WriteBlockedException>(() => p.Assert(new RepositoryRef(ProviderKind.GitLab, "777"), new WriteIntent("t", "r")));
        }

        [Fact]
        public void ProductionNeedsAllThreeSwitches()
        {
            var p = new WritePolicy { Mode = WriteMode.Production, ProductionTarget = Source };
            p.SandboxTargets.Add(Sandbox);
            Assert.Throws<WriteBlockedException>(() => p.Assert(Source, new WriteIntent("t", "r")));                       // env flag missing
            p.AllowProduction = true;
            Assert.Throws<WriteBlockedException>(() => p.Assert(Source, new WriteIntent("t", "r")));                       // intent missing
            p.Assert(Source, new WriteIntent("t", "r") { AllowProduction = true });                                           // all three present
            Assert.Throws<WriteBlockedException>(() => p.Assert(new RepositoryRef(ProviderKind.GitLab, "1"), new WriteIntent("t", "r") { AllowProduction = true }));
            p.Assert(Sandbox, new WriteIntent("t", "r"));                                                                  // sandbox always fine
        }

        [Fact]
        public void NoSandboxConfiguredBlocksEverything()
        {
            var p = new WritePolicy();
            Assert.Throws<WriteBlockedException>(() => p.Assert(Sandbox, new WriteIntent("t", "r")));
        }

        [Fact]
        public void RepositoryRefEqualityIgnoresCase()
        {
            Assert.Equal(new RepositoryRef(ProviderKind.GitHub, "Owner/Repo"), new RepositoryRef(ProviderKind.GitHub, "owner/repo"));
            Assert.NotEqual(new RepositoryRef(ProviderKind.GitHub, "a/b"), new RepositoryRef(ProviderKind.GitLab, "a/b"));
        }
    }

    public class WriteGatewayTests
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitHub, "fleethacker-data-crucible/Halyard-Ballast");
        private static readonly RepositoryRef Other = new RepositoryRef(ProviderKind.GitHub, "sun-of-anton/Halyard");

        private static (GitHubProvider provider, FakeTransport http) Provider()
        {
            var http = new FakeTransport();
            http.On("POST", "/issues", 201, Issue(42, "created", new[] { "halyard" }));
            http.On("PATCH", "/issues/", 200, Issue(42, "updated", new[] { "halyard" }));
            http.On("POST", "/graphql", 200, "{\"data\":{\"deleteIssue\":{\"clientMutationId\":null}}}");
            http.On("POST", "/labels", 201, "{}");
            return (new GitHubProvider(http, "https://api.github.com", "tok", Sandbox, 0), http);
        }

        private static string Issue(long number, string title, string[] labels)
        {
            return new JObject
            {
                ["number"] = number, ["id"] = 1000 + number, ["node_id"] = "I_" + number, ["title"] = title, ["body"] = "", ["state"] = "open",
                ["html_url"] = "https://github.com/fleethacker-data-crucible/Halyard-Ballast/issues/" + number,
                ["labels"] = new JArray(labels.Select(l => new JObject { ["name"] = l })), ["created_at"] = "2026-10-24T10:00:00Z", ["updated_at"] = "2026-10-24T10:00:00Z",
            }.ToString();
        }

        private static WriteGateway Gateway()
        {
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Sandbox);
            policy.ProtectedTargets.Add(Other);
            return new WriteGateway(policy);
        }

        [Fact]
        public async Task CreateStampsManagedLabelAndAudits()
        {
            var (provider, http) = Provider();
            var gateway = Gateway();
            var writer = gateway.Open(provider, Sandbox, new WriteIntent("test", "because", "run1"));
            var created = await writer.CreateAsync(new WorkItemDraft { Title = "x", Labels = new List<string> { "type::story" } }, CancellationToken.None);
            Assert.Equal(42, created.Number);
            var sent = JObject.Parse(http.Writes().Single(c => c.Url.EndsWith("/issues")).JsonBody!);
            Assert.Contains("halyard", sent["labels"]!.Select(l => (string)l!));
            var entry = Assert.Single(gateway.Audit);
            Assert.Equal("create", entry.Operation);
            Assert.True(entry.Succeeded);
            Assert.Equal("run1", entry.RunId);
        }

        [Fact]
        public void OpeningANonSandboxTargetThrowsBeforeAnyCall()
        {
            var (provider, http) = Provider();
            Assert.Throws<WriteBlockedException>(() => Gateway().Open(provider, Other, new WriteIntent("test", "r")));
            Assert.Empty(http.Calls);
        }

        [Fact]
        public async Task DeleteRequiresSyntheticLabelOnTheLiveItemUnlessWipe()
        {
            var gh = new FakeGitHubRepo("fleethacker-data-crucible/Halyard-Ballast");
            gh.Add(5, "someone's real issue", "halyard");
            gh.Add(6, "synthetic", Labels.Synthetic);
            var provider = new GitHubProvider(gh.Http, "https://api.github.com", "tok", Sandbox, 0);
            var gateway = Gateway();
            var writer = gateway.Open(provider, Sandbox, new WriteIntent("teardown", "r"));

            // The caller's copy claims "synthetic", but the live issue is not: the live labels decide.
            var forged = new WorkItem(Sandbox, 5) { Title = "forged" };
            forged.Labels.Add(Labels.Synthetic);
            await Assert.ThrowsAsync<WriteBlockedException>(() => writer.DeleteAsync(forged, CancellationToken.None));
            Assert.True(gh.Exists(5));

            Assert.True(await writer.DeleteAsync(new WorkItem(Sandbox, 6), CancellationToken.None));
            Assert.False(gh.Exists(6));
            Assert.False(await writer.DeleteAsync(new WorkItem(Sandbox, 6), CancellationToken.None)); // already gone

            var wipe = gateway.Open(provider, Sandbox, new WriteIntent("wipe", "r") { AllowDeleteOfAnyItem = true });
            Assert.True(await wipe.DeleteAsync(new WorkItem(Sandbox, 5), CancellationToken.None));
            Assert.False(gh.Exists(5));
        }

        [Fact]
        public async Task GitHubDeleteRefusesANodeFromAnotherRepository()
        {
            var gh = new FakeGitHubRepo("fleethacker-data-crucible/Halyard-Ballast");
            gh.Add(7, "transferred away", Labels.Synthetic);
            gh.NodeRepositoryOverride = "sun-of-anton/Halyard"; // GraphQL says the node now lives elsewhere
            var provider = new GitHubProvider(gh.Http, "https://api.github.com", "tok", Sandbox, 0);
            var writer = Gateway().Open(provider, Sandbox, new WriteIntent("teardown", "r"));
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => writer.DeleteAsync(new WorkItem(Sandbox, 7), CancellationToken.None));
            Assert.Contains("Refusing to delete", ex.Message);
            Assert.True(gh.Exists(7));
            Assert.DoesNotContain(gh.Http.Calls, c => c.JsonBody != null && c.JsonBody.Contains("deleteIssue"));
        }

        [Fact]
        public async Task LinkParentRefusesItemsOutsideTheTarget()
        {
            var (provider, _) = Provider();
            var writer = Gateway().Open(provider, Sandbox, new WriteIntent("seed", "r"));
            await Assert.ThrowsAsync<WriteBlockedException>(() => writer.LinkParentAsync(new WorkItem(Sandbox, 1), new WorkItem(Other, 2), CancellationToken.None));
        }

        [Fact]
        public async Task VerifiedOpenBlocksTheSameRepositoryUnderAnotherName()
        {
            var http = new FakeTransport();
            http.On("GET", "/repos/fleethacker-data-crucible/Halyard-Ballast", 200, "{\"id\": 111}");
            http.On("GET", "/repos/sun-of-anton/Old-Name", 200, "{\"id\": 111}"); // renamed repo, same id
            var renamedSandbox = new RepositoryRef(ProviderKind.GitHub, "sun-of-anton/Old-Name");
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(renamedSandbox);
            policy.ProtectedTargets.Add(Sandbox);
            var provider = new GitHubProvider(http, "https://api.github.com", "tok", Sandbox, 0);
            await Assert.ThrowsAsync<WriteBlockedException>(() => new WriteGateway(policy).OpenVerifiedAsync(provider, renamedSandbox, new WriteIntent("seed", "r"), CancellationToken.None));
        }

        [Fact]
        public async Task UpdateRefusesItemsFromAnotherRepository()
        {
            var (provider, _) = Provider();
            var writer = Gateway().Open(provider, Sandbox, new WriteIntent("apply", "r"));
            var foreign = new WorkItem(Other, 1);
            await Assert.ThrowsAsync<WriteBlockedException>(() => writer.UpdateAsync(foreign, new WorkItemPatch { Body = "x" }, CancellationToken.None));
        }

        [Fact]
        public async Task FailedWritesAreAuditedAndRethrown()
        {
            var http = new FakeTransport().On("POST", "/issues", 403, "{\"message\":\"Resource not accessible\"}");
            var provider = new GitHubProvider(http, "https://api.github.com", "tok", Sandbox, 0);
            var gateway = Gateway();
            var writer = gateway.Open(provider, Sandbox, new WriteIntent("test", "r"));
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => writer.CreateAsync(new WorkItemDraft { Title = "x" }, CancellationToken.None));
            Assert.Equal("FORBIDDEN", ex.Code);
            var entry = Assert.Single(gateway.Audit);
            Assert.False(entry.Succeeded);
            Assert.Contains("403", entry.Error);
        }
    }

    public class GitHubProviderTests
    {
        private static readonly RepositoryRef Repo = new RepositoryRef(ProviderKind.GitHub, "fleethacker-data-crucible/Halyard-Ballast");

        [Fact]
        public async Task ListsIssuesAcrossPagesAndSkipsPullRequests()
        {
            var page1 = new JArray(
                new JObject { ["number"] = 1, ["id"] = 11, ["node_id"] = "a", ["title"] = "one", ["body"] = "~~~halyard-meta\ntype: STORY\nid: STORY-one\n~~~", ["state"] = "open", ["labels"] = new JArray(new JObject { ["name"] = "type::story" }) },
                new JObject { ["number"] = 2, ["id"] = 12, ["node_id"] = "b", ["title"] = "a PR", ["body"] = "", ["state"] = "open", ["labels"] = new JArray(), ["pull_request"] = new JObject() });
            var page2 = new JArray(new JObject { ["number"] = 3, ["id"] = 13, ["node_id"] = "c", ["title"] = "three", ["body"] = "", ["state"] = "closed", ["labels"] = new JArray() });
            var http = new FakeTransport();
            http.On("GET", "page=2", 200, page2.ToString());
            http.On("GET", "/issues?state=all", 200, page1.ToString(), new Dictionary<string, string> { { "Link", "<https://api.github.com/repos/fleethacker-data-crucible/Halyard-Ballast/issues?state=all&per_page=100&page=2>; rel=\"next\"" } });
            var provider = new GitHubProvider(http, "https://api.github.com", "tok", Repo, 0);
            var items = await provider.ListWorkItemsAsync(new WorkItemQuery(), null, CancellationToken.None);
            Assert.Equal(new long[] { 1, 3 }, items.Select(i => i.Number).ToArray());
            Assert.Equal("STORY-one", items[0].CanonicalId);
            Assert.Equal(WorkItemState.Closed, items[1].State);
            Assert.Equal(11, items[0].ProviderNumericId);
            Assert.All(http.Calls, c => Assert.Equal("Bearer tok", c.Headers["Authorization"]));
        }

        [Fact]
        public async Task MapsHttpStatusesToStableCodes()
        {
            var http = new FakeTransport().On("GET", "/user", 401, "{\"message\":\"Bad credentials\"}");
            var provider = new GitHubProvider(http, "https://api.github.com", "bad", Repo, 0);
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => provider.TestConnectionAsync(CancellationToken.None));
            Assert.Equal("AUTH_REJECTED", ex.Code);
            Assert.Contains("Bad credentials", ex.Message);
        }

        [Theory]
        [InlineData("fleethacker-data-crucible/Halyard-Ballast", true)]
        [InlineData("owner/repo.name_1", true)]
        [InlineData("acme/x/../../repos/acme/prod", false)]
        [InlineData("acme/..", false)]
        [InlineData("acme/.", false)]
        [InlineData("acme/repo?x=1", false)]
        [InlineData("acme%2Fother/repo", false)]
        [InlineData("just-a-name", false)]
        public void ValidatesRepositoryIdentifiers(string identifier, bool valid)
        {
            Assert.Equal(valid, GitHubProvider.IsValidIdentifier(identifier));
        }

        [Fact]
        public void RequiresOwnerSlashRepo()
        {
            Assert.Throws<ArgumentException>(() => new GitHubProvider(new FakeTransport(), "https://api.github.com", null, new RepositoryRef(ProviderKind.GitHub, "just-a-name")));
        }
    }

    public class GitLabProviderTests
    {
        private static readonly RepositoryRef Project = new RepositoryRef(ProviderKind.GitLab, "5678");

        [Fact]
        public async Task UsesPrivateTokenAndNextPageHeader()
        {
            var issue = new JObject { ["iid"] = 271, ["id"] = 9000, ["title"] = "t", ["description"] = "d", ["state"] = "opened", ["labels"] = new JArray("area::x"), ["web_url"] = "https://gitlab.example.com/x/-/issues/271" };
            var http = new FakeTransport();
            http.On("GET", "&page=2", 200, new JArray(issue).ToString());
            http.On("GET", "&page=1", 200, new JArray(issue).ToString(), new Dictionary<string, string> { { "X-Next-Page", "2" } });
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "glpat", Project, 0);
            var items = await provider.ListWorkItemsAsync(new WorkItemQuery(), null, CancellationToken.None);
            Assert.Equal(2, items.Count);
            Assert.Equal(WorkItemState.Open, items[0].State);
            Assert.Equal("gitlab:5678:271", items[0].Key);
            Assert.All(http.Calls, c => Assert.Equal("glpat", c.Headers["PRIVATE-TOKEN"]));
            Assert.All(http.Calls, c => Assert.StartsWith("https://gitlab.example.com/api/v4/projects/5678/issues", c.Url));
        }

        [Theory]
        [InlineData("5678", true)]
        [InlineData("group/sub/project", true)]
        [InlineData("group/../other", false)]
        [InlineData("project-only", false)]
        [InlineData("group//project", false)]
        public void ValidatesProjectIdentifiers(string identifier, bool valid)
        {
            Assert.Equal(valid, GitLabProvider.IsValidIdentifier(identifier));
        }

        [Fact]
        public async Task CreateSendsCommaSeparatedLabelsAndDescription()
        {
            var http = new FakeTransport().On("POST", "/issues", 201, "{\"iid\":5,\"id\":1,\"title\":\"t\",\"description\":\"b\",\"state\":\"opened\",\"labels\":[]}");
            var provider = new GitLabProvider(http, "https://gitlab.example.com/api/v4/", "glpat", Project, 0);
            var writer = provider.OpenWriter(Project);
            var created = await writer.CreateAsync(new WorkItemDraft { Title = "t", Body = "b", Labels = new List<string> { "a", "b" } }, CancellationToken.None);
            Assert.Equal(5, created.Number);
            var sent = JObject.Parse(http.Writes().Single().JsonBody!);
            Assert.Equal("a,b", (string)sent["labels"]!);
            Assert.Equal("b", (string)sent["description"]!);
        }
    }

    public class SeederTests
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitHub, "fleethacker-data-crucible/Halyard-Ballast");

        [Fact]
        public async Task SeedsInParentOrderWithRunLabelsAndManifest()
        {
            var options = SyntheticOptions.ForTotal(12);
            var catalog = SyntheticCatalog.Generate(options);
            var http = new FakeTransport();
            var next = 100L;
            http.On("POST", "/labels", 201, "{}");
            http.On("POST", "/sub_issues", 201, "{}");
            http.On("PATCH", "/issues/", call =>
            {
                var n = long.Parse(call.Url.Substring(call.Url.LastIndexOf('/') + 1));
                var body = new JObject { ["number"] = n, ["id"] = n * 10, ["node_id"] = "N" + n, ["title"] = "t", ["body"] = "b", ["state"] = "closed", ["labels"] = new JArray(), ["html_url"] = "https://github.com/x/y/issues/" + n };
                return new HttpReply(200, body.ToString(), new Dictionary<string, string>());
            });
            http.On("POST", "/issues", call =>
            {
                var sent = JObject.Parse(call.JsonBody!);
                var n = ++next;
                var body = new JObject { ["number"] = n, ["id"] = n * 10, ["node_id"] = "N" + n, ["title"] = sent["title"], ["body"] = sent["body"], ["state"] = "open", ["labels"] = new JArray(sent["labels"]!.Select(l => new JObject { ["name"] = l })), ["html_url"] = "https://github.com/x/y/issues/" + n };
                return new HttpReply(201, body.ToString(), new Dictionary<string, string>());
            });
            var provider = new GitHubProvider(http, "https://api.github.com", "tok", Sandbox, 0);
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Sandbox);
            var gateway = new WriteGateway(policy);
            var manifest = await SandboxSeeder.SeedAsync(provider, gateway, Sandbox, catalog, new SeedOptions { RunId = "t1", NativeLinks = true }, null, CancellationToken.None);

            Assert.True(manifest.Completed);
            Assert.Equal(catalog.Count, manifest.Items.Count);
            var creates = http.Writes().Where(c => c.Url.EndsWith("/issues")).Select(c => JObject.Parse(c.JsonBody!)).ToList();
            Assert.Equal("THEME", (string)creates[0]["title"]!.ToString().Substring(0, 5));
            Assert.All(creates, c => Assert.Contains("halyard-run::t1", c["labels"]!.Select(l => (string)l!)));
            // Finished work from past sprints is closed after creation, so the sandbox has delivery history.
            var closes = http.Writes().Where(c => c.Method == "PATCH").Select(c => JObject.Parse(c.JsonBody!)).ToList();
            Assert.Equal(catalog.Count(c => c.Closed), closes.Count);
            Assert.All(closes, c => Assert.Equal("closed", (string)c["state"]!));
            Assert.All(creates, c => Assert.Contains("halyard-synthetic", c["labels"]!.Select(l => (string)l!)));
            // every child's parent was created before it
            var order = creates.Select(c => HalyardMeta.Parse((string)c["body"]!)!).ToList();
            var seen = new HashSet<string>();
            foreach (var meta in order)
            {
                if (!string.IsNullOrEmpty(meta.Parent)) Assert.Contains(meta.Parent!, seen);
                seen.Add(meta.Id!);
            }
            Assert.Contains(order, m => m.Get("parent_ref") != null && m.Get("parent_ref")!.StartsWith("#"));
            Assert.True(manifest.Items.Count(i => i.NativeLinked) == catalog.Count(c => c.ParentId != null));
        }

        [Fact]
        public async Task TeardownDeletesOnlyTheRequestedRun()
        {
            var gh = new FakeGitHubRepo("fleethacker-data-crucible/Halyard-Ballast");
            gh.Add(1, "run1", "halyard-synthetic", "halyard-run::r1");
            gh.Add(2, "run2", "halyard-synthetic", "halyard-run::r2");
            gh.Add(3, "real, but labelled with the run", "halyard-run::r1");
            gh.IgnoreLabelFilter = true; // even if the server returned everything, only r1 synthetic items go
            var provider = new GitHubProvider(gh.Http, "https://api.github.com", "tok", Sandbox, 0);
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Sandbox);
            var result = await SandboxTeardown.RunAsync(provider, new WriteGateway(policy), Sandbox, new TeardownOptions { Scope = TeardownScope.Run, RunId = "r1" }, null, CancellationToken.None);
            Assert.Equal(1, result.Matched);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(new[] { "github:fleethacker-data-crucible/Halyard-Ballast:1" }, result.DeletedKeys.ToArray());
            Assert.False(gh.Exists(1));
            Assert.True(gh.Exists(2));
            Assert.True(gh.Exists(3));
        }

        [Fact]
        public async Task WipeNeedsTheConfirmationPhrase()
        {
            var http = new FakeTransport();
            var provider = new GitHubProvider(http, "https://api.github.com", "tok", Sandbox, 0);
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Sandbox);
            await Assert.ThrowsAsync<WriteBlockedException>(() => SandboxTeardown.RunAsync(provider, new WriteGateway(policy), Sandbox, new TeardownOptions { Scope = TeardownScope.Everything, ConfirmText = "yes" }, null, CancellationToken.None));
            Assert.Empty(http.Calls);
        }

        [Fact]
        public void CatalogueIsDeterministicAndSerialisable()
        {
            var a = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(100));
            var b = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(100));
            Assert.Equal(SyntheticCatalog.ToJson(a, SyntheticOptions.ForTotal(100)), SyntheticCatalog.ToJson(b, SyntheticOptions.ForTotal(100)));
            var roundTrip = SyntheticCatalog.FromJson(SyntheticCatalog.ToJson(a, SyntheticOptions.ForTotal(100)));
            Assert.Equal(a.Count, roundTrip.Count);
            Assert.Equal(a[57].Description, roundTrip[57].Description);
            Assert.Equal(100, a.Select(i => i.Id).Distinct().Count());
            var different = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(100, 7));
            Assert.NotEqual(a.Select(i => i.Meta.Points).ToArray(), different.Select(i => i.Meta.Points).ToArray());
        }

        [Fact]
        public void CommittedFixturesMatchTheGenerator()
        {
            // If this fails, the generator changed: regenerate with New-HalyardSyntheticCatalog and commit the files.
            foreach (var (file, defects) in new[] { ("synthetic-work-items.json", false), ("synthetic-work-items-with-gaps.json", true) })
            {
                var path = FindFixture(file);
                var options = SyntheticOptions.ForTotal(100);
                options.IncludeDefects = defects;
                var expected = SyntheticCatalog.Generate(options);
                var committed = SyntheticCatalog.Load(path);
                Assert.Equal(expected.Count, committed.Count);
                for (var i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].Id, committed[i].Id);
                    Assert.Equal(expected[i].Description, committed[i].Description);
                    Assert.Equal(expected[i].Labels, committed[i].Labels);
                }
            }
        }

        internal static string FindFixture(string name)
        {
            foreach (var start in new[] { AppContext.BaseDirectory, System.IO.Directory.GetCurrentDirectory() })
            {
                var dir = new System.IO.DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = System.IO.Path.Combine(dir.FullName, "fixtures", name);
                    if (System.IO.File.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            throw new System.IO.FileNotFoundException("fixture not found: " + name);
        }

        [Theory]
        [InlineData(100, 100)]
        [InlineData(25, 25)]
        [InlineData(500, 500)]
        [InlineData(7, 7)]
        public void ForTotalHitsTheRequestedCount(int total, int expected)
        {
            var options = SyntheticOptions.ForTotal(total);
            Assert.Equal(expected, options.Total);
            Assert.Equal(expected, SyntheticCatalog.Generate(options).Count);
        }
    }
}
