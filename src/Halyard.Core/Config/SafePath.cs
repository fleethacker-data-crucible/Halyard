using System;
using System.IO;

namespace Halyard.Core.Config
{
    /// <summary>
    /// Path joins that cannot leave their root. <c>Path.Combine</c> silently drops everything before a rooted part, and <c>..</c>
    /// climbs out; <see cref="Under"/> refuses both, so a value from settings, a request or a lookup table can never point a read
    /// or a write outside the folder it was meant for.
    /// </summary>
    public static class SafePath
    {
        /// <summary>The full path of <paramref name="parts"/> joined under <paramref name="root"/>. Throws when a part is rooted, empty, or the result is outside the root.</summary>
        public static string Under(string root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A root folder is required.", "root");
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var result = full;
            foreach (var part in parts ?? new string[0])
            {
                if (string.IsNullOrEmpty(part) || Path.IsPathRooted(part)) throw new ArgumentException("A path part must be relative and not empty: '" + part + "'.", "parts");
                // The part is relative (checked above), so a plain separator join cannot drop the root; GetFullPath then resolves any "..".
                result = result + Path.DirectorySeparatorChar + part;
            }
            result = Path.GetFullPath(result);
            var inside = string.Equals(result, full, StringComparison.OrdinalIgnoreCase)
                || result.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!inside) throw new ArgumentException("The path leaves its folder: " + string.Join("/", parts ?? new string[0]), "parts");
            return result;
        }
    }
}
