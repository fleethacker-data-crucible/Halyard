using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Analysis;
using Cascade.Core.Config;
using Cascade.Core.Host;
using Cascade.Core.Intelligence;
using Cascade.Core.Llm;
using Cascade.Core.Model;
using Cascade.Core.Providers;
using Cascade.Core.Schema;
using Cascade.Core.Synthetic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cascade.Core.Tests
{
    public class EstimateParserTests
    {
        [Fact]
        public void ParsesJsonArrayWithFences()
        {
            var text = "```json\n[{\"id\":\"STORY-a\",\"points\":5,\"confidence\":\"high\",\"reason\":\"clear\"},{\"id\":\"STORY-b\",\"points\":\"TBD\",\"confidence\":0.3,\"reason\":\"no AC\"}]\n```";
            var rows = EstimateParser.Parse(text);
            Assert.Equal(2, rows.Count);
            Assert.Equal("5", rows[0].Points);
            Assert.Equal("high", rows[0].Confidence);
            Assert.Equal("low", rows[1].Confidence);
        }

        [Fact]
        public void ParsesObjectWithListProperty()
        {
            var rows = EstimateParser.Parse("{\"estimates\":[{\"ref\":\"#12\",\"effort\":\"L\",\"reason\":\"big\"}]}");
            Assert.Single(rows);
            Assert.Equal("#12", rows[0].Ref);
            Assert.Equal("L", rows[0].Effort);
        }

        [Fact]
        public void ParsesMarkdownTable()
        {
            var text = "| IID | Title | Points | Reason |\n|---|---|---|---|\n| **264** | Do thing | **5** | medium clarity |\n| 276 | Other | 3 pts | fine |";
            var rows = EstimateParser.Parse(text);
            Assert.Equal(2, rows.Count);
            Assert.Equal("264", rows[0].Ref);
            Assert.Equal("5", rows[0].Points);
            Assert.Equal("276", rows[1].Ref);
            Assert.Equal("3", rows[1].Points);
        }

        [Fact]
        public void UnparseableTextYieldsNothing()
        {
            Assert.Empty(EstimateParser.Parse("I cannot help with that."));
            Assert.Empty(EstimateParser.Parse(null));
        }
    }

    public class LlmClientTests
    {
        [Fact]
        public void ExtractsTextFromVariousShapes()
        {
            Assert.Equal("hi", LlmClient.ExtractText(JToken.Parse("{\"choices\":[{\"message\":{\"content\":\"hi\"}}]}")));
            Assert.Equal("ab", LlmClient.ExtractText(JToken.Parse("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"text\":\"b\"}]}}]}")));
            Assert.Equal("r", LlmClient.ExtractText(JToken.Parse("{\"choices\":[{\"message\":{\"content\":null,\"reasoning_content\":\"r\"}}]}")));
            Assert.Equal("p", LlmClient.ExtractText(JToken.Parse("{\"predictions\":[\"p\"]}")));
            Assert.Equal(string.Empty, LlmClient.ExtractText(JToken.Parse("{}")));
        }

        [Fact]
        public async Task SendsBearerTokenAndProfileShapedBody()
        {
            var http = new FakeTransport().On("POST", "/chat/completions", 200, "{\"choices\":[{\"message\":{\"content\":\"[]\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}");
            var settings = new LlmSettings { Profile = LlmProfile.OpenAi, OpenAiToken = "'Bearer sk-abcdefghijklmnop'\n", OpenAiUrl = "https://example.test/v1/chat/completions", OpenAiModel = "m" };
            var client = new LlmClient(settings, http);
            var r = await client.ChatAsync(new[] { LlmMessage.User("x") }, CancellationToken.None);
            var call = http.Calls.Single();
            Assert.Equal("Bearer sk-abcdefghijklmnop", call.Headers["Authorization"]);
            var body = JObject.Parse(call.JsonBody!);
            Assert.Equal("m", (string)body["model"]!);
            Assert.False((bool)body["stream"]!);
            Assert.Equal(10, r.PromptTokens);
        }

        [Fact]
        public async Task GrokProfileOmitsStreamFlag()
        {
            var http = new FakeTransport().On("POST", "invocations", 200, "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
            var client = new LlmClient(new LlmSettings { Profile = LlmProfile.Grok, GrokToken = "dapi0123456789abcdef0123456789" }, http);
            await client.TestAsync(CancellationToken.None);
            Assert.Null(JObject.Parse(http.Calls.Single().JsonBody!)["stream"]);
            Assert.StartsWith(LlmSettings.DefaultGrokUrl, http.Calls.Single().Url);
        }

        [Fact]
        public void RedactsTokenShapedStrings()
        {
            var text = "key sk-abcdefghijklmnopqrstuvwxyz and ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456 and glpat-abcdefghijklmnopqrst";
            var red = LlmClient.Redact(text);
            Assert.DoesNotContain("sk-abc", red);
            Assert.DoesNotContain("ghp_", red);
            Assert.DoesNotContain("glpat-", red);
            Assert.Equal(3, red.Split(new[] { "[REDACTED]" }, StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void AutoProfileFollowsAvailableToken()
        {
            Assert.Equal(LlmProfile.Grok, new LlmSettings { GrokToken = "x" }.Resolve());
            Assert.Equal(LlmProfile.GenAi, new LlmSettings { GenAiToken = "x", GrokToken = "y" }.Resolve());
            Assert.Equal(LlmProfile.OpenAi, new LlmSettings { OpenAiToken = "x" }.Resolve());
        }
    }

    public class EstimatorTests
    {
        private sealed class ScriptedLlm : ILlmClient
        {
            private readonly Func<IReadOnlyList<LlmMessage>, string> _reply;
            public ScriptedLlm(Func<IReadOnlyList<LlmMessage>, string> reply) { _reply = reply; }
            public LlmProfile Profile { get { return LlmProfile.OpenAi; } }
            public string Model { get { return "scripted"; } }
            public Task<LlmResponse> ChatAsync(IReadOnlyList<LlmMessage> messages, CancellationToken cancellation) { return Task.FromResult(new LlmResponse { Text = _reply(messages) }); }
            public Task<LlmResponse> TestAsync(CancellationToken cancellation) { return Task.FromResult(new LlmResponse { Text = "OK" }); }
        }

        [Fact]
        public async Task GatesUndefinedStoriesAndClampsModelOutput()
        {
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(40));
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            // Make every story unsized; strip acceptance criteria from one.
            foreach (var it in items.Where(i => i.Type == WorkItemType.Story))
            {
                var meta = it.Meta!.Clone();
                meta.Points = "TBD";
                it.Body = Descriptions.Rewrite(it.Body, meta);
                it.InvalidateMeta();
            }
            var undefined = items.First(i => i.Type == WorkItemType.Story);
            undefined.Body = undefined.Body.Replace("## Acceptance criteria", "## Notes").Replace("## User need", "## Context").Replace("I want to", "the team will");
            undefined.InvalidateMeta();

            var llm = new ScriptedLlm(messages =>
            {
                var ids = System.Text.RegularExpressions.Regex.Matches(messages[1].Content, @"### id: (\S+)").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
                return new JArray(ids.Select((id, i) => new JObject { ["id"] = id, ["points"] = i == 0 ? 13 : 3, ["confidence"] = "high", ["reason"] = "r" })).ToString();
            });
            var batch = await Estimator.RunAsync(items, llm, new EstimateOptions { MaxItems = 10, BatchSize = 4 }, Items.Repo, null, CancellationToken.None);
            Assert.Contains(batch.SkipReasons, s => s.StartsWith(undefined.CanonicalId));
            Assert.Equal(10, batch.Proposals.Count);
            Assert.DoesNotContain(batch.Proposals, p => p.ItemKey == undefined.Key);
            Assert.All(batch.Proposals, p => Assert.True(Sizing.IsValidPoints(p.ProposedValue)));
            Assert.Contains(batch.Proposals, p => p.ProposedValue == "8" && p.Reason.Contains("clamped"));
        }

        [Fact]
        public void ParentProposalsComeFromEvidence()
        {
            var options = SyntheticOptions.ForTotal(60);
            options.IncludeDefects = true;
            var catalog = SyntheticCatalog.Generate(options);
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            var report = Review.Run(items, Taxonomy.Default());
            var batch = Estimator.ParentProposals(report, Items.Repo);
            Assert.NotEmpty(batch.Proposals);
            Assert.All(batch.Proposals, p => Assert.Equal(ProposalKind.ParentLink, p.Kind));
            Assert.All(batch.Proposals, p => Assert.Equal(ProposalDecision.Pending, p.Decision));
        }
    }

    public class ProposalStoreTests
    {
        [Fact]
        public void SavesDecidesAndReloads()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cascade-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ProposalStore(dir);
                var batch = new ProposalBatch { Kind = ProposalKind.StoryPoints, Target = "github:a/b" };
                batch.Proposals.Add(new Proposal { BatchId = batch.Id, Kind = ProposalKind.StoryPoints, ItemKey = "k1", ProposedValue = "5", Confidence = "high" });
                batch.Proposals.Add(new Proposal { BatchId = batch.Id, Kind = ProposalKind.StoryPoints, ItemKey = "k2", ProposedValue = "3", Confidence = "low" });
                store.Save(batch);
                Assert.Equal(1, store.DecideAll(batch.Id, ProposalDecision.Approved, "medium"));
                var reloaded = store.Load(batch.Id)!;
                Assert.Equal(ProposalDecision.Approved, reloaded.Proposals[0].Decision);
                Assert.Equal(ProposalDecision.Pending, reloaded.Proposals[1].Decision);
                store.Decide(batch.Id, reloaded.Proposals[1].Id, ProposalDecision.Rejected, "no");
                Assert.Equal("no", store.Load(batch.Id)!.Proposals[1].DecisionNote);
                Assert.Single(store.LoadAll());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void EnvFileDoesNotOverrideExistingVariables()
        {
            var path = Path.GetTempFileName();
            var key = "CASCADE_TEST_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                File.WriteAllText(path, "# comment\n" + key + "=\"from file\"\nexport " + key + "_B='b'\n");
                Environment.SetEnvironmentVariable(key, "preset");
                var applied = CascadeSettings.LoadEnvFile(path);
                Assert.Equal(1, applied);
                Assert.Equal("preset", Environment.GetEnvironmentVariable(key));
                Assert.Equal("b", Environment.GetEnvironmentVariable(key + "_B"));
            }
            finally
            {
                File.Delete(path);
                Environment.SetEnvironmentVariable(key, null);
                Environment.SetEnvironmentVariable(key + "_B", null);
            }
        }

        [Fact]
        public void PublicDictionaryNeverContainsTokens()
        {
            var s = new CascadeSettings();
            s.Provider.Token = "ghp_SECRETSECRETSECRETSECRETSECRET";
            s.Llm.GenAiToken = "secret-token-value";
            var json = Json.Serialize(s.ToPublicDictionary());
            Assert.DoesNotContain("SECRET", json);
            Assert.DoesNotContain("secret-token-value", json);
            Assert.Contains("\"hasProviderToken\":true", json);
        }

        [Fact]
        public void WritePolicyProtectsSourceInSandboxMode()
        {
            var s = new CascadeSettings();
            s.Provider.Kind = ProviderKind.GitLab;
            s.Provider.SourceRepository = "16629";
            s.Provider.SandboxRepository = "52999";
            var policy = s.BuildWritePolicy();
            Assert.True(policy.IsSandbox(new RepositoryRef(ProviderKind.GitLab, "52999")));
            Assert.True(policy.IsProtected(new RepositoryRef(ProviderKind.GitLab, "16629")));
            Assert.Throws<Safety.WriteBlockedException>(() => policy.Assert(new RepositoryRef(ProviderKind.GitLab, "16629"), new Safety.WriteIntent("x", "y")));
        }
    }

    public class HostTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cascade-host-" + Guid.NewGuid().ToString("N"));
        private readonly CascadeHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();

        public HostTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new CascadeSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "sun-of-anton/cascade-sandbox";
            settings.Provider.SourceRepository = "sun-of-anton/cascade-sandbox";
            _host = new CascadeHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null });
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30));
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(catalog, Items.Repo), null);
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Cascade-Session", _info.SessionToken);
        }

        [Fact]
        public async Task HealthIsOpenEverythingElseNeedsTheToken()
        {
            var anon = new HttpClient();
            Assert.Equal(200, (int)(await anon.GetAsync(_info.Url + "api/health")).StatusCode);
            Assert.Equal(401, (int)(await anon.GetAsync(_info.Url + "api/status")).StatusCode);
            Assert.Equal(401, (int)(await anon.GetAsync(_info.Url + "api/status?session=" + _info.SessionToken)).StatusCode);
            Assert.Equal(200, (int)(await _client.GetAsync(_info.Url + "api/status")).StatusCode);
        }

        [Fact]
        public async Task ReviewAndHierarchyServeTheSnapshot()
        {
            var review = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/review"));
            Assert.Equal(30, (int)review["summary"]!["total"]!);
            Assert.Equal(0, (int)review["summary"]!["needsReview"]!);
            var hierarchy = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/hierarchy"));
            Assert.Single(hierarchy["roots"]!);
            Assert.Empty(hierarchy["orphans"]!);
            var schema = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/schema"));
            var storyKeys = schema["requiredMeta"]!["STORY"]!.Select(k => (string)k!).ToList();
            Assert.Contains("points", storyKeys);
            Assert.Contains("sprint", storyKeys);
            Assert.NotNull(schema["deficiencyRules"]);
        }

        [Fact]
        public async Task WriteModeCannotBeChangedFromTheBrowser()
        {
            foreach (var locked in new[] { "{\"writeMode\":\"production\"}", "{\"sandboxRepository\":\"acme/real\"}", "{\"sourceRepository\":\"acme/real\"}", "{\"baseUrl\":\"https://evil.example\"}", "{\"provider\":\"gitlab\"}" })
            {
                var r = await _client.PostAsync(_info.Url + "api/config", new StringContent(locked));
                Assert.Equal(400, (int)r.StatusCode);
            }
            Assert.Equal("sun-of-anton/cascade-sandbox", _host.Settings.Provider.SandboxRepository);
            var ok = await _client.PostAsync(_info.Url + "api/config", new StringContent("{\"labelFilter\":\"team::x\"}"));
            Assert.Equal(200, (int)ok.StatusCode);
            Assert.Equal("team::x", (string)JObject.Parse(await ok.Content.ReadAsStringAsync())["config"]!["labelFilter"]!);
        }

        [Fact]
        public async Task ProviderCallsWithoutTokenAreAClearConflict()
        {
            var r = await _client.PostAsync(_info.Url + "api/sandbox/seed", new StringContent("{\"total\":10}"));
            Assert.Equal(409, (int)r.StatusCode);
            Assert.Equal("PROVIDER_NOT_READY", (string)JObject.Parse(await r.Content.ReadAsStringAsync())["code"]!);
        }

        [Fact]
        public async Task StaticTraversalIsImpossible()
        {
            Assert.Equal("/etc/passwd", HttpServer.NormalizePath("/../../etc/passwd"));
            Assert.Equal("/a/c", HttpServer.NormalizePath("/a/./b/../c"));
            var r = await _client.GetAsync(_info.Url + "..%2F..%2Fetc%2Fpasswd");
            Assert.NotEqual(200, (int)r.StatusCode);
        }

        [Fact]
        public async Task StopRequiresIntentHeader()
        {
            var r = await _client.PostAsync(_info.Url + "api/session/stop", new StringContent(""));
            Assert.Equal(403, (int)r.StatusCode);
        }

        public void Dispose()
        {
            _host.Dispose();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}
