using System;
using System.Text.RegularExpressions;

namespace Halyard.Core.Schema
{
    /// <summary>
    /// Sizing policy. Stories carry Fibonacci points (1, 2, 3, 5, 8); Features carry effort Small/Medium/Large.
    /// The two never mix. Oversize values are capped (13 and 21 → 8) rather than rejected so AI output
    /// still lands as a proposal a human can lower further.
    /// </summary>
    public static class Sizing
    {
        public const string Tbd = "TBD";
        public static readonly int[] StoryPointScale = { 1, 2, 3, 5, 8 };
        public static readonly string[] EffortScale = { "Small", "Medium", "Large" };

        private static readonly Regex PointsNoise = new Regex(@"(STORY|POINTS?|PTS?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Normalises any points text to 1|2|3|5|8|TBD.</summary>
        public static string ClampPoints(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Tbd;
            var p = PointsNoise.Replace(raw!.Trim().Trim('"', '\'', '*'), string.Empty).Trim().ToUpperInvariant();
            if (p == Tbd) return Tbd;
            int n;
            if (!int.TryParse(p, out n))
            {
                var m = Regex.Match(p, @"\d+");
                if (!m.Success || !int.TryParse(m.Value, out n)) return Tbd;
            }
            if (n <= 1) return "1";
            if (n == 2) return "2";
            if (n == 3) return "3";
            if (n <= 5) return "5";
            return "8";
        }

        public static bool IsValidPoints(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value!.Trim();
            if (v.Equals(Tbd, StringComparison.OrdinalIgnoreCase)) return true;
            int n;
            return int.TryParse(v, out n) && Array.IndexOf(StoryPointScale, n) >= 0;
        }

        /// <summary>Normalises effort text to Small|Medium|Large|TBD. XS→Small, XL/XXL→Large (policy: three bands only).</summary>
        public static string ClampEffort(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Tbd;
            var e = raw!.Trim().Trim('"', '\'', '*').ToUpperInvariant();
            if (e == Tbd) return Tbd;
            if (Regex.IsMatch(e, @"^(XS|X-SMALL|EXTRA\s*SMALL|S|SMALL)$")) return "Small";
            if (Regex.IsMatch(e, @"^(M|MEDIUM|MED)$")) return "Medium";
            if (Regex.IsMatch(e, @"^(L|LARGE|XL|XXL|X-LARGE|EXTRA\s*LARGE|EPIC)$")) return "Large";
            if (e.StartsWith("S")) return "Small";
            if (e.StartsWith("M")) return "Medium";
            if (e.StartsWith("L") || e.StartsWith("X")) return "Large";
            return Tbd;
        }

        public static bool IsValidEffort(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value!.Trim();
            if (v.Equals(Tbd, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var band in EffortScale)
            {
                if (band.Equals(v, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
