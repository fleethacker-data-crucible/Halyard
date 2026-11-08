using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Spoken answers through TalkForge, the way Lazarette does it. Fakes only: no node, no TalkForge, no audio.</summary>
    public sealed class TalkForgeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-tf-" + Guid.NewGuid().ToString("N"));
        private string Folder { get { return Path.Combine(_root, "TalkForge"); } }

        public TalkForgeTests()
        {
            var models = Path.Combine(Folder, "assets", "models");
            Directory.CreateDirectory(models);
            Directory.CreateDirectory(Path.Combine(Folder, "tools"));
            File.WriteAllText(Path.Combine(Folder, "tools", "node-engine.mjs"), "export const DEFAULT_VOICE = 'en_US-libritts_r-medium';");
            File.WriteAllText(Path.Combine(models, "en_GB-alba-medium.onnx"), "x");
            File.WriteAllText(Path.Combine(models, "en_GB-alba-medium.onnx.json"), "{\"audio\":{\"quality\":\"medium\"},\"num_speakers\":1,\"language\":{\"name_english\":\"English\",\"country_english\":\"Great Britain\"}}");
            File.WriteAllText(Path.Combine(models, "en_US-libritts_r-medium.onnx"), "x");
            File.WriteAllText(Path.Combine(models, "en_US-libritts_r-medium.onnx.json"), "{\"audio\":{\"quality\":\"medium\"},\"num_speakers\":904}");
            File.WriteAllText(Path.Combine(models, "no-config.onnx"), "x");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static FakeTransport Heartbeat(bool up)
        {
            var http = new FakeTransport();
            if (up) http.On("GET", "/__fleethacker/heartbeat", 200, "{\"tool\":\"talkforge\",\"ok\":true}");
            else http.On("GET", "/__fleethacker/heartbeat", c => { throw new ProviderHttpException(0, "refused", null); });
            return http;
        }

        [Fact]
        public void VoicesNeedTheirConfigAndTheDefaultComesFirst()
        {
            var v = new TalkForge(Folder, 0, new FakeTransport()).ListVoices();
            Assert.True(v.Found);
            Assert.Equal("en_US-libritts_r-medium", v.DefaultVoice);
            Assert.Equal(new[] { "en_US-libritts_r-medium", "en_GB-alba-medium" }, v.Voices.Select(x => x.Id).ToArray());
            Assert.Equal(904, v.Voices[0].Speakers);
            Assert.Equal("English, Great Britain", v.Voices[1].Language);
            Assert.False(new TalkForge(Path.Combine(_root, "nope"), 0, new FakeTransport()).ListVoices().Found);
        }

        [Fact]
        public async Task StateSaysMissingOfflineOrThatAnotherCopyHasNoToken()
        {
            Assert.Equal("missing", (await new TalkForge(Path.Combine(_root, "nope"), 0, Heartbeat(false)).StateAsync(CancellationToken.None)).State);
            Assert.Equal("offline", (await new TalkForge(Folder, 0, Heartbeat(false)).StateAsync(CancellationToken.None)).State);
            var foreign = await new TalkForge(Folder, 25008, Heartbeat(true)).StateAsync(CancellationToken.None);
            Assert.Equal("offline", foreign.State);
            Assert.Contains("no token", foreign.Message);
            var tf = new TalkForge(Folder, 25008, Heartbeat(true)) { ExternalToken = () => "0123456789abcdef0123" };
            Assert.Equal("ready", (await tf.StateAsync(CancellationToken.None)).State);
        }

        [Fact]
        public async Task StartNeedsTheTalkForgeScript()
        {
            var s = await new TalkForge(Folder, 0, Heartbeat(false)).StartAsync(CancellationToken.None);
            Assert.Equal("missing", s.State);
            Assert.Contains("talkforge.mjs", s.Message);
        }

        [Fact]
        public async Task SpeakSendsTheBearerTokenAndReturnsTheAudio()
        {
            var http = Heartbeat(true);
            HttpCall? seen = null;
            http.On("POST", "/v1/audio/speech", c => { seen = c; return new HttpReply(200, "", new Dictionary<string, string>()) { BodyBytes = new byte[] { 82, 73, 70, 70 } }; });
            var tf = new TalkForge(Folder, 25008, http) { ExternalToken = () => "0123456789abcdef0123" };
            var audio = await tf.SpeakAsync("Hello there.", "en_GB-alba-medium", 1.0, CancellationToken.None);
            Assert.Equal(new byte[] { 82, 73, 70, 70 }, audio);
            Assert.Equal("http://127.0.0.1:25008/v1/audio/speech", seen!.Url);
            Assert.Equal("Bearer 0123456789abcdef0123", seen.Headers["Authorization"]);
            Assert.True(seen.BinaryResponse);
            var p = JObject.Parse(seen.JsonBody);
            Assert.Equal("Hello there.", (string)p["input"]);
            Assert.Equal("en_GB-alba-medium", (string)p["voice"]);
            Assert.Equal("wav", (string)p["response_format"]);
        }

        [Fact]
        public async Task NoTokenOrARefusedTokenIsOfflineNotACrash()
        {
            await Assert.ThrowsAsync<TalkForgeOfflineException>(() => new TalkForge(Folder, 0, Heartbeat(false)).SpeakAsync("Hi.", null, 1, CancellationToken.None));
            var http = Heartbeat(true).On("POST", "/v1/audio/speech", 401, "{}");
            var tf = new TalkForge(Folder, 0, http) { ExternalToken = () => "0123456789abcdef0123" };
            await Assert.ThrowsAsync<TalkForgeOfflineException>(() => tf.SpeakAsync("Hi.", null, 1, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => tf.SpeakAsync("Hi.", "../../etc", 1, CancellationToken.None));
        }

        [Fact]
        public void PayloadClampsTheSpeed()
        {
            var p = JObject.Parse(TalkForge.Payload("Hi.", null, 9));
            Assert.Equal(2.0, (double)p["speed"]!);
            Assert.Null(p["voice"]);
        }

        [Fact]
        public async Task TheSpeakRouteSaysOfflineAndRefusesAnUnknownVoice()
        {
            var settings = new HalyardSettings { RootDirectory = Path.Combine(_root, "halyard"), TalkForgePath = Folder };
            settings.Provider.Kind = ProviderKind.GitHub;
            using (var h = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null, AutoStop = false, Transport = Heartbeat(false) }))
            using (var c = new HttpClient())
            {
                var info = h.Start();
                c.DefaultRequestHeaders.Add("X-Halyard-Session", info.SessionToken);
                var offline = await c.PostAsync(info.Url + "api/voice/speak", new StringContent("{\"text\":\"Hello.\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(409, (int)offline.StatusCode);
                Assert.Equal("VOICE_OFFLINE", (string)JObject.Parse(await offline.Content.ReadAsStringAsync())["code"]);
                var unknown = await c.PostAsync(info.Url + "api/voice/speak", new StringContent("{\"text\":\"Hello.\",\"voice\":\"robot\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(400, (int)unknown.StatusCode);
                var voices = JObject.Parse(await c.GetStringAsync(info.Url + "api/voice/voices"));
                Assert.Equal(2, ((JArray)voices["voices"]!).Count);
                var status = JObject.Parse(await c.GetStringAsync(info.Url + "api/voice/status"));
                Assert.Equal("offline", (string)status["speech"]!["state"]);
            }
        }
    }
}
