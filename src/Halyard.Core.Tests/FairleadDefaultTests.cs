using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Llm;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Installs a fake Credential Manager for one test and puts the real reader back afterwards.</summary>
    public sealed class FakeCredentialStore : IDisposable
    {
        private readonly Func<string, string?> _old;
        public List<string> Targets { get; } = new List<string>();

        public FakeCredentialStore(string? key)
        {
            _old = CredentialReader.Reader!;
            CredentialReader.Reader = target => { Targets.Add(target); return key; };
        }

        public void Dispose() { CredentialReader.Reader = _old!; }
    }

    /// <summary>Model calls go to Fairlead on this machine only, with the key from Credential Manager. Fake transport only.</summary>
    [Collection("CredentialReader")]
    public class FairleadDefaultTests
    {
        private const string Reply = "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}";
        private const string Key = "fl-halyard-client-key-0123456789";

        [Fact]
        public void TheDefaultEndpointIsTheLocalRouter()
        {
            Assert.Equal("http://127.0.0.1:8776/v1/chat/completions", LlmSettings.DefaultUrl);
            Assert.Equal(LlmSettings.DefaultUrl, new LlmSettings().Url());
            Assert.Equal(LlmSettings.DefaultUrl, LlmSettings.NormalizeUrl(null));
            Assert.Equal("auto", new LlmSettings().Model());
        }

        [Fact]
        public async Task TheRequestGoesToFairleadWithTheClientKeyAndTheLabel()
        {
            using (var creds = new FakeCredentialStore(Key))
            {
                var http = new FakeTransport().On("POST", "127.0.0.1:8776", 200, Reply);
                var client = new LlmClient(new LlmSettings(), http);
                Assert.Equal("OK", (await client.TestAsync(CancellationToken.None)).Text);
                var call = Assert.Single(http.Calls);
                Assert.Equal("http://127.0.0.1:8776/v1/chat/completions", call.Url);
                Assert.Equal("Bearer " + Key, call.Headers["Authorization"]);
                Assert.Equal("Halyard", call.Headers["X-Fairlead-Label"]);
                Assert.False(call.Headers.ContainsKey("X-Fairlead-Privacy"));
                var body = JObject.Parse(call.JsonBody!);
                Assert.Equal("auto", (string)body["model"]!);
                Assert.Equal(4000, (int)body["max_tokens"]!);
                Assert.Equal("Fairlead/Halyard", Assert.Single(creds.Targets.Distinct()));
            }
        }

        [Fact]
        public async Task LocalPrivacyAsksTheRouterForLocalEnginesOnly()
        {
            using (new FakeCredentialStore(Key))
            {
                var http = new FakeTransport().On("POST", "127.0.0.1:8776", 200, Reply);
                var client = new LlmClient(new LlmSettings { Privacy = "local" }, http);
                await client.TestAsync(CancellationToken.None);
                Assert.Equal("local", http.Calls.Single().Headers["X-Fairlead-Privacy"]);
            }
        }

        [Fact]
        public async Task TheAddressAndRouteComeFromHalyardFairleadVariablesAndStayOnThisPc()
        {
            using (new FakeCredentialStore(Key))
            {
                var http = new FakeTransport().On("POST", "127.0.0.1:9001", 200, Reply);
                var s = HalyardSettings.FromEnvironment(Path.GetTempPath(), n => n == "HALYARD_FAIRLEAD_URL" ? "http://127.0.0.1:9001/v1" : n == "HALYARD_FAIRLEAD_MODEL" ? "smart" : n == "HALYARD_LLM_PRIVACY" ? "local" : null);
                Assert.Equal("local", s.Llm.Privacy);
                await new LlmClient(s.Llm, http).TestAsync(CancellationToken.None);
                var call = http.Calls.Single();
                Assert.Equal("http://127.0.0.1:9001/v1/chat/completions", call.Url);
                Assert.Equal("smart", (string)JObject.Parse(call.JsonBody!)["model"]!);
            }
        }

        [Theory]
        [InlineData("https://gateway.example.test/v1")]
        [InlineData("http://fairlead.example.test:8776/v1")]
        [InlineData("http://user:pw@127.0.0.1:8776/v1")]
        [InlineData("ftp://127.0.0.1/v1")]
        [InlineData("not a url")]
        public async Task AnAddressThatIsNotOnThisPcIsRefusedAndNothingIsSent(string url)
        {
            using (new FakeCredentialStore(Key))
            {
                var http = new FakeTransport();
                var s = new LlmSettings { FairleadUrl = url };
                Assert.False(s.IsConfigured());
                Assert.Contains("HALYARD_FAIRLEAD_URL", s.MissingFor());
                await Assert.ThrowsAsync<InvalidOperationException>(() => new LlmClient(s, http).TestAsync(CancellationToken.None));
                Assert.Empty(http.Calls);
            }
        }

        [Theory]
        [InlineData("http://localhost:8776/v1")]
        [InlineData("http://[::1]:8776/v1")]
        [InlineData("http://127.0.0.1:8776")]
        public void LoopbackAddressesAreAccepted(string url)
        {
            Assert.Null(LlmSettings.UrlProblem(LlmSettings.NormalizeUrl(url)));
        }

        [Fact]
        public async Task WithoutAnInstalledKeyTheCallIsRefusedWithAClearMessage()
        {
            using (new FakeCredentialStore(null))
            {
                var http = new FakeTransport();
                var s = new LlmSettings();
                Assert.False(s.IsConfigured());
                Assert.Equal("Open Fairlead and install a key for Halyard.", s.MissingFor());
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new LlmClient(s, http).TestAsync(CancellationToken.None));
                Assert.Contains("Open Fairlead and install a key for Halyard.", ex.Message);
                Assert.Empty(http.Calls);
            }
        }

        [Fact]
        public async Task TheKeyIsNeverTakenFromTheEnvironment()
        {
            using (new FakeCredentialStore(null))
            {
                var s = HalyardSettings.FromEnvironment(Path.GetTempPath(), n => n.EndsWith("TOKEN") || n.EndsWith("API_KEY") || n.EndsWith("_KEY") ? "tok-0123456789abcdef" : null);
                Assert.False(s.Llm.IsConfigured());
                var http = new FakeTransport();
                await Assert.ThrowsAsync<InvalidOperationException>(() => new LlmClient(s.Llm, http).TestAsync(CancellationToken.None));
                Assert.Empty(http.Calls);
            }
        }

        [Fact]
        public async Task ARejectedKeyTellsTheUserToInstallANewOne()
        {
            using (new FakeCredentialStore(Key))
            {
                var http = new FakeTransport().On("POST", "127.0.0.1:8776", 401, "{\"error\":{\"message\":\"bad key\"}}");
                var ex = await Assert.ThrowsAsync<ProviderHttpException>(() => new LlmClient(new LlmSettings(), http).TestAsync(CancellationToken.None));
                Assert.Contains("install a new key for Halyard", ex.Message);
                Assert.DoesNotContain(Key, ex.Message);
            }
        }

        [Fact]
        public void TheKeyDecodesFromUtf16OrUtf8AndNothingElse()
        {
            Assert.Equal(Key, CredentialReader.Decode(Encoding.Unicode.GetBytes(Key)));
            Assert.Equal(Key, CredentialReader.Decode(Encoding.UTF8.GetBytes(Key)));
            Assert.Equal(Key, CredentialReader.Decode(Encoding.Unicode.GetBytes("  " + Key + "\r\n")));
            Assert.Null(CredentialReader.Decode(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
            Assert.Null(CredentialReader.Decode(new byte[0]));
            Assert.Null(CredentialReader.Decode(null));
        }

        [Fact]
        public void AReaderThatThrowsMeansNoKey()
        {
            var old = CredentialReader.Reader;
            CredentialReader.Reader = t => throw new InvalidOperationException("boom");
            try { Assert.Null(CredentialReader.Read("Fairlead/Halyard")); }
            finally { CredentialReader.Reader = old; }
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
