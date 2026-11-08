using System.IO;

namespace Halyard.Core.Tests
{
    public static class TestDirs
    {
        /// <summary>Removes a test's temp folder; false when Windows still holds a file in it (the system temp folder is cleaned later).</summary>
        public static bool TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); return true; }
            catch (IOException) { return false; }
        }
    }
}
