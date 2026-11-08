using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Halyard.Core.Host;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>
    /// The engine ships as source compiled by Add-Type on the workstation, where application control blocks loading
    /// any custom DLL. These checks keep the source loadable there; the CI step build\Test-HalyardAddType.ps1
    /// compiles it with the real Windows PowerShell 5.1 compiler.
    /// </summary>
    public class EngineSourceTests
    {
        private static string[] EngineFiles()
        {
            var dir = Path.Combine(Golden.RepoRoot(), "src", "Halyard.Core");
            return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToArray();
        }

        [Fact]
        public void VersionMatchesTheProjectFile()
        {
            // One version everywhere: the root VERSION file, the engine, the project file (the release reads it) and the web package.
            var root = Golden.RepoRoot();
            var version = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
            var csproj = XDocument.Load(Path.Combine(root, "src", "Halyard.Core", "Halyard.Core.csproj"));
            Assert.Equal(version, csproj.Descendants("Version").Single().Value.Trim());
            Assert.Equal(HalyardHost.Version, version);
            var web = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(root, "web", "package.json")), "\"version\"\\s*:\\s*\"([^\"]+)\"");
            Assert.Equal(version, web.Groups[1].Value);
            Assert.Contains("## " + version, File.ReadAllText(Path.Combine(root, "CHANGELOG.md")));
        }

        [Fact]
        public void LoadsNothingFromDisk()
        {
            // Loading an assembly by path (or bytes) is exactly what the workstation policy blocks; third-party
            // packages would need their DLLs next to the engine.
            var forbidden = new Regex(@"\bAssembly\s*\.\s*(Load|LoadFrom|LoadFile|UnsafeLoadFrom|ReflectionOnlyLoad)\b|\bNativeLibrary\b|\bAppDomain\s*\.\s*CurrentDomain\s*\.\s*Load\b|\bNewtonsoft\b|#nullable");
            var hits = EngineFiles().SelectMany(f => File.ReadAllLines(f).Select((l, i) => new { f, i, l }))
                .Where(x => !x.l.TrimStart().StartsWith("//") && !x.l.TrimStart().StartsWith("///") && forbidden.IsMatch(x.l))
                .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1) + ": " + x.l.Trim()).ToList();
            Assert.True(hits.Count == 0, string.Join("\n", hits));
        }
    }
}

namespace Halyard.Core.Tests
{
    /// <summary>The JSON reader parses untrusted request bodies and provider/model replies: it must fail cleanly.</summary>
    public class JsonReaderHardeningTests
    {
        [Fact]
        public void DeepNestingIsRejectedNotAStackOverflow()
        {
            var ok = new string('[', 500) + new string(']', 500);
            Assert.Equal(Serialization.JTokenType.Array, Serialization.JToken.Parse(ok).Type);
            foreach (var open in new[] { '[', '{' })
            {
                var deep = open == '[' ? new string('[', 100000) : string.Concat(System.Linq.Enumerable.Repeat("{\"a\":", 100000));
                Assert.Throws<Serialization.JsonException>(() => Serialization.JToken.Parse(deep));
            }
        }

        [Fact]
        public void MalformedInputThrowsJsonExceptionOnly()
        {
            foreach (var bad in new[] { "", "{", "[1,", "{\"a\"", "{\"a\":}", "\"unterminated", "[\"\\u12\"]", "[\"\\q\"]", "tru", "nul", "{} x", "[1]]", "-", "1e", "{'a':1", "/* open" })
                Assert.Throws<Serialization.JsonException>(() => Serialization.JToken.Parse(bad));
        }

        [Fact]
        public void BadRequestBodiesAre400s()
        {
            foreach (var bad in new[] { new string('[', 100000), "{\"a\":" + new string('1', 5000) + "}x", "{\"x\":\"\\ud800\"" })
            {
                var r = new Host.HttpRequest { Body = System.Text.Encoding.UTF8.GetBytes(bad) };
                Assert.Throws<Host.BadRequestException>(() => Host.Json.Body(r));
            }
        }
    }
}
