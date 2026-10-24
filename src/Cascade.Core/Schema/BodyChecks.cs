using System.Text.RegularExpressions;
using Cascade.Core.Model;

namespace Cascade.Core.Schema
{
    /// <summary>
    /// Deterministic content checks for the Definition of Ready. These look for the presence of the
    /// playbook sections; they do not judge quality. All regexes are case-insensitive.
    /// </summary>
    public static class BodyChecks
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Multiline;
        private const string BulletAfter = @"[\s\S]{0,240}?(^\s*(-|\*|\d+\.)\s+\S)";

        public static bool HasAcceptanceCriteria(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            if (!Regex.IsMatch(body!, @"acceptance\s*criteria", Opts)) return false;
            return Regex.IsMatch(body!, @"acceptance\s*criteria" + BulletAfter, Opts) || Regex.IsMatch(body!, @"\bcriterion\b", Opts);
        }

        public static bool HasDoneWhen(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            return Regex.IsMatch(body!, @"(done-?\s*when|definition\s+of\s+done|completion\s+criteria)" + BulletAfter, Opts);
        }

        public static bool HasTasks(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            return Regex.IsMatch(body!, @"^\s*#{1,4}\s*(recommended\s+)?tasks?\b" + BulletAfter, Opts) || Regex.IsMatch(body!, @"^\s*-\s*\[[ xX]\]\s+\S", Opts);
        }

        public static bool HasUserNeed(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            return Regex.IsMatch(body!, @"(user\s+need|story\s+text|user\s+story)", Opts) || Regex.IsMatch(body!, @"\bas\s+an?\s+[^,\n]{2,80},?\s+i\s+(want|need)\b", Opts);
        }

        public static bool HasOutcome(string? body)
        {
            return !string.IsNullOrWhiteSpace(body) && Regex.IsMatch(body!, @"\boutcome\b", Opts);
        }

        public static bool HasSuccessPicture(string? body)
        {
            return !string.IsNullOrWhiteSpace(body) && Regex.IsMatch(body!, @"(success\s*picture|success\s*(criteri|measure))", Opts);
        }

        public static bool HasPurpose(string? body)
        {
            return !string.IsNullOrWhiteSpace(body) && Regex.IsMatch(body!, @"\bpurpose\b", Opts);
        }

        public static bool HasScope(string? body)
        {
            return !string.IsNullOrWhiteSpace(body) && Regex.IsMatch(body!, @"\b(in\s+scope|scope\s+in|out\s+of\s+scope)\b", Opts);
        }

        /// <summary>True when the body has substantive text beyond the meta block and Cascade mirror.</summary>
        public static bool HasSubstantiveText(string? body, int minimumLength = 20)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            var clean = CascadeMeta.StripFromBody(body);
            clean = Regex.Replace(clean, @"^\s*###\s*Cascade\s*\r?\n(?:-\s*\*\*[^\r\n]+\r?\n?)+", string.Empty, Opts);
            return clean.Trim().Length >= minimumLength;
        }
    }
}
