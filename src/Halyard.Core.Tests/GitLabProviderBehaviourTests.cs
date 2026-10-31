using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Serialization;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>GitLab REST v4 adapter against a stateful fake project: paging, filters, error mapping, the writer and the gateway.</summary>
    public class GitLabProviderBehaviourTests
    {
        private static readonly RepositoryRef Project = new RepositoryRef(ProviderKind.GitLab, "5678");
        private static readonly RepositoryRef Source = new RepositoryRef(ProviderKind.GitLab, "1234");

        private static (GitLabProvider provider, FakeGitLabProject gitlab) Make(string baseUrl = "https://gitlab.example.com")
        {
            var gitlab = new FakeGitLabProject(5678, "platform/halyard-sandbox");
            return (new GitLabProvider(gitlab.Http, baseUrl, "glpat-test", Project, 0), gitlab);
        }

        private static WriteGateway Gateway()
        {
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Project);
            policy.ProtectedTargets.Add(Source);
            return new WriteGateway(policy);
        }

        // ---- construction and capabilities ----

        [Fact]
        public void RejectsAGitHubRepositoryAndABadProjectIdentifier()
        {
            var http = new FakeTransport();
            Assert.Throws<ArgumentException>(() => new GitLabProvider(http, "https://gitlab.example.com", "t", new RepositoryRef(ProviderKind.GitHub, "a/b")));
            var ex = Assert.Throws<ArgumentException>(() => new GitLabProvider(http, "https://gitlab.example.com", "t", new RepositoryRef(ProviderKind.GitLab, "group/../other")));
            Assert.Contains("group/../other", ex.Message);
        }

        [Fact]
        public void ReportsGitLabCapabilities()
        {
            var (provider, _) = Make();
            Assert.Equal(ProviderKind.GitLab, provider.Kind);
            Assert.True(provider.Capabilities.DeleteIssues);
            Assert.False(provider.Capabilities.NativeParentLinks);
            Assert.Equal(250, provider.Capabilities.RecommendedWriteDelayMs);
        }

        [Theory]
        [InlineData("https://gitlab.example.com")]
        [InlineData("https://gitlab.example.com/")]
        [InlineData("https://gitlab.example.com/api/v4")]
        [InlineData("https://gitlab.example.com/api/v4/")]
        [InlineData("https://gitlab.example.com/API/V4")]
        public async Task NormalizesTheBaseUrlToApiV4Once(string baseUrl)
        {
            var (provider, gitlab) = Make(baseUrl);
            await provider.GetWorkItemAsync(1, CancellationToken.None);
            var url = gitlab.Http.Calls.Single().Url;
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(url, "/api/v4", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
            Assert.EndsWith("/projects/5678/issues/1", url);
        }

        [Fact]
        public async Task EncodesAGroupProjectPathAsOnePathSegment()
        {
            var gitlab = new FakeGitLabProject(5678, "platform/team/halyard-sandbox", addressByPath: true);
            var provider = new GitLabProvider(gitlab.Http, "https://gitlab.example.com", "t", new RepositoryRef(ProviderKind.GitLab, "platform/team/halyard-sandbox"), 0);
            gitlab.Add("one");
            var items = await provider.ListWorkItemsAsync(new WorkItemQuery(), null, CancellationToken.None);
            Assert.Single(items);
            Assert.Contains("/projects/platform%2Fteam%2Fhalyard-sandbox/issues", gitlab.Http.Calls.Single().Url);
        }

        // ---- reading ----

        [Fact]
        public async Task TestConnectionReadsTheUserThenTheProject()
        {
            var (provider, gitlab) = Make();
            var identity = await provider.TestConnectionAsync(CancellationToken.None);
            Assert.Equal("halyard-bot", identity.Login);
            Assert.Equal("Halyard Bot", identity.Name);
            Assert.Equal(ProviderKind.GitLab, identity.Provider);
            Assert.Equal("https://gitlab.example.com/api/v4", identity.ApiBase);
            Assert.EndsWith("/api/v4/user", gitlab.Http.Calls[0].Url);
            Assert.EndsWith("/api/v4/projects/5678", gitlab.Http.Calls[1].Url);
        }

        [Theory]
        [InlineData(401, "AUTH_REJECTED")]
        [InlineData(403, "FORBIDDEN")]
        [InlineData(404, "NOT_FOUND")]
        [InlineData(429, "RATE_LIMITED")]
        [InlineData(503, "SERVER_ERROR")]
        public async Task MapsStatusesToStableCodesWithoutLeakingTheToken(int status, string code)
        {
            var http = new FakeTransport().On("GET", "/user", status, "{\"message\":\"nope\"}");
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "glpat-secret", Project, 0);
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => provider.TestConnectionAsync(CancellationToken.None));
            Assert.Equal(status, ex.Status);
            Assert.Equal(code, ex.Code);
            Assert.Contains("nope", ex.Message);
            Assert.DoesNotContain("glpat-secret", ex.Message);
        }

        [Fact]
        public async Task ListsRepositoriesFollowingPagesUpToTheLimit()
        {
            var (provider, _) = Make();
            var repos = await provider.ListRepositoriesAsync(10, CancellationToken.None);
            var repo = Assert.Single(repos);
            Assert.Equal("5678", repo.Identifier);
            Assert.Equal("platform/halyard-sandbox", repo.DisplayName);
            Assert.Equal(ProviderKind.GitLab, repo.Provider);
        }

        [Fact]
        public async Task ListsLabelsAcrossPages()
        {
            var (provider, gitlab) = Make();
            gitlab.PageSize = 2;
            var writer = provider.OpenWriter(Project);
            foreach (var name in new[] { "type::story", "type::epic", "area::x", "area::y", "blocked" })
                await writer.EnsureLabelAsync(new LabelDefinition(name, "ff0000"), CancellationToken.None);
            var labels = await provider.ListLabelsAsync(CancellationToken.None);
            Assert.Equal(5, labels.Count);
            Assert.Contains("area::y", labels);
            Assert.Equal(3, gitlab.Http.Calls.Count(c => c.Method == "GET" && c.Url.Contains("/labels?")));
        }

        [Fact]
        public async Task ListsAllIssuesAcrossPagesAndReportsProgress()
        {
            var (provider, gitlab) = Make();
            gitlab.PageSize = 3;
            for (var i = 1; i <= 7; i++) gitlab.Add("story " + i, "body " + i);
            var messages = new List<string>();
            var items = await provider.ListWorkItemsAsync(new WorkItemQuery(), new Progress<string>(messages.Add) as IProgress<string>, CancellationToken.None);
            Assert.Equal(7, items.Count);
            Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), items.Select(i => i.Number).ToList());
            Assert.Equal(3, gitlab.Http.Calls.Count);
        }

        [Fact]
        public async Task StopsAtMaxItems()
        {
            var (provider, gitlab) = Make();
            gitlab.PageSize = 3;
            for (var i = 1; i <= 9; i++) gitlab.Add("story " + i);
            var items = await provider.ListWorkItemsAsync(new WorkItemQuery { MaxItems = 4 }, null, CancellationToken.None);
            Assert.Equal(4, items.Count);
            Assert.Equal(2, gitlab.Http.Calls.Count); // the third page is never requested
        }

        [Fact]
        public async Task FiltersByStateAndLabelsInTheQueryString()
        {
            var (provider, gitlab) = Make();
            gitlab.Add("open one", "", "opened", "area::x", "type::story");
            gitlab.Add("closed one", "", "closed", "area::x");
            gitlab.Add("open two", "", "opened", "area::y");

            var open = await provider.ListWorkItemsAsync(new WorkItemQuery { State = WorkItemState.Open }, null, CancellationToken.None);
            Assert.Equal(new[] { "open one", "open two" }, open.Select(i => i.Title).ToArray());
            Assert.Contains("state=opened", gitlab.Http.Calls.Last().Url);

            var closed = await provider.ListWorkItemsAsync(new WorkItemQuery { State = WorkItemState.Closed }, null, CancellationToken.None);
            Assert.Equal("closed one", Assert.Single(closed).Title);
            Assert.Equal(WorkItemState.Closed, closed[0].State);
            Assert.Contains("state=closed", gitlab.Http.Calls.Last().Url);

            var all = await provider.ListWorkItemsAsync(new WorkItemQuery(), null, CancellationToken.None);
            Assert.Equal(3, all.Count);
            Assert.Contains("state=all", gitlab.Http.Calls.Last().Url);

            var labelled = await provider.ListWorkItemsAsync(new WorkItemQuery { Labels = { "area::x", "type::story" } }, null, CancellationToken.None);
            Assert.Equal("open one", Assert.Single(labelled).Title);
            Assert.Contains("labels=area%3A%3Ax%2Ctype%3A%3Astory", gitlab.Http.Calls.Last().Url);
        }

        [Fact]
        public async Task SendsUpdatedAfterAsAnIsoTimestamp()
        {
            var (provider, gitlab) = Make();
            await provider.ListWorkItemsAsync(new WorkItemQuery { UpdatedAfter = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.FromHours(2)) }, null, CancellationToken.None);
            Assert.Contains("updated_after=2026-10-01T06%3A30%3A00", gitlab.Http.Calls.Single().Url);
        }

        [Fact]
        public async Task MapsIssueFieldsMilestoneAssigneesAndLabels()
        {
            var (provider, gitlab) = Make();
            var iid = gitlab.Add("Title", "## Body", "opened", "area::x", "type::story");
            gitlab.SetMilestone(iid, "Sprint 12");
            gitlab.SetAssignees(iid, "casey", "riley");
            var item = (await provider.ListWorkItemsAsync(new WorkItemQuery(), null, CancellationToken.None)).Single();
            Assert.Equal("gitlab:5678:" + iid, item.Key);
            Assert.Equal("Title", item.Title);
            Assert.Equal("## Body", item.Body);
            Assert.Equal("Sprint 12", item.Milestone);
            Assert.Equal(new[] { "casey", "riley" }, item.Assignees.ToArray());
            Assert.Equal(new[] { "area::x", "type::story" }, item.Labels.ToArray());
            Assert.Equal(9000 + iid, item.ProviderNumericId);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), item.CreatedAt);
            Assert.NotNull(item.Url);
        }

        [Fact]
        public async Task ANullMilestoneAndMissingFieldsBecomeEmptyValues()
        {
            var http = new FakeTransport().On("GET", "/issues/9", 200, "{\"iid\":9,\"id\":1,\"title\":null,\"description\":null,\"state\":\"closed\",\"milestone\":null,\"labels\":[],\"assignees\":null}");
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "t", Project, 0);
            var item = (await provider.GetWorkItemAsync(9, CancellationToken.None))!;
            Assert.Equal(string.Empty, item.Title);
            Assert.Equal(string.Empty, item.Body);
            Assert.Null(item.Milestone);
            Assert.Empty(item.Assignees);
            Assert.Equal(WorkItemState.Closed, item.State);
        }

        [Fact]
        public async Task GetWorkItemReturnsNullOn404AndThrowsOnOtherFailures()
        {
            var (provider, gitlab) = Make();
            var iid = gitlab.Add("there");
            Assert.Equal("there", (await provider.GetWorkItemAsync(iid, CancellationToken.None))!.Title);
            Assert.Null(await provider.GetWorkItemAsync(99, CancellationToken.None));

            var failing = new GitLabProvider(new FakeTransport().On("GET", "/issues/1", 500, "boom"), "https://gitlab.example.com", "t", Project, 0);
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => failing.GetWorkItemAsync(1, CancellationToken.None));
            Assert.Equal("SERVER_ERROR", ex.Code);
        }

        [Fact]
        public async Task ReturnsTheNumericProjectId()
        {
            var (provider, _) = Make();
            Assert.Equal("5678", await provider.GetRepositoryIdAsync(CancellationToken.None));
            var noId = new GitLabProvider(new FakeTransport().On("GET", "/projects/5678", 200, "{}"), "https://gitlab.example.com", "t", Project, 0);
            await Assert.ThrowsAsync<ProviderHttpException>(() => noId.GetRepositoryIdAsync(CancellationToken.None));
        }

        [Fact]
        public async Task SendsNoAuthHeaderWithoutAToken()
        {
            var http = new FakeTransport().On("GET", "/issues", 200, "[]");
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "  ", Project, 0);
            await provider.ListWorkItemsAsync(new WorkItemQuery(), null, CancellationToken.None);
            Assert.False(http.Calls.Single().Headers.ContainsKey("PRIVATE-TOKEN"));
        }

        [Fact]
        public void ForRepositoryReusesTheProviderForTheSameProjectOnly()
        {
            var (provider, _) = Make();
            Assert.Same(provider, provider.ForRepository(Project));
            var other = provider.ForRepository(Source);
            Assert.NotSame(provider, other);
            Assert.Equal(Source, other.Repository);
        }

        // ---- writing ----

        [Fact]
        public async Task EnsureLabelSendsAHashColourAndIgnoresAnExistingLabel()
        {
            var (provider, gitlab) = Make();
            var writer = provider.OpenWriter(Project);
            var label = new LabelDefinition("type::story", "A1B2C3", "A story");
            await writer.EnsureLabelAsync(label, CancellationToken.None);
            await writer.EnsureLabelAsync(label, CancellationToken.None); // 409 is fine
            var sent = JObject.Parse(gitlab.Http.Writes().First().JsonBody!);
            Assert.Equal("#A1B2C3", (string)sent["color"]!);
            Assert.Equal("A story", (string)sent["description"]!);
            Assert.Equal(new[] { "type::story" }, gitlab.KnownLabels.ToArray());
        }

        [Fact]
        public async Task EnsureLabelSurfacesAuthFailures()
        {
            var http = new FakeTransport().On("POST", "/labels", 401, "{\"message\":\"401 Unauthorized\"}");
            var writer = new GitLabProvider(http, "https://gitlab.example.com", "t", Project, 0).OpenWriter(Project);
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => writer.EnsureLabelAsync(new LabelDefinition("x", "ffffff"), CancellationToken.None));
            Assert.Equal("AUTH_REJECTED", ex.Code);
        }

        [Fact]
        public async Task UpdateSendsOnlyTheChangedFields()
        {
            var (provider, gitlab) = Make();
            var iid = gitlab.Add("old title", "old body", "opened", "a");
            var writer = provider.OpenWriter(Project);

            var updated = await writer.UpdateAsync(iid, new WorkItemPatch { Body = "new body" }, CancellationToken.None);
            Assert.Equal("new body", updated.Body);
            var sent = JObject.Parse(gitlab.Http.Writes().Last().JsonBody!);
            Assert.Equal(new[] { "description" }, sent.Properties().Select(p => p.Name).ToArray());
            Assert.Equal("PUT", gitlab.Http.Writes().Last().Method);

            await writer.UpdateAsync(iid, new WorkItemPatch { Title = "new title", Labels = new List<string> { "a", "b" } }, CancellationToken.None);
            Assert.Equal("new title", provider.GetWorkItemAsync(iid, CancellationToken.None).Result!.Title);
            Assert.Equal(new[] { "a", "b" }, gitlab.LabelsOf(iid).ToArray());
        }

        [Fact]
        public async Task UpdateClosesAndReopensThroughStateEvent()
        {
            var (provider, gitlab) = Make();
            var iid = gitlab.Add("x");
            var writer = provider.OpenWriter(Project);
            var closed = await writer.UpdateAsync(iid, new WorkItemPatch { State = WorkItemState.Closed }, CancellationToken.None);
            Assert.Equal(WorkItemState.Closed, closed.State);
            Assert.Equal("closed", gitlab.StateOf(iid));
            var reopened = await writer.UpdateAsync(iid, new WorkItemPatch { State = WorkItemState.Open }, CancellationToken.None);
            Assert.Equal(WorkItemState.Open, reopened.State);
            Assert.Equal("reopen", (string)JObject.Parse(gitlab.Http.Writes().Last().JsonBody!)["state_event"]!);
        }

        [Fact]
        public async Task DeleteReturnsFalseWhenTheIssueIsAlreadyGone()
        {
            var (provider, gitlab) = Make();
            var iid = gitlab.Add("bye");
            var writer = provider.OpenWriter(Project);
            Assert.True(await writer.DeleteAsync(new WorkItem(Project, iid), CancellationToken.None));
            Assert.False(gitlab.Exists(iid));
            Assert.False(await writer.DeleteAsync(new WorkItem(Project, iid), CancellationToken.None));
        }

        [Fact]
        public async Task DeleteSurfacesForbidden()
        {
            var http = new FakeTransport().On("DELETE", "/issues/3", 403, "{\"message\":\"403 Forbidden\"}");
            var writer = new GitLabProvider(http, "https://gitlab.example.com", "t", Project, 0).OpenWriter(Project);
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => writer.DeleteAsync(new WorkItem(Project, 3), CancellationToken.None));
            Assert.Equal("FORBIDDEN", ex.Code);
        }

        [Fact]
        public async Task NativeParentLinksAreNotUsed()
        {
            var (provider, gitlab) = Make();
            var writer = provider.OpenWriter(Project);
            Assert.False(await writer.LinkParentAsync(new WorkItem(Project, 1), new WorkItem(Project, 2), CancellationToken.None));
            Assert.Empty(gitlab.Http.Calls);
        }

        [Fact]
        public async Task AWriterOpenedForAnotherProjectWritesToThatProject()
        {
            var http = new FakeTransport().On("POST", "/projects/1234/issues", 201, "{\"iid\":1,\"id\":1,\"title\":\"t\",\"description\":\"\",\"state\":\"opened\",\"labels\":[]}");
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "t", Project, 0);
            var writer = provider.OpenWriter(Source);
            Assert.Equal(Source, writer.Repository);
            await writer.CreateAsync(new WorkItemDraft { Title = "t" }, CancellationToken.None);
            Assert.Contains("/projects/1234/issues", http.Writes().Single().Url);
        }

        // ---- through the write gateway ----

        [Fact]
        public async Task GatewayStampsTheManagedLabelAndRefusesTheProtectedProject()
        {
            var (provider, gitlab) = Make();
            var gateway = Gateway();
            Assert.Throws<WriteBlockedException>(() => gateway.Open(provider, Source, new WriteIntent("test", "r")));
            Assert.Empty(gitlab.Http.Calls);

            var writer = gateway.Open(provider, Project, new WriteIntent("test", "create", "run1"));
            var created = await writer.CreateAsync(new WorkItemDraft { Title = "x", Labels = new List<string> { "type::story" } }, CancellationToken.None);
            Assert.Contains("halyard", gitlab.LabelsOf(created.Number));
            Assert.Equal("create", Assert.Single(gateway.Audit).Operation);
        }

        [Fact]
        public async Task GatewayDeleteChecksTheLiveSyntheticLabel()
        {
            var (provider, gitlab) = Make();
            var real = gitlab.Add("someone's real issue", "", "opened", "halyard");
            var synthetic = gitlab.Add("synthetic", "", "opened", Labels.Synthetic);
            var writer = Gateway().Open(provider, Project, new WriteIntent("teardown", "r"));

            var forged = new WorkItem(Project, real) { Title = "forged" };
            forged.Labels.Add(Labels.Synthetic);
            await Assert.ThrowsAsync<WriteBlockedException>(() => writer.DeleteAsync(forged, CancellationToken.None));
            Assert.True(gitlab.Exists(real));

            Assert.True(await writer.DeleteAsync(new WorkItem(Project, synthetic), CancellationToken.None));
            Assert.False(gitlab.Exists(synthetic));
            Assert.False(await writer.DeleteAsync(new WorkItem(Project, 404), CancellationToken.None));
        }

        [Fact]
        public async Task VerifiedOpenBlocksTheProtectedProjectUnderAnotherSpelling()
        {
            // The sandbox is written as a path, the protected project as a numeric id, and both are project 5678.
            var http = new FakeTransport()
                .On("GET", "/projects/platform%2Fhalyard-sandbox", 200, "{\"id\":5678}")
                .On("GET", "/projects/5678", 200, "{\"id\":5678}");
            var sandboxByPath = new RepositoryRef(ProviderKind.GitLab, "platform/halyard-sandbox");
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "t", sandboxByPath, 0);
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(sandboxByPath);
            policy.ProtectedTargets.Add(new RepositoryRef(ProviderKind.GitLab, "5678"));
            var ex = await Assert.ThrowsAsync<WriteBlockedException>(() => new WriteGateway(policy).OpenVerifiedAsync(provider, sandboxByPath, new WriteIntent("test", "r"), CancellationToken.None));
            Assert.Contains("same repository", ex.Message);
            Assert.Empty(http.Writes());
        }

        [Fact]
        public async Task VerifiedOpenAllowsADifferentProject()
        {
            var http = new FakeTransport()
                .On("GET", "/projects/5678", 200, "{\"id\":5678}")
                .On("GET", "/projects/1234", 200, "{\"id\":1234}");
            var provider = new GitLabProvider(http, "https://gitlab.example.com", "t", Project, 0);
            var writer = await Gateway().OpenVerifiedAsync(provider, Project, new WriteIntent("test", "r"), CancellationToken.None);
            Assert.Equal(Project, writer.Target);
        }
    }
}
