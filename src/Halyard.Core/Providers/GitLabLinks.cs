using System;
using System.Text.RegularExpressions;
using Halyard.Core.Model;

namespace Halyard.Core.Providers
{
    /// <summary>One native GitLab issue link, as read from <c>/issues/:iid/links</c>.</summary>
    public sealed class IssueLink
    {
        /// <summary>relates_to | blocks | is_blocked_by</summary>
        public string Type { get; set; }
        public long TargetNumber { get; set; }
        public string TargetProject { get; set; }
        public string Title { get; set; }
        public WorkItemState State { get; set; }
        public string Url { get; set; }

        public IssueLink()
        {
            Type = "relates_to";
            Title = string.Empty;
        }
    }

    /// <summary>Server version handling. A version nobody confirmed is treated as too old, so a newer feature is never switched on by guesswork.</summary>
    public static class GitLabServerVersion
    {
        private static readonly Regex Pattern = new Regex(@"^\s*v?(\d{1,3})\.(\d{1,3})(?:\.\d{1,3})?(?:[-+][\w.\-]*)?\s*$", RegexOptions.Compiled);

        public static bool TryParse(string text, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Pattern.Match(text);
            if (!m.Success) return false;
            major = int.Parse(m.Groups[1].Value);
            minor = int.Parse(m.Groups[2].Value);
            return true;
        }

        public static bool AtLeast(string text, int major, int minor)
        {
            int ma, mi;
            return TryParse(text, out ma, out mi) && (ma > major || (ma == major && mi >= minor));
        }
    }
}
