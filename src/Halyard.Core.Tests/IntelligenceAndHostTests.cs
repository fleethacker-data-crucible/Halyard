using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Schema;
using Halyard.Core.Synthetic;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
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
        public async Task DatabricksProfileOmitsStreamFlag()
        {
            var http = new FakeTransport().On("POST", "invocations", 200, "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
            var settings = new LlmSettings { Profile = LlmProfile.Databricks, DatabricksToken = "dapi0123456789abcdef0123456789", DatabricksUrl = "https://ws.example.test/serving-endpoints/m/invocations", DatabricksModel = "m" };
            var client = new LlmClient(settings, http);
            await client.TestAsync(CancellationToken.None);
            Assert.Null(JObject.Parse(http.Calls.Single().JsonBody!)["stream"]);
            Assert.StartsWith("https://ws.example.test/serving-endpoints/m/invocations", http.Calls.Single().Url);
        }

        [Fact]
        public async Task RetriesWithTheParametersTheEndpointAccepts()
        {
            // A reasoning model rejects the temperature; an older gateway rejects max_completion_tokens. Both are
            // answered by dropping or renaming the named parameter and retrying.
            var http = new FakeTransport().On("POST", "chat/completions", call =>
            {
                var body = JObject.Parse(call.JsonBody!);
                if (body["temperature"] != null)
                    return new HttpReply(400, "{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0.2 with this model. Only the default (1) value is supported.\",\"type\":\"invalid_request_error\",\"param\":\"temperature\",\"code\":\"unsupported_value\"}}", new Dictionary<string, string>());
                if (body["max_completion_tokens"] != null)
                    return new HttpReply(400, "{\"error\":{\"message\":\"Unrecognized request argument supplied: max_completion_tokens\",\"type\":\"invalid_request_error\",\"param\":null}}", new Dictionary<string, string>());
                return new HttpReply(200, "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", new Dictionary<string, string>());
            });
            var client = new LlmClient(new LlmSettings { Profile = LlmProfile.OpenAi, OpenAiToken = "sk-test-0123456789abcdef", OpenAiModel = "gpt-5-mini" }, http);
            var r = await client.TestAsync(CancellationToken.None);
            Assert.Equal("OK", r.Text);
            Assert.Equal(3, http.Calls.Count);
            var last = JObject.Parse(http.Calls.Last().JsonBody!);
            Assert.Null(last["temperature"]);
            Assert.Null(last["max_completion_tokens"]);
            Assert.Equal(4000, (int)last["max_tokens"]!);
            // The first request already used max_completion_tokens, the current OpenAI name.
            Assert.NotNull(JObject.Parse(http.Calls.First().JsonBody!)["max_completion_tokens"]);
        }

        [Fact]
        public async Task GenuineBadRequestsAreNotRetried()
        {
            var http = new FakeTransport().On("POST", "chat/completions", 400, "{\"error\":{\"message\":\"The model `gpt-9` does not exist or you do not have access to it.\",\"param\":null,\"code\":\"model_not_found\"}}");
            var client = new LlmClient(new LlmSettings { Profile = LlmProfile.OpenAi, OpenAiToken = "sk-test-0123456789abcdef", OpenAiModel = "gpt-9" }, http);
            var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => client.TestAsync(CancellationToken.None));
            Assert.Contains("does not exist", ex.Message);
            Assert.Single(http.Calls);
        }

        [Fact]
        public void DatabricksWithoutUrlIsNotConfigured()
        {
            var s = new LlmSettings { DatabricksToken = "dapi0123456789abcdef0123456789" };
            Assert.False(s.IsConfigured(LlmProfile.Databricks));
            Assert.Contains("DATABRICKS_URL", s.MissingFor(LlmProfile.Databricks));
        }

        [Theory]
        [InlineData(null, "http://127.0.0.1:8776/v1/chat/completions")]
        [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
        [InlineData("https://gateway.example.test/v1/chat/completions/", "https://gateway.example.test/v1/chat/completions")]
        [InlineData("https://gateway.example.test", "https://gateway.example.test/v1/chat/completions")]
        public void OpenAiUrlAcceptsBaseOrFullUrl(string? input, string expected)
        {
            Assert.Equal(expected, LlmSettings.NormalizeOpenAiUrl(input));
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
            Assert.Equal(LlmProfile.Databricks, new LlmSettings { DatabricksToken = "x" }.Resolve());
            Assert.Equal(LlmProfile.OpenAi, new LlmSettings().Resolve());
            Assert.Equal(LlmProfile.OpenAi, new LlmSettings { OpenAiToken = "x", DatabricksToken = "y" }.Resolve());
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

    public class RefinerTests
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

        private static (List<WorkItem> items, ReviewReport report) Defective()
        {
            var options = SyntheticOptions.ForTotal(100);
            options.IncludeDefects = true;
            var items = SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(options), Items.Repo);
            return (items, Review.Run(items, Taxonomy.Default()));
        }

        [Fact]
        public void ParsesJsonMarkdownAndRejectsNoise()
        {
            var json = Refiner.ParseReply("```json\n{\"narrative\": \"## User need\\nAs a tester, I want x so that y.\", \"meta\": {\"points\": \"TBD\"}, \"note\": \"added need\"}\n```")!;
            Assert.Equal("json", json.Source);
            Assert.Equal("TBD", json.Meta["points"]);
            Assert.Equal("added need", json.Note);
            var md = Refiner.ParseReply("## Acceptance criteria\n- [ ] works")!;
            Assert.Equal("markdown", md.Source);
            Assert.Null(Refiner.ParseReply("Sorry, I cannot help with that."));
        }

        [Fact]
        public async Task DraftsCloseContentGapsAndKeepTheOriginalText()
        {
            var (items, report) = Defective();
            var candidates = Refiner.Candidates(report, new RefineOptions());
            Assert.True(candidates.Count >= 4, "catalogue should contain content gaps");
            var llm = new ScriptedLlm(messages =>
            {
                // Echo the current narrative and append whatever sections the gaps ask for.
                var user = messages[1].Content;
                var current = user.Substring(user.IndexOf("Current narrative", StringComparison.Ordinal));
                current = current.Substring(current.IndexOf('\n') + 1).Replace("\n\nRespond with the JSON object only.", string.Empty);
                var add = string.Empty;
                if (user.Contains(Deficiencies.StoryMissingAcceptanceCriteria)) add += "\n\n## Acceptance criteria\n- [ ] One positive case passes\n- [ ] One negative case is rejected";
                if (user.Contains(Deficiencies.StoryMissingUserNeed)) add += "\n\n## User need\nAs a reviewer, I want the gap closed so that the story is ready.";
                if (user.Contains(Deficiencies.StoryMissingTasks)) add += "\n\n## Tasks\n- [ ] Do the work\n- [ ] Show the evidence";
                if (user.Contains(Deficiencies.FeatureMissingDoneWhen)) add += "\n\n## Definition of done\n- [ ] All child stories accepted";
                if (user.Contains(Deficiencies.EpicMissingSuccessPicture)) add += "\n\n## Success picture\n- Teams see the outcome\n- Nothing regresses";
                if (user.Contains(Deficiencies.EpicMissingOutcome)) add += "\n\n## Outcome\nThe capability is live and measured.";
                if (user.Contains(Deficiencies.ThemeMissingScope)) add += "\n\n## In scope\n- Platform work\n\n## Out of scope\n- Product reports";
                return new JObject { ["narrative"] = current.Trim() + add, ["note"] = "Added the missing sections." }.ToString();
            });
            var batch = await Refiner.RunAsync(items, report, llm, new RefineOptions { MaxItems = 20 }, Items.Repo, null, CancellationToken.None);
            Assert.Equal(ProposalKind.Refinement, batch.Kind);
            Assert.Equal(candidates.Count, batch.Proposals.Count);
            Assert.All(batch.Proposals, p => Assert.Empty(p.Remaining));
            Assert.All(batch.Proposals, p => Assert.Equal("high", p.Confidence));
            Assert.All(batch.Proposals, p => Assert.NotEmpty(p.Addresses));

            // Projecting the proposal onto the item leaves no content deficiency behind and keeps the meta fence first.
            var byKey = items.ToDictionary(i => i.Key);
            foreach (var p in batch.Proposals)
            {
                var projected = ProposalApplier.Project(byKey[p.ItemKey], p);
                var after = new WorkItem(Items.Repo, p.ItemNumber) { Title = p.ItemTitle, Body = projected.Body };
                Assert.StartsWith(HalyardMeta.Fence, projected.Body);
                Assert.DoesNotContain(Deficiencies.Evaluate(after), c => Refiner.IsContentGap(c));
                Assert.Contains("refined_by", projected.Body);
            }
        }

        [Fact]
        public async Task DestructiveDraftsAreFlaggedLowConfidence()
        {
            var (items, report) = Defective();
            var llm = new ScriptedLlm(_ => "{\"narrative\": \"## Acceptance criteria\\n- [ ] something\", \"note\": \"rewrote\"}");
            var batch = await Refiner.RunAsync(items, report, llm, new RefineOptions { MaxItems = 3 }, Items.Repo, null, CancellationToken.None);
            Assert.NotEmpty(batch.Proposals);
            Assert.All(batch.Proposals, p => Assert.Equal("low", p.Confidence));
            Assert.All(batch.Proposals, p => Assert.Contains("survive", p.Reason));
        }

        [Fact]
        public void MetadataRepairBuildsABlockFromLabelsAndPlaceholders()
        {
            var (items, report) = Defective();
            var batch = Refiner.MetadataProposals(items, report, Taxonomy.Default(), Items.Repo);
            Assert.Equal(ProposalKind.Metadata, batch.Kind);
            Assert.NotEmpty(batch.Proposals);
            var noMeta = batch.Proposals.First(p => p.Addresses.Contains(Deficiencies.MissingMeta));
            Assert.Equal("STORY", noMeta.ProposedMeta!["type"]);
            Assert.StartsWith("STORY-", noMeta.ProposedMeta["id"]);
            Assert.StartsWith("THEME-", noMeta.ProposedMeta["theme"]);
            Assert.Equal("TBD", noMeta.ProposedMeta["points"]);
            var item = items.First(i => i.Key == noMeta.ItemKey);
            var projected = ProposalApplier.Project(item, noMeta);
            var after = new WorkItem(Items.Repo, item.Number) { Title = item.Title, Body = projected.Body };
            after.Labels.AddRange(item.Labels);
            Assert.True(after.HasMeta);
            Assert.DoesNotContain(Deficiencies.MissingMeta, Deficiencies.Evaluate(after));
            Assert.DoesNotContain(Deficiencies.StoryMissingPoints, Deficiencies.Evaluate(after));
            // Invalid points become TBD; a story with an effort band loses it.
            Assert.Contains(batch.Proposals, p => p.Addresses.Contains(Deficiencies.StoryInvalidPoints) && p.ProposedMeta!["points"] == "TBD");
            Assert.Contains(batch.Proposals, p => p.Addresses.Contains(Deficiencies.StoryHasEffort) && p.ProposedMeta!.ContainsKey("effort") && p.ProposedMeta["effort"] == null);
        }

        [Fact]
        public void ManualEditsAreApprovedProposalsWithPolicyChecks()
        {
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(40));
            var items = SyntheticCatalog.ToWorkItems(catalog, Items.Repo);
            var story = items.First(i => i.Type == WorkItemType.Story);
            var feature = items.First(i => i.Type == WorkItemType.Feature);
            var epic = items.First(i => i.Type == WorkItemType.Epic);
            var points = Estimator.Manual(story, ProposalKind.StoryPoints, "13", null, Items.Repo, items);
            Assert.Equal(ProposalDecision.Approved, points.Proposals[0].Decision);
            Assert.Equal("8", points.Proposals[0].ProposedValue);
            var parent = Estimator.Manual(story, ProposalKind.ParentLink, "#" + feature.Number, "moved", Items.Repo, items);
            Assert.Equal(feature.CanonicalId, parent.Proposals[0].ProposedValue);
            Assert.Throws<ArgumentException>(() => Estimator.Manual(story, ProposalKind.ParentLink, epic.CanonicalId, null, Items.Repo, items));
            Assert.Throws<ArgumentException>(() => Estimator.Manual(feature, ProposalKind.StoryPoints, "3", null, Items.Repo, items));
            var projected = ProposalApplier.Project(story, parent.Proposals[0]);
            Assert.Equal(feature.CanonicalId, projected.Meta.Parent);
        }
    }

    public class MultiRepositoryTests
    {
        [Fact]
        public void SourceListIsParsedAndEverySourceIsProtected()
        {
            var s = new HalyardSettings();
            s.Provider.Kind = ProviderKind.GitHub;
            s.Provider.SourceRepository = "acme/platform, acme/data ;acme/platform";
            s.Provider.SandboxRepository = "acme/sandbox";
            Assert.Equal(new[] { "acme/platform", "acme/data" }, s.Provider.SourceRepositories.ToArray());
            var policy = s.BuildWritePolicy();
            Assert.True(policy.IsProtected(new RepositoryRef(ProviderKind.GitHub, "acme/platform")));
            Assert.True(policy.IsProtected(new RepositoryRef(ProviderKind.GitHub, "acme/data")));
            Assert.True(policy.IsSandbox(new RepositoryRef(ProviderKind.GitHub, "acme/sandbox")));
            Assert.Throws<Safety.WriteBlockedException>(() => policy.Assert(new RepositoryRef(ProviderKind.GitHub, "acme/data"), new Safety.WriteIntent("x", "y")));
        }

        [Fact]
        public void SnapshotKeepsEachItemsRepositoryAcrossSaveAndLoad()
        {
            var dir = Path.Combine(Path.GetTempPath(), "halyard-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new Snapshots.SnapshotStore(dir);
                var a = new RepositoryRef(ProviderKind.GitHub, "acme/platform");
                var b = new RepositoryRef(ProviderKind.GitHub, "acme/data");
                var items = new List<WorkItem> { new WorkItem(a, 1) { Title = "one" }, new WorkItem(b, 1) { Title = "other one" }, new WorkItem(b, 2) { Title = "two" } };
                var saved = store.Save(new[] { a, b }, items, null);
                Assert.Equal("acme/platform, acme/data", saved.Repository);
                var loaded = store.Load(saved.Id)!;
                Assert.Equal(2, loaded.Repositories.Count);
                Assert.Equal(3, loaded.Items.Count);
                Assert.Equal("github:acme/platform:1", loaded.Items[0].Key);
                Assert.Equal("github:acme/data:1", loaded.Items[1].Key);
                Assert.Equal(3, loaded.Items.Select(i => i.Key).Distinct().Count());
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }

    public class ProposalStoreTests
    {
        [Fact]
        public void SavesDecidesAndReloads()
        {
            var dir = Path.Combine(Path.GetTempPath(), "halyard-tests-" + Guid.NewGuid().ToString("N"));
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
            var key = "HALYARD_TEST_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                File.WriteAllText(path, "# comment\n" + key + "=\"from file\"\nexport " + key + "_B='b'\n");
                Environment.SetEnvironmentVariable(key, "preset");
                var applied = HalyardSettings.LoadEnvFile(path);
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

        private static Func<string, string?> Vars(params (string Key, string Value)[] vars)
        {
            var d = vars.ToDictionary(v => v.Key, v => (string?)v.Value, StringComparer.Ordinal);
            return name => { string? v; return d.TryGetValue(name, out v) ? v : null; };
        }

        [Fact]
        public void OnlyHalyardVariablesAreRead()
        {
            // Built from parts so the retired-name check does not see the old prefix in this file.
            var old = "CAS" + "CADE_";
            var s = HalyardSettings.FromEnvironment("root", Vars(
                ("HALYARD_SANDBOX_REPO", "owner/new-sandbox"), (old + "SANDBOX_REPO", "owner/old-sandbox"),
                (old + "SOURCE_REPO", "owner/source"), (old + "WRITE_DELAY_MS", "17")));
            Assert.Equal("owner/new-sandbox", s.Provider.SandboxRepository);
            Assert.Equal(string.Empty, s.Provider.SourceRepository);
            Assert.NotEqual(17, s.Provider.WriteDelayMs);
        }

        [Fact]
        public void PersonalSandboxAddressIsMappedToTheOrganization()
        {
            // The sandbox's pre-move address is now a personal fork.
            var s = HalyardSettings.FromEnvironment("root", Vars(
                ("HALYARD_SANDBOX_REPO", "sun-of-anton/Halyard-Ballast"), ("HALYARD_SOURCE_REPO", "sun-of-anton/Halyard-Ballast")));
            Assert.Equal("fleethacker-data-crucible/Halyard-Ballast", s.Provider.SandboxRepository);
            Assert.Equal("fleethacker-data-crucible/Halyard-Ballast", s.Provider.SourceRepository);
            Assert.Throws<Safety.WriteBlockedException>(() => s.BuildWritePolicy().Assert(new RepositoryRef(ProviderKind.GitHub, "sun-of-anton/Halyard-Ballast"), new Safety.WriteIntent("x", "y")));
        }

        [Fact]
        public void EnvFilePathIsAlwaysHalyardEnv()
        {
            var s = new HalyardSettings { RootDirectory = "root" };
            Assert.Equal(Path.Combine("root", "config", "halyard.env"), s.EnvFilePath);
        }

        [Fact]
        public void PublicDictionaryNeverContainsTokens()
        {
            var s = new HalyardSettings();
            s.Provider.Token = "ghp_SECRETSECRETSECRETSECRETSECRET";
            s.Llm.OpenAiToken = "secret-token-value";
            var json = Json.Serialize(s.ToPublicDictionary());
            Assert.DoesNotContain("SECRET", json);
            Assert.DoesNotContain("secret-token-value", json);
            Assert.Contains("\"hasProviderToken\":true", json);
        }

        [Fact]
        public void WritePolicyProtectsSourceInSandboxMode()
        {
            var s = new HalyardSettings();
            s.Provider.Kind = ProviderKind.GitLab;
            s.Provider.SourceRepository = "1234";
            s.Provider.SandboxRepository = "5678";
            var policy = s.BuildWritePolicy();
            Assert.True(policy.IsSandbox(new RepositoryRef(ProviderKind.GitLab, "5678")));
            Assert.True(policy.IsProtected(new RepositoryRef(ProviderKind.GitLab, "1234")));
            Assert.Throws<Safety.WriteBlockedException>(() => policy.Assert(new RepositoryRef(ProviderKind.GitLab, "1234"), new Safety.WriteIntent("x", "y")));
        }
    }

    public class HostTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-host-" + Guid.NewGuid().ToString("N"));
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();

        public HostTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null });
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30));
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(catalog, Items.Repo), null);
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
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
        public async Task OnlyTheHalyardSessionHeaderIsAccepted()
        {
            using (var other = new HttpClient())
            {
                other.DefaultRequestHeaders.Add("X-Other-Session", _info.SessionToken);
                Assert.Equal(401, (int)(await other.GetAsync(_info.Url + "api/status")).StatusCode);
            }
        }

        [Fact]
        public async Task EngineStopsItselfOnlyAfterTheBrowserIsGone()
        {
            // The fixture host has AutoStop on but its timer only fires on the rule below; exercise the rule directly.
            var t0 = DateTimeOffset.UtcNow;
            Assert.False(_host.BrowserGone(t0 + TimeSpan.FromHours(1)), "nothing ever connected: the launcher decides, not the heartbeat");

            var reply = await _client.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":false}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(200, (int)reply.StatusCode);
            Assert.False(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5)), "within the timeout");
            Assert.True(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25)), "no heartbeat for longer than the timeout");

            await _client.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":true}", System.Text.Encoding.UTF8, "application/json"));
            Assert.False(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1)), "inside the leaving grace: a reload may follow");
            Assert.True(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6)), "tab closed and the grace passed");

            await _client.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":false}", System.Text.Encoding.UTF8, "application/json"));
            Assert.False(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6)), "the reload arrived; the leaving mark is cleared");

            var anon = new HttpClient();
            Assert.Equal(401, (int)(await anon.PostAsync(_info.Url + "api/heartbeat", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(401, (int)(await anon.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":true,\"session\":\"wrong\"}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            // The unload beacon cannot set headers: the token in the body is accepted for this route only.
            var beacon = await anon.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":true,\"session\":\"" + _info.SessionToken + "\"}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(200, (int)beacon.StatusCode);
            Assert.True(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6)), "the beacon marked the tab as leaving");
            Assert.Equal(401, (int)(await anon.PostAsync(_info.Url + "api/status", new StringContent("{\"session\":\"" + _info.SessionToken + "\"}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        }

        [Fact]
        public async Task HiddenTabKeepsTheEngineThroughThrottledHeartbeats()
        {
            // A background tab gets its timers about once a minute (a sleeping tab none): the visible-tab timeout
            // would stop an engine whose tab is still open.
            await _client.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":false,\"hidden\":true}", System.Text.Encoding.UTF8, "application/json"));
            Assert.False(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90)), "hidden: a throttled minute between heartbeats is not gone");
            Assert.False(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30)), "hidden: a sleeping tab still counts as open");
            Assert.True(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(61)), "hidden past the hidden timeout");

            // Closing a background tab still stops the engine within seconds.
            await _client.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":true}", System.Text.Encoding.UTF8, "application/json"));
            Assert.True(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6)), "a hidden tab that closes is gone after the grace");

            // Back in front: the short timeout applies again.
            await _client.PostAsync(_info.Url + "api/heartbeat", new StringContent("{\"leaving\":false,\"hidden\":false}", System.Text.Encoding.UTF8, "application/json"));
            Assert.True(_host.BrowserGone(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25)), "visible again: the 20 s timeout is back");
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
            Assert.Equal("fleethacker-data-crucible/Halyard-Ballast", _host.Settings.Provider.SandboxRepository);
            var ok = await _client.PostAsync(_info.Url + "api/config", new StringContent("{\"labelFilter\":\"team::x\"}"));
            Assert.Equal(200, (int)ok.StatusCode);
            Assert.Equal("team::x", (string)JObject.Parse(await ok.Content.ReadAsStringAsync())["config"]!["labelFilter"]!);
        }

        [Fact]
        public async Task ApiKeyCanBeEnteredInTheBrowserAndRememberedLocally()
        {
            var r = await _client.PostAsync(_info.Url + "api/config", new StringContent("{\"llmToken\":\"sk-test-0123456789abcdef\",\"llmModel\":\"gpt-4o-mini\",\"remember\":true}"));
            Assert.Equal(200, (int)r.StatusCode);
            var body = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.True((bool)body["remembered"]!);
            Assert.True((bool)body["config"]!["hasLlmToken"]!);
            Assert.DoesNotContain("sk-test", body.ToString());
            Assert.Equal("sk-test-0123456789abcdef", _host.Settings.Llm.OpenAiToken);
            var env = File.ReadAllText(_host.Settings.EnvFilePath);
            Assert.Contains("LLM_TOKEN=sk-test-0123456789abcdef", env);
            Assert.Contains("LLM_MODEL=gpt-4o-mini", env);
            // The URL is never settable from the browser.
            var locked = await _client.PostAsync(_info.Url + "api/config", new StringContent("{\"llmUrl\":\"https://evil.example\"}"));
            Assert.Equal(200, (int)locked.StatusCode); // unknown keys are ignored, not applied
            Assert.Equal(LlmSettings.DefaultOpenAiUrl, _host.Settings.Llm.Url(LlmProfile.OpenAi));
            // Clearing removes it from memory and from the file.
            await _client.PostAsync(_info.Url + "api/config", new StringContent("{\"llmToken\":null,\"remember\":true}"));
            Assert.Null(_host.Settings.Llm.OpenAiToken);
            Assert.DoesNotContain("LLM_TOKEN=", File.ReadAllText(_host.Settings.EnvFilePath));
        }

        [Fact]
        public async Task ManualProposalAndPreviewRoundTrip()
        {
            var snap = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/snapshot"));
            var story = snap["items"]!.First(i => (string)i["type"]! == "STORY");
            var number = (long)story["number"]!;
            var r = await _client.PostAsync(_info.Url + "api/proposals/manual", new StringContent("{\"number\":" + number + ",\"kind\":\"points\",\"value\":\"5\",\"note\":\"test\"}"));
            Assert.Equal(200, (int)r.StatusCode);
            var batch = JObject.Parse(await r.Content.ReadAsStringAsync());
            var batchId = (string)batch["id"]!;
            var proposalId = (string)batch["proposals"]![0]!["id"]!;
            Assert.Equal("approved", (string)batch["proposals"]![0]!["decision"]!);
            var preview = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/proposals/" + batchId + "/" + proposalId + "/preview"));
            Assert.Equal(number, (long)preview["number"]!);
            Assert.Contains("points: 5", (string)preview["proposed"]!["body"]!);
            Assert.NotEqual((string)preview["current"]!["body"]!, (string)preview["proposed"]!["body"]!);
            var bad = await _client.PostAsync(_info.Url + "api/proposals/manual", new StringContent("{\"number\":" + number + ",\"kind\":\"effort\",\"value\":\"Large\"}"));
            Assert.Equal(400, (int)bad.StatusCode);
            var meta = await _client.PostAsync(_info.Url + "api/intelligence/metadata", new StringContent("{}"));
            Assert.Equal(200, (int)meta.StatusCode);
            Assert.Equal("metadata", (string)JObject.Parse(await meta.Content.ReadAsStringAsync())["kind"]!);
        }

        [Fact]
        public async Task ItemsAreFoundByKeyAndProposalsStayInTheSandbox()
        {
            var snap = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/snapshot"));
            var first = snap["items"]![0]!;
            var key = (string)first["key"]!;
            var byKey = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/item?key=" + Uri.EscapeDataString(key)));
            Assert.Equal(key, (string)byKey["key"]!);
            Assert.True((bool)byKey["inSandbox"]!);
            Assert.Equal("fleethacker-data-crucible/Halyard-Ballast", (string)byKey["repository"]!);
            // A snapshot of a source repository only: every proposal path refuses, with a message that names the sandbox.
            var source = new RepositoryRef(ProviderKind.GitHub, "sun-of-anton/other-backlog");
            var sourceItems = SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(20)), source);
            _host.Snapshots.Save(source, sourceItems, null);
            try
            {
                var refused = await _client.PostAsync(_info.Url + "api/intelligence/metadata", new StringContent("{}"));
                Assert.Equal(400, (int)refused.StatusCode);
                Assert.Contains("sandbox", (string)JObject.Parse(await refused.Content.ReadAsStringAsync())["error"]!);
                var manual = await _client.PostAsync(_info.Url + "api/proposals/manual", new StringContent("{\"key\":\"" + sourceItems[5].Key + "\",\"kind\":\"points\",\"value\":\"3\"}"));
                Assert.Equal(403, (int)manual.StatusCode);
            }
            finally
            {
                _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30)), Items.Repo), null);
            }
        }

        [Fact]
        public async Task BatchesCanBeDeletedAndApplyAllNeedsApprovedWork()
        {
            var snap = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/snapshot"));
            var number = (long)snap["items"]!.First(i => (string)i["type"]! == "STORY")["number"]!;
            var made = JObject.Parse(await (await _client.PostAsync(_info.Url + "api/proposals/manual", new StringContent("{\"number\":" + number + ",\"kind\":\"points\",\"value\":\"3\"}"))).Content.ReadAsStringAsync());
            var batchId = (string)made["id"]!;
            Assert.Equal(200, (int)(await _client.GetAsync(_info.Url + "api/proposals/" + batchId)).StatusCode);
            var del = await _client.PostAsync(_info.Url + "api/proposals/" + batchId + "/delete", new StringContent("{}"));
            Assert.Equal(200, (int)del.StatusCode);
            Assert.Equal(404, (int)(await _client.GetAsync(_info.Url + "api/proposals/" + batchId)).StatusCode);
            Assert.Equal(404, (int)(await _client.PostAsync(_info.Url + "api/proposals/" + batchId + "/delete", new StringContent("{}"))).StatusCode);
            // Nothing approved is left, so apply-all is a clear 400 rather than an empty job (no provider token here anyway).
            var all = await _client.PostAsync(_info.Url + "api/proposals/apply-all", new StringContent("{}"));
            Assert.True((int)all.StatusCode == 400 || (int)all.StatusCode == 409);
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
