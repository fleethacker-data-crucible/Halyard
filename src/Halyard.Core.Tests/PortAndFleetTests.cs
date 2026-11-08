using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Model;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>edge-ops PORTS.md: Halyard's own block with no random fallback, and the /__fleethacker id and shutdown endpoints.</summary>
    public sealed class PortAndFleetTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-port-" + Guid.NewGuid().ToString("N"));

        public PortAndFleetTests() { Directory.CreateDirectory(_root); }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private HalyardHost NewHost(int port)
        {
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            return new HalyardHost(new HostOptions { Settings = settings, PreferredPort = port, WebRoot = null, AutoStop = false });
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        [Fact]
        public void TheDefaultIsHalyardsBlock()
        {
            Assert.Equal(8806, new HalyardSettings().PreferredPort);
            Assert.Equal(8806, HalyardSettings.PortBlockStart);
            Assert.Equal(8808, HalyardSettings.PortBlockEnd);
        }

        [Fact]
        public void AnExplicitPortOutsideTheBlockIsUsedExactlyAndABusyOneIsAClearError()
        {
            var port = FreePort();
            var blocker = new TcpListener(IPAddress.Loopback, port);
            blocker.Start();
            try
            {
                using (var h = NewHost(port))
                {
                    var ex = Assert.Throws<InvalidOperationException>(() => h.Start());
                    Assert.Contains("Port " + port + " is in use", ex.Message);
                }
            }
            finally { blocker.Stop(); }
            using (var h = NewHost(port)) Assert.Equal(port, h.Start().Port);
        }

        [Fact]
        public async Task IdAndShutdownFollowTheFleetContract()
        {
            using (var h = NewHost(HostOptions.AnyPort))
            {
                h.LauncherPath = @"C:\Halyard\scripts\Start-Halyard.ps1";
                var info = h.Start();
                using (var c = new HttpClient())
                {
                    var id = JObject.Parse(await c.GetStringAsync(info.Url + "__fleethacker/id"));
                    Assert.Equal("halyard", (string)id["tool"]);
                    Assert.Equal(HalyardHost.Version, (string)id["version"]);
                    Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, (int)id["pid"]!);
                    Assert.Equal(@"C:\Halyard\scripts\Start-Halyard.ps1", (string)id["path"]);

                    var noIntent = await c.PostAsync(info.Url + "__fleethacker/shutdown", new StringContent(""));
                    Assert.Equal(403, (int)noIntent.StatusCode);

                    var foreign = new HttpRequestMessage(HttpMethod.Get, info.Url + "__fleethacker/id");
                    foreign.Headers.Add("Origin", "http://evil.example");
                    Assert.Equal(403, (int)(await c.SendAsync(foreign)).StatusCode);

                    var stop = new HttpRequestMessage(HttpMethod.Post, info.Url + "__fleethacker/shutdown");
                    stop.Headers.Add("X-FleetHacker-Shutdown", "1");
                    Assert.Equal(200, (int)(await c.SendAsync(stop)).StatusCode);
                    Assert.True(h.WaitForStop(5000), "the engine stops after a shutdown request");
                }
            }
        }
    }
}
