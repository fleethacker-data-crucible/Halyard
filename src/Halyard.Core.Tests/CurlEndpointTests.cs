using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Llm;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    public class CurlCommandTests
    {
        // The two shapes the owner's gateway documents, with placeholder hosts.
        private const string ResponsesCurl = "curl https://gateway.test/serving-endpoints/responses -H \"Content-Type: application/json\" -H \"Authorization: Bearer $AI_ENDPOINT_TOKEN\" -d '{\"model\":\"system.ai.example-4-6\",\"max_output_tokens\":256,\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Hello!\"}]},{\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hi\"}]}]}'";
        private const string ChatCurl = "curl -X POST https://gateway.test/v1/chat/completions -H \"Authorization: Bearer <your-token>\" -d '{\"model\":\"example-flash-lite-withtools\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}'";

        [Fact]
        public void ReadsTheResponsesApiShape()
        {
            var e = CurlCommand.Parse(ResponsesCurl);
            Assert.Equal("https://gateway.test/serving-endpoints/responses", e.Url);
            Assert.Equal(LlmWireFormat.Responses, e.Format);
            Assert.Equal("system.ai.example-4-6", e.Model);
            Assert.Equal("POST", e.Method);
            Assert.Equal(256, e.BodyMaxTokens);
            Assert.Empty(e.Headers);
            Assert.False(e.HadLiteralToken);
            Assert.Empty(e.Warnings);
        }

        [Fact]
        public void ReadsTheChatCompletionsShape()
        {
            var e = CurlCommand.Parse(ChatCurl);
            Assert.Equal("https://gateway.test/v1/chat/completions", e.Url);
            Assert.Equal(LlmWireFormat.Chat, e.Format);
            Assert.Equal("example-flash-lite-withtools", e.Model);
            Assert.False(e.HadLiteralToken);
        }

        [Fact]
        public void DatabricksInvocationsUrlIsDetected()
        {
            var e = CurlCommand.Parse("curl https://ws.gateway.test/serving-endpoints/my-model/invocations -H 'Authorization: Bearer ${AI_ENDPOINT_TOKEN}' -d '{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}'");
            Assert.Equal(LlmWireFormat.Databricks, e.Format);
            Assert.Equal(string.Empty, e.Model); // the URL names the model
        }

        [Fact]
        public void BashLineContinuationsAndDoubleQuotedBody()
        {
            var text = "curl \\\n  --url https://gateway.test/v1/responses \\\n  --header 'Content-Type: application/json' \\\r\n  --header \"Authorization: Bearer ${AI_ENDPOINT_TOKEN:-unset}\" \\\n  --data \"{\\\"model\\\": \\\"m-1\\\", \\\"input\\\": \\\"hi\\\"}\"";
            var e = CurlCommand.Parse(text);
            Assert.Equal("https://gateway.test/v1/responses", e.Url);
            Assert.Equal("m-1", e.Model);
            Assert.Equal(LlmWireFormat.Responses, e.Format);
            Assert.False(e.HadLiteralToken);
        }

        [Fact]
        public void CmdAndPowerShellContinuationsAndAttachedOptions()
        {
            var cmd = "curl.exe -sS -XPOST ^\r\n https://gateway.test/v1/chat/completions ^\r\n -H \"X-Request-Source: halyard-test\" ^\r\n --data-raw \"{\\\"model\\\":\\\"m\\\",\\\"messages\\\":[]}\"";
            var e = CurlCommand.Parse(cmd);
            Assert.Equal("https://gateway.test/v1/chat/completions", e.Url);
            Assert.Equal("halyard-test", e.Headers["X-Request-Source"]);
            var ps = "curl.exe `\n  https://gateway.test/v1/chat/completions `\n  -d '{\"model\":\"m\",\"messages\":[]}'";
            Assert.Equal("m", CurlCommand.Parse(ps).Model);
        }

        [Fact]
        public void SingleQuotesKeepEverythingLiteral()
        {
            var args = CurlCommand.Tokenize("curl 'a \"b\" \\n $X' \"c\\\"d\" e\\ f");
            Assert.Equal(new[] { "curl", "a \"b\" \\n $X", "c\"d", "e f" }, args.ToArray());
        }

        [Fact]
        public void KeepsOnlyNonSecretHeadersHalyardCanSend()
        {
            var e = CurlCommand.Parse("curl https://gateway.test/v1/chat/completions -H 'Content-Type: application/json' -H 'Accept: application/json' -H 'X-Team: delivery' -H 'api-key: $AI_ENDPOINT_TOKEN' -H 'X-Trace: ${TRACE_ID}' -d '{\"model\":\"m\",\"messages\":[]}'");
            Assert.Equal(new[] { "X-Team" }, e.Headers.Keys.ToArray());
            Assert.Contains(e.IgnoredHeaders, h => h.StartsWith("Accept "));
            Assert.Contains(e.IgnoredHeaders, h => h.StartsWith("api-key "));
            Assert.Contains(e.IgnoredHeaders, h => h.StartsWith("X-Trace "));
            Assert.False(e.HadLiteralToken);
        }

        [Fact]
        public void LiteralBearerTokenIsDroppedAndReported()
        {
            var e = CurlCommand.Parse("curl https://gateway.test/v1/chat/completions -H 'Authorization: Bearer abcdef0123456789literal' -d '{\"model\":\"m\",\"messages\":[]}'");
            Assert.True(e.HadLiteralToken);
            Assert.Contains(CurlCommand.LiteralTokenWarning, e.Warnings);
            Assert.Empty(e.Headers);
            Assert.DoesNotContain("abcdef0123456789literal", string.Join(" ", e.Warnings.Concat(e.IgnoredHeaders)) + e.Url + e.Model);
            // A literal key in another header and -u credentials count too.
            Assert.True(CurlCommand.Parse("curl https://gateway.test/v1/chat/completions -H 'x-api-key: abcdef0123' -d '{\"model\":\"m\",\"messages\":[]}'").HadLiteralToken);
            Assert.True(CurlCommand.Parse("curl -u me:secret https://gateway.test/v1/chat/completions -d '{\"model\":\"m\",\"messages\":[]}'").HadLiteralToken);
        }

        [Theory]
        [InlineData("curl $AI_ENDPOINT_URL -d '{\"model\":\"m\",\"input\":\"x\"}'", "$AI_ENDPOINT_URL")]
        [InlineData("curl \"${AI_ENDPOINT_URL:-https://gateway.test}/v1/responses\" -d '{\"model\":\"m\",\"input\":\"x\"}'", "${AI_ENDPOINT_URL:-https://gateway.test}")]
        [InlineData("curl <OPENAI_RESPONSES_URL> -d '{\"model\":\"m\",\"input\":\"x\"}'", "<OPENAI_RESPONSES_URL>")]
        [InlineData("curl https://<workspace>.gateway.test/serving-endpoints/m/invocations -d '{\"messages\":[]}'", "<workspace>")]
        [InlineData("curl https://gateway.test/{{model}}/responses -d '{\"model\":\"m\",\"input\":\"x\"}'", "{{model}}")]
        public void PlaceholdersInTheUrlAreAnError(string text, string placeholder)
        {
            var ex = Assert.Throws<CurlParseException>(() => CurlCommand.Parse(text));
            Assert.Contains("placeholder " + placeholder, ex.Message);
        }

        [Theory]
        [InlineData("curl http://gateway.test/v1/chat/completions -d '{\"model\":\"m\",\"messages\":[]}'", "https://")]
        [InlineData("curl https://me:pw@gateway.test/v1/chat/completions -d '{\"model\":\"m\",\"messages\":[]}'", "user name or password")]
        [InlineData("curl ftp://gateway.test/x -d '{\"model\":\"m\",\"messages\":[]}'", "https://")]
        [InlineData("curl 'https://gateway.test/v1/chat/completions?api-key=abc' -d '{\"model\":\"m\",\"messages\":[]}'", "query string")]
        [InlineData("curl https://gateway.test/v1/chat/completions -d '{\"messages\":[]}'", "no model")]
        [InlineData("curl https://gateway.test/v1/chat/completions -d '{\"model\":\"$MODEL\",\"messages\":[]}'", "placeholder")]
        [InlineData("curl https://gateway.test/v1/chat/completions -d '{\"model\":\"m\",\"messages\":[' ", "not valid JSON")]
        [InlineData("curl https://gateway.test/v1/thing -d '{\"model\":\"m\"}'", "Could not tell the API format")]
        [InlineData("curl https://gateway.test/v1/chat/completions -d @body.json", "file")]
        [InlineData("curl https://gateway.test/v1/chat/completions -d '{\"model\":\"m\",\"messages\":[]}", "single quote")]
        [InlineData("curl -H 'X: y'", "no URL")]
        [InlineData("   ", "Paste a curl command")]
        public void UnusableCommandsAreRefusedWithAReason(string text, string reason)
        {
            var ex = Assert.Throws<CurlParseException>(() => CurlCommand.Parse(text));
            Assert.Contains(reason, ex.Message);
        }

        [Fact]
        public void PlainHttpIsAcceptedForThisMachineOnly()
        {
            Assert.Equal("http://127.0.0.1:8790/v1/chat/completions", CurlCommand.Parse("curl http://127.0.0.1:8790/v1/chat/completions -d '{\"model\":\"m\",\"messages\":[]}'").Url);
            Assert.Equal(LlmWireFormat.Responses, CurlCommand.Parse("curl http://localhost:8790/v1/responses -d '{\"model\":\"m\",\"input\":\"x\"}'").Format);
        }

        [Fact]
        public void IgnoresTransportFlagsButSaysWhatItWillNotDo()
        {
            var e = CurlCommand.Parse("curl -sSLk --max-time 30 -o out.json https://gateway.test/v1/responses -d '{\"model\":\"m\",\"input\":\"x\",\"stream\":true}'");
            Assert.Equal("https://gateway.test/v1/responses", e.Url);
            Assert.Contains(e.Warnings, w => w.Contains("redirects"));
            Assert.Contains(e.Warnings, w => w.Contains("TLS"));
            Assert.Contains(e.Warnings, w => w.Contains("streaming"));
        }

        [Fact]
        public void OriginComparesSchemeHostAndPort()
        {
            Assert.Equal(CurlCommand.Origin("https://gateway.test/a"), CurlCommand.Origin("https://GATEWAY.test:443/b"));
            Assert.NotEqual(CurlCommand.Origin("https://gateway.test/a"), CurlCommand.Origin("https://other.gateway.test/a"));
        }
    }

    public class ResponsesApiTests
    {
        [Fact]
        public void BuildsTheResponsesRequest()
        {
            var body = ResponsesApi.BuildRequest(new[] { LlmMessage.System("Be brief."), LlmMessage.User("Hello!"), new LlmMessage("assistant", "Hi") }, "m-1", 256);
            Assert.Equal("{\"model\":\"m-1\",\"input\":[{\"role\":\"system\",\"content\":[{\"type\":\"input_text\",\"text\":\"Be brief.\"}]},{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Hello!\"}]},{\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hi\"}]}],\"max_output_tokens\":256}", body.ToString(Formatting.None));
            Assert.Null(body["stream"]);
            Assert.Null(body["temperature"]);
        }

        [Fact]
        public void ReadsOutputTextItemsAndSkipsReasoning()
        {
            var r = JToken.Parse("{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"summary\":[]},{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hel\"},{\"type\":\"refusal\",\"refusal\":\"no\"},{\"type\":\"output_text\",\"text\":\"lo\"}]}]}");
            Assert.Equal("Hello", ResponsesApi.Text(r));
            Assert.Equal("top", ResponsesApi.Text(JToken.Parse("{\"output\":[],\"output_text\":\"top\"}")));
            Assert.Null(ResponsesApi.Text(JToken.Parse("{\"output\":[]}")));
        }

        [Fact]
        public async Task CustomResponsesProfileCallsTheExactUrlWithoutStreaming()
        {
            var http = new FakeTransport().On("POST", "/serving-endpoints/responses", 200, "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\" OK \"}]}],\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}");
            var settings = new LlmSettings { Profile = LlmProfile.Custom, CustomUrl = "https://gateway.test/serving-endpoints/responses", CustomModel = "system.ai.example-4-6", CustomFormat = LlmWireFormat.Responses, CustomToken = "tok-0123456789" };
            settings.CustomHeaders["X-Team"] = "delivery";
            var client = new LlmClient(settings, http);
            var r = await client.TestAsync(CancellationToken.None);
            Assert.Equal("OK", r.Text);
            Assert.Equal(12, r.PromptTokens);
            Assert.Equal(1, r.CompletionTokens);
            var call = http.Calls.Single();
            Assert.Equal("https://gateway.test/serving-endpoints/responses", call.Url);
            Assert.Equal("Bearer tok-0123456789", call.Headers["Authorization"]);
            Assert.Equal("delivery", call.Headers["X-Team"]);
            var body = JObject.Parse(call.JsonBody!);
            Assert.Equal("system.ai.example-4-6", (string)body["model"]!);
            Assert.Equal(4000, (int)body["max_output_tokens"]!);
            Assert.Equal("input_text", (string)body["input"]![0]!["content"]![0]!["type"]!);
            Assert.Null(body["stream"]);
            Assert.Null(body["messages"]);
        }

        [Fact]
        public async Task CustomResponsesProfileDropsMaxOutputTokensWhenRejected()
        {
            var http = new FakeTransport().On("POST", "/responses", call =>
            {
                var body = JObject.Parse(call.JsonBody!);
                if (body["max_output_tokens"] != null)
                    return new HttpReply(400, "{\"error\":{\"message\":\"Unsupported parameter: max_output_tokens\",\"param\":\"max_output_tokens\"}}", new Dictionary<string, string>());
                return new HttpReply(200, "{\"output_text\":\"OK\"}", new Dictionary<string, string>());
            });
            var settings = new LlmSettings { Profile = LlmProfile.Custom, CustomUrl = "https://gateway.test/v1/responses", CustomModel = "m", CustomFormat = LlmWireFormat.Responses, CustomToken = "tok-0123456789" };
            Assert.Equal("OK", (await new LlmClient(settings, http).TestAsync(CancellationToken.None)).Text);
            Assert.Equal(2, http.Calls.Count);
        }

        [Fact]
        public async Task CustomChatAndDatabricksFormatsKeepTheirBodies()
        {
            var http = new FakeTransport().On("POST", "gateway.test", 200, "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
            var chat = new LlmSettings { Profile = LlmProfile.Custom, CustomUrl = "https://gateway.test/custom/path", CustomModel = "m", CustomFormat = LlmWireFormat.Chat, CustomToken = "tok-0123456789" };
            await new LlmClient(chat, http).TestAsync(CancellationToken.None);
            var chatBody = JObject.Parse(http.Calls.Last().JsonBody!);
            Assert.Equal("https://gateway.test/custom/path", http.Calls.Last().Url); // exact URL, nothing appended
            Assert.False((bool)chatBody["stream"]!);
            Assert.NotNull(chatBody["max_completion_tokens"]);
            var db = new LlmSettings { Profile = LlmProfile.Custom, CustomUrl = "https://ws.gateway.test/serving-endpoints/m/invocations", CustomFormat = LlmWireFormat.Databricks, CustomToken = "tok-0123456789" };
            await new LlmClient(db, http).TestAsync(CancellationToken.None);
            var dbBody = JObject.Parse(http.Calls.Last().JsonBody!);
            Assert.Null(dbBody["stream"]);
            Assert.Null(dbBody["model"]);
            Assert.NotNull(dbBody["max_tokens"]);
        }

        [Fact]
        public void CustomProfileNeedsUrlModelAndToken()
        {
            var s = new LlmSettings();
            Assert.Contains("curl", s.MissingFor(LlmProfile.Custom));
            s.CustomUrl = "http://gateway.test/v1/responses";
            Assert.Contains("https://", s.MissingFor(LlmProfile.Custom));
            s.CustomUrl = "https://gateway.test/v1/responses";
            s.CustomFormat = LlmWireFormat.Responses;
            Assert.Contains("model", s.MissingFor(LlmProfile.Custom));
            s.CustomModel = "m";
            Assert.Contains("token", s.MissingFor(LlmProfile.Custom));
            s.CustomToken = "tok-0123456789";
            Assert.Null(s.MissingFor(LlmProfile.Custom));
            Assert.Equal(LlmProfile.Custom, s.Resolve());
            Assert.Equal(LlmProfile.Custom, LlmSettings.ParseProfile("custom"));
            Assert.Equal(LlmWireFormat.Responses, LlmSettings.ParseFormat("responses"));
        }

        [Fact]
        public void EnvironmentReadsTheCustomEndpoint()
        {
            var env = new Dictionary<string, string>
            {
                { "HALYARD_LLM_PROFILE", "custom" },
                { "HALYARD_LLM_CUSTOM_URL", "https://gateway.test/v1/responses" },
                { "HALYARD_LLM_CUSTOM_MODEL", "m" },
                { "HALYARD_LLM_CUSTOM_FORMAT", "responses" },
                { "HALYARD_LLM_CUSTOM_HEADERS", "{\"X-Team\":\"delivery\"}" },
                { "HALYARD_LLM_CUSTOM_TOKEN", "dpapi:not-a-valid-blob" },
            };
            var s = HalyardSettings.FromEnvironment("root", k => env.ContainsKey(k) ? env[k] : null);
            Assert.Equal(LlmProfile.Custom, s.Llm.Profile);
            Assert.Equal(LlmWireFormat.Responses, s.Llm.CustomFormat);
            Assert.Equal("delivery", s.Llm.CustomHeaders["X-Team"]);
            Assert.Null(s.Llm.CustomToken); // unreadable protected value: no token, never the raw text
            Assert.True(s.CustomTokenRemembered);
            var json = Json.Serialize(s.ToPublicDictionary());
            Assert.DoesNotContain("dpapi", json);
        }

        [Fact]
        public void DpapiRoundTripsWhereAvailable()
        {
            if (!SecretProtector.IsAvailable) return; // Windows only; Linux CI has no DPAPI
            var stored = SecretProtector.Protect("tok-0123456789");
            Assert.StartsWith(SecretProtector.Prefix, stored);
            Assert.DoesNotContain("tok-0123456789", stored);
            Assert.Equal("tok-0123456789", SecretProtector.Unprotect(stored));
        }
    }

    public class CurlRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-curl-" + Guid.NewGuid().ToString("N"));
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();
        private readonly FakeTransport _http = new FakeTransport();

        public CurlRouteTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Llm.OpenAiToken = "sk-env-0123456789abcdef";
            settings.Llm.TranscriptDirectory = null;
            _http.On("POST", "gateway.test", 200, "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}");
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null, Transport = _http });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        private async Task<HttpResponseMessage> Post(string path, JObject body)
        {
            return await _client.PostAsync(_info.Url + path, new StringContent(body.ToString(Formatting.None)));
        }

        [Fact]
        public async Task CurlPlusTokenSetsTheEndpointAndTestUsesIt()
        {
            var curl = "curl https://gateway.test/serving-endpoints/responses -H \"Authorization: Bearer $AI_ENDPOINT_TOKEN\" -d '{\"model\":\"m-1\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Hello!\"}]}]}'";
            var r = await Post("api/llm/curl", new JObject { { "curl", curl }, { "token", "tok-browser-0123456789" } });
            Assert.Equal(200, (int)r.StatusCode);
            var text = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("tok-browser", text);
            Assert.DoesNotContain("sk-env", text);
            var body = JObject.Parse(text);
            Assert.Equal("responses", (string)body["format"]!);
            Assert.True((bool)body["tokenSet"]!);
            Assert.False((bool)body["remembered"]!);
            Assert.Equal("custom", (string)body["config"]!["llmProfile"]!);
            Assert.True((bool)body["config"]!["llmCustom"]!["tokenSet"]!);

            var test = JObject.Parse(await (await Post("api/llm/test", new JObject { { "profile", "custom" } })).Content.ReadAsStringAsync());
            Assert.True((bool)test["ok"]!);
            Assert.Equal("OK", (string)test["text"]!);
            var call = _http.Calls.Last();
            Assert.Equal("https://gateway.test/serving-endpoints/responses", call.Url);
            Assert.Equal("Bearer tok-browser-0123456789", call.Headers["Authorization"]);
            // The env-configured OpenAI key is untouched and never goes to the curl endpoint.
            Assert.Equal("sk-env-0123456789abcdef", _host.Settings.Llm.OpenAiToken);
            Assert.Equal(LlmSettings.DefaultOpenAiUrl, _host.Settings.Llm.Url(LlmProfile.OpenAi));
            Assert.DoesNotContain(_http.Calls, c => c.Headers.ContainsKey("Authorization") && c.Headers["Authorization"].Contains("sk-env"));
            // Nothing secret reached the engine log.
            var log = string.Join("\n", _host.Log.Recent(500, null).Select(e => Json.Serialize(e)));
            Assert.DoesNotContain("tok-browser", log);
        }

        [Fact]
        public async Task TokenIsRequiredForANewOriginButKeptForTheSameOne()
        {
            var first = "curl https://gateway.test/v1/responses -d '{\"model\":\"m\",\"input\":\"x\"}'";
            var noToken = await Post("api/llm/curl", new JObject { { "curl", first } });
            Assert.Equal(400, (int)noToken.StatusCode);
            Assert.Contains("token field", await noToken.Content.ReadAsStringAsync());
            Assert.Equal(200, (int)(await Post("api/llm/curl", new JObject { { "curl", first }, { "token", "tok-browser-0123456789" } })).StatusCode);
            // Same origin, other model: the token stays.
            Assert.Equal(200, (int)(await Post("api/llm/curl", new JObject { { "curl", "curl https://gateway.test/v2/responses -d '{\"model\":\"m2\",\"input\":\"x\"}'" } })).StatusCode);
            Assert.Equal("m2", _host.Settings.Llm.CustomModel);
            // Another host: the token must be entered again.
            var other = await Post("api/llm/curl", new JObject { { "curl", "curl https://other.gateway.test/v1/responses -d '{\"model\":\"m\",\"input\":\"x\"}'" } });
            Assert.Equal(400, (int)other.StatusCode);
            Assert.Equal("https://gateway.test/v2/responses", _host.Settings.Llm.CustomUrl);
        }

        [Fact]
        public async Task LiteralTokenInTheCurlIsNeverStoredOrReturned()
        {
            var curl = "curl https://gateway.test/v1/chat/completions -H 'Authorization: Bearer literal-secret-0123456789' -d '{\"model\":\"m\",\"messages\":[]}'";
            var refused = await Post("api/llm/curl", new JObject { { "curl", curl } });
            Assert.Equal(400, (int)refused.StatusCode);
            Assert.DoesNotContain("literal-secret", await refused.Content.ReadAsStringAsync());
            Assert.Null(_host.Settings.Llm.CustomToken);
            var r = await Post("api/llm/curl", new JObject { { "curl", curl }, { "token", "tok-browser-0123456789" }, { "remember", true } });
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(200, (int)r.StatusCode);
            Assert.DoesNotContain("literal-secret", text);
            Assert.True((bool)JObject.Parse(text)["literalTokenIgnored"]!);
            Assert.Equal("tok-browser-0123456789", _host.Settings.Llm.CustomToken);
            var env = File.ReadAllText(_host.Settings.EnvFilePath);
            Assert.DoesNotContain("literal-secret", env);
            Assert.DoesNotContain("tok-browser", env); // DPAPI-protected on Windows, not written at all elsewhere
            Assert.Contains("HALYARD_LLM_CUSTOM_URL=https://gateway.test/v1/chat/completions", env);
            Assert.Contains("HALYARD_LLM_CUSTOM_FORMAT=chat", env);
            Assert.Contains("HALYARD_LLM_PROFILE=custom", env);
            Assert.Equal(SecretProtector.IsAvailable, env.Contains("HALYARD_LLM_CUSTOM_TOKEN=dpapi:"));
        }

        [Fact]
        public async Task PlaceholderUrlIsABadRequestWithTheReason()
        {
            var r = await Post("api/llm/curl", new JObject { { "curl", "curl $AI_ENDPOINT_URL -d '{\"model\":\"m\",\"input\":\"x\"}'" }, { "token", "tok-browser-0123456789" } });
            Assert.Equal(400, (int)r.StatusCode);
            Assert.Contains("placeholder $AI_ENDPOINT_URL", await r.Content.ReadAsStringAsync());
            Assert.Null(_host.Settings.Llm.CustomUrl);
        }

        public void Dispose()
        {
            _host.Dispose();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}
