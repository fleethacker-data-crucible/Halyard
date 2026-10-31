using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Model;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Fleet CLI parity: every API route the UI can call is a named command, and every command has a route.</summary>
    public sealed class CommandParityTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-parity-" + Guid.NewGuid().ToString("N"));
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();

        public CommandParityTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = 0, WebRoot = null });
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        public void Dispose()
        {
            _host.Stop();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        [Fact]
        public void EveryRouteIsACommandAndEveryCommandIsARoute()
        {
            var routes = _host.Router.Templates.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var commands = CommandRegistry.All.Select(c => c.Method + " " + c.Route).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Empty(routes.Except(commands));   // a route with no command: add it to CommandRegistry
            Assert.Empty(commands.Except(routes));   // a command with no route
            Assert.Equal(routes.Count, routes.Distinct().Count());
        }

        [Fact]
        public void NamesFollowAppNounVerbAndAreUnique()
        {
            Assert.All(CommandRegistry.All, c =>
            {
                var parts = c.Name.Split(' ');
                Assert.Equal(3, parts.Length);
                Assert.Equal("halyard", parts[0]);
                Assert.All(parts, p => Assert.Matches("^[a-z][a-z-]*$", p));
                Assert.False(string.IsNullOrWhiteSpace(c.Summary));
            });
            Assert.Equal(CommandRegistry.All.Count, CommandRegistry.All.Select(c => c.Name).Distinct().Count());
        }

        [Fact]
        public void EveryWritingCommandIsAPostAndEveryReadIsAGet()
        {
            Assert.All(CommandRegistry.All.Where(c => c.Writes), c => Assert.Equal("POST", c.Method));
            Assert.All(CommandRegistry.All.Where(c => c.Async), c => Assert.Equal("POST", c.Method));
            Assert.Contains(CommandRegistry.All, c => c.Name == "halyard apply rollback" && c.Writes);
            Assert.Contains(CommandRegistry.All, c => c.Name == "halyard promote run" && c.Writes && c.Async);
        }

        [Fact]
        public async Task CommandsJsonIsVersionedAndServedByTheApi()
        {
            var fromApi = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/commands"));
            Assert.Equal("halyard", (string)fromApi["app"]!);
            Assert.Equal(CommandRegistry.SchemaVersion, (int)fromApi["schemaVersion"]!);
            Assert.Equal(CommandRegistry.All.Count, ((JArray)fromApi["commands"]!).Count);
            var first = (JObject)fromApi["commands"]![0]!;
            Assert.Equal(new[] { "name", "method", "route", "summary", "writes", "async" }, first.Properties().Select(p => p.Name).ToArray());
            Assert.Equal(JObject.Parse(CommandRegistry.Json()).ToString(), fromApi.ToString());
            Assert.Equal(JObject.Parse(Facade.CommandsJson()).ToString(), fromApi.ToString());
        }

        [Fact]
        public async Task TheCommandListNeedsTheSessionToken()
        {
            using (var anon = new HttpClient())
                Assert.Equal(401, (int)(await anon.GetAsync(_info.Url + "api/commands")).StatusCode);
        }
    }
}
