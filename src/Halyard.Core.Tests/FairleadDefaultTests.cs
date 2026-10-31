using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Llm;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>The model endpoint defaults to the Fairlead router on this machine; no external endpoint is built in. Fake transport only.</summary>
    public class FairleadDefaultTests
    {
        private const string Reply = "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}";

        [Fact]
        public void TheDefaultEndpointIsTheLocalRouter()
        {
            Assert.Equal("http://127.0.0.1:8776/v1/chat/completions", LlmSettings.DefaultOpenAiUrl);
            Assert.Equal(LlmSettings.DefaultOpenAiUrl, new LlmSettings().Url(LlmProfile.OpenAi));
            Assert.Equal(LlmSettings.DefaultOpenAiUrl, LlmSettings.NormalizeOpenAiUrl(null));
        }

        [Fact]
        public async Task WithNothingConfiguredTheRequestGoesToFairleadWithTheClientKey()
        {
            var http = new FakeTransport().On("POST", "127.0.0.1:8776", 200, Reply);
            var client = new LlmClient(new LlmSettings { Profile = LlmProfile.OpenAi, OpenAiToken = "halyard-client-key-0123456789" }, http);
            Assert.Equal("OK", (await client.TestAsync(CancellationToken.None)).Text);
            var call = Assert.Single(http.Calls);
            Assert.Equal("http://127.0.0.1:8776/v1/chat/completions", call.Url);
            Assert.Equal("Bearer halyard-client-key-0123456789", call.Headers["Authorization"]);
            Assert.False(call.Headers.ContainsKey("X-Fairlead-Privacy"));
        }

        [Fact]
        public async Task LocalPrivacyAsksTheRouterForLocalEnginesOnly()
        {
            var http = new FakeTransport().On("POST", "127.0.0.1:8776", 200, Reply);
            var client = new LlmClient(new LlmSettings { Profile = LlmProfile.OpenAi, OpenAiToken = "halyard-client-key-0123456789", Privacy = "local" }, http);
            await client.TestAsync(CancellationToken.None);
            Assert.Equal("local", http.Calls.Single().Headers["X-Fairlead-Privacy"]);
        }

        [Fact]
        public async Task AnExplicitLlmUrlStillWinsAndNoCallGoesToAnExternalDefault()
        {
            var http = new FakeTransport().On("POST", "gateway.example.test", 200, Reply);
            var s = HalyardSettings.FromEnvironment(Path.GetTempPath(), n => n == "LLM_URL" ? "https://gateway.example.test/v1" : n == "LLM_TOKEN" ? "tok-0123456789abcdef" : n == "HALYARD_LLM_PRIVACY" ? "local" : null);
            Assert.Equal("local", s.Llm.Privacy);
            await new LlmClient(s.Llm, http, LlmProfile.OpenAi).TestAsync(CancellationToken.None);
            Assert.Equal("https://gateway.example.test/v1/chat/completions", http.Calls.Single().Url);
        }

        [Fact]
        public void NoSourceFileBuildsInAnExternalModelHost()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Halyard.Core"))) root = root.Parent;
            Assert.NotNull(root);
            var hits = Directory.GetFiles(Path.Combine(root!.FullName, "src", "Halyard.Core"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllText(f).Contains("api.openai.com")).ToList();
            Assert.Empty(hits);
        }
    }
}
