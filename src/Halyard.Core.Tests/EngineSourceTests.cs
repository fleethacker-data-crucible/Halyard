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
            var csproj = XDocument.Load(Path.Combine(Golden.RepoRoot(), "src", "Halyard.Core", "Halyard.Core.csproj"));
            var version = csproj.Descendants("Version").Single().Value.Trim();
            Assert.Equal(HalyardHost.Version, version);
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
