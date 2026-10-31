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
    public class HandoffImportTests
    {
        private static string Doc(string app = "packetrat", string items = "[{\"ref\":\"msg-1\",\"title\":\"Renew the badge reader contract\",\"body\":\"Decide by Friday.\",\"labels\":[\"area::facilities\"],\"url\":\"https://example.org/t/1\",\"due\":\"2026-11-06\",\"kind\":\"story\"}]")
        {
            return "{\"format\":\"fleet-handoff\",\"version\":1,\"source\":{\"app\":\"" + app + "\",\"exportedAt\":\"2026-10-31T09:00:00Z\"},\"items\":" + items + "}";
        }

        [Fact]
        public void ReadsTheDocumentedExample()
        {
            var r = HandoffImport.Parse(Doc());
            Assert.Equal("packetrat", r.App);
            Assert.Equal("2026-10-31T09:00:00Z", r.ExportedAt);
            var item = Assert.Single(r.Items);
            Assert.Equal("Renew the badge reader contract", item.Title);
            Assert.Equal("github:handoff/packetrat:" + HandoffImport.StableNumber("msg-1"), item.Key);
            Assert.Contains("Decide by Friday.", item.Body);
            Assert.Contains("Due: 2026-11-06", item.Body);
            Assert.Contains("Handed over by packetrat (ref msg-1)", item.Body);
            Assert.Equal(new[] { "source::packetrat", "type::story", "area::facilities" }, item.Labels.ToArray());
            Assert.Equal("https://example.org/t/1", item.Url);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void EightBellsUsesTheSameEnvelope()
        {
            var r = HandoffImport.Parse(Doc("eight-bells", "[{\"ref\":\"ll-7\",\"title\":\"Lesson: double-check the ballast log\"}]"));
            Assert.Equal("eight-bells", r.App);
            Assert.Equal("handoff/eight-bells", r.Source.Identifier);
            Assert.Contains("source::eight-bells", r.Items[0].Labels);
        }

        [Theory]
        [InlineData("not json", "not valid JSON")]
        [InlineData("[]", "must be a JSON object")]
        [InlineData("{\"format\":\"other\",\"version\":1}", "Not a fleet-handoff file")]
        [InlineData("{\"format\":\"fleet-handoff\",\"version\":2}", "Unsupported")]
        [InlineData("{\"format\":\"fleet-handoff\",\"version\":1,\"source\":{\"app\":\"Bad App!\"},\"items\":[]}", "source.app")]
        [InlineData("{\"format\":\"fleet-handoff\",\"version\":1,\"source\":{\"app\":\"packetrat\"}}", "items must be an array")]
        public void RefusesDocumentsItCannotTrust(string text, string expected)
        {
            var ex = Assert.Throws<FormatException>(() => HandoffImport.Parse(text));
            Assert.Contains(expected, ex.Message);
        }

        [Fact]
        public void SkipsBadItemsAndSaysWhy()
        {
            var r = HandoffImport.Parse(Doc(items: "[{\"ref\":\"a\",\"title\":\"ok\"},{\"title\":\"no ref\"},{\"ref\":\"c\"},\"text\",{\"ref\":\"a\",\"title\":\"dup\"}]"));
            Assert.Equal("ok", Assert.Single(r.Items).Title);
            Assert.Equal(4, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.Contains("no ref"));
            Assert.Contains(r.Warnings, w => w.Contains("no title"));
            Assert.Contains(r.Warnings, w => w.Contains("duplicate"));
        }

        [Fact]
        public void LimitsLengthsStripsControlCharactersAndNonHttpLinks()
        {
            var longTitle = new string('t', 500);
            var r = HandoffImport.Parse(Doc(items: "[{\"ref\":\"a\",\"title\":\"" + longTitle + "\",\"body\":\"x\\u0007y\",\"url\":\"javascript:alert(1)\",\"kind\":\"weird\"}]"));
            var item = r.Items.Single();
            Assert.Equal(HandoffImport.MaxTitle, item.Title.Length);
            Assert.StartsWith("xy", item.Body);
            Assert.Null(item.Url);
            Assert.DoesNotContain(item.Labels, l => l.StartsWith("type::"));
            var tooMany = "[" + string.Join(",", Enumerable.Range(0, HandoffImport.MaxItems + 1).Select(i => "{\"ref\":\"r" + i + "\",\"title\":\"t\"}")) + "]";
            Assert.Throws<FormatException>(() => HandoffImport.Parse(Doc(items: tooMany)));
        }

        [Fact]
        public void TheSameRefAlwaysGivesTheSameNumber()
        {
            Assert.Equal(HandoffImport.StableNumber("msg-1"), HandoffImport.StableNumber("msg-1"));
            Assert.NotEqual(HandoffImport.StableNumber("msg-1"), HandoffImport.StableNumber("msg-2"));
            Assert.InRange(HandoffImport.StableNumber("anything"), 1, int.MaxValue);
        }
    }

    public sealed class HandoffRouteTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-handoff-" + Guid.NewGuid().ToString("N"));
        private readonly FakeGitLabProject _gitlab = new FakeGitLabProject(5678, "platform/sandbox");
        private readonly HttpClient _client = new HttpClient();
        private readonly HalyardHost _host;
        private readonly HostInfo _info;

        public HandoffRouteTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitLab;
            settings.Provider.BaseUrl = "https://gitlab.example.com";
            settings.Provider.Token = "t";
            settings.Provider.SandboxRepository = "5678";
            settings.Provider.SourceRepository = "1234";
            settings.Provider.WriteDelayMs = 0;
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null, Transport = _gitlab.Http });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static string Body(string items)
        {
            return new JObject { ["document"] = JObject.Parse("{\"format\":\"fleet-handoff\",\"version\":1,\"source\":{\"app\":\"packetrat\"},\"items\":" + items + "}") }.ToString();
        }

        private async Task<JObject> Post(string path, string body)
        {
            var reply = await _client.PostAsync(_info.Url + path, new StringContent(body));
            var json = JObject.Parse(await reply.Content.ReadAsStringAsync());
            json["httpStatus"] = (int)reply.StatusCode;
            return json;
        }

        [Fact]
        public async Task PreviewShowsDraftsAndWritesNothing()
        {
            var reply = await Post("api/intake/handoff/preview", Body("[{\"ref\":\"a\",\"title\":\"Fix the badge reader\",\"kind\":\"story\"},{\"ref\":\"b\",\"title\":\"Order spare cables\"}]"));
            Assert.Equal(200, (int)reply["httpStatus"]!);
            Assert.Equal("packetrat", (string)reply["app"]!);
            Assert.Equal(2, (int)reply["count"]!);
            Assert.Equal(0, (int)reply["alreadyCopied"]!);
            Assert.Contains(((JArray)reply["drafts"]!), d => ((string)d["title"]!).Contains("badge reader") && ((string)d["sourceTitle"]!) == "Fix the badge reader");
            Assert.Empty(_gitlab.Http.Writes());
        }

        [Fact]
        public async Task BadDocumentsAreBadRequests()
        {
            Assert.Equal(400, (int)(await Post("api/intake/handoff/preview", "{}"))["httpStatus"]!);
            Assert.Equal(400, (int)(await Post("api/intake/handoff/preview", "{\"document\":\"nope\"}"))["httpStatus"]!);
            var empty = await Post("api/intake/handoff/preview", Body("[{\"title\":\"no ref\"}]"));
            Assert.Equal(400, (int)empty["httpStatus"]!);
            Assert.Contains("no usable items", (string)empty["error"]!);
        }

        [Fact]
        public async Task RunCopiesIntoTheSandboxOnlyAndAnImportedRefIsRecognisedNextTime()
        {
            var body = Body("[{\"ref\":\"a\",\"title\":\"Fix the badge reader\",\"kind\":\"story\"}]");
            var accepted = await Post("api/intake/handoff/run", body);
            Assert.Equal(202, (int)accepted["httpStatus"]!);
            var id = (string)accepted["job"]!["id"]!;
            JObject job;
            do { await Task.Delay(25); job = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/jobs/" + id)); } while ((string)job["state"]! == "queued" || (string)job["state"]! == "running");
            Assert.Equal("succeeded", (string)job["state"]!);
            Assert.Equal(1, _gitlab.Count);
            Assert.All(_gitlab.Http.Writes(), w => Assert.Contains("/projects/5678/", w.Url));
            Assert.Contains("Fix the badge reader", _gitlab.Http.Calls.Where(c => c.Method == "POST" && c.Url.EndsWith("/issues")).Select(c => c.JsonBody!).Single());
        }
    }
}
