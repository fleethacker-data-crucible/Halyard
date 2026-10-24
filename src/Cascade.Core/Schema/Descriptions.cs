using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Cascade.Core.Model;

namespace Cascade.Core.Schema
{
    /// <summary>
    /// Builds and updates issue bodies so the machine block, the human-visible mirror and the
    /// narrative stay in sync. Writers must go through here rather than editing meta in isolation.
    /// </summary>
    public static class Descriptions
    {
        private static readonly Regex MirrorRegex = new Regex(@"^[ \t]*###[ \t]*Cascade[ \t]*\r?\n(?:[ \t]*-[ \t]*\*\*[^\r\n]*\r?\n?)+", RegexOptions.Multiline | RegexOptions.Compiled);

        public const string MirrorHeading = "### Cascade";

        /// <summary>Human-visible mirror of the key meta fields.</summary>
        public static string BuildMirror(CascadeMeta meta)
        {
            var lines = new List<string> { MirrorHeading };
            lines.Add("- **Type:** " + (meta.Get("type") ?? string.Empty));
            lines.Add("- **Id:** " + (meta.Id ?? string.Empty));
            lines.Add("- **Parent:** " + (string.IsNullOrWhiteSpace(meta.Parent) ? "(none)" : meta.Parent));
            if (meta.Has("theme")) lines.Add("- **Theme:** " + meta.Theme);
            lines.Add("- **State:** " + (string.IsNullOrWhiteSpace(meta.State) ? "open" : meta.State));
            if (meta.Has("status")) lines.Add("- **Status:** " + meta.Status);
            if (meta.Has("area")) lines.Add("- **Area:** " + meta.Area);
            if (meta.Has("effort")) lines.Add("- **Effort:** " + meta.Effort);
            if (meta.Has("points")) lines.Add("- **Story points:** " + meta.Points);
            if (meta.Has("sprint")) lines.Add("- **Sprint:** " + meta.Sprint);
            if (meta.Has("milestone")) lines.Add("- **Milestone:** " + meta.Milestone);
            if (meta.Has("artifact_level")) lines.Add("- **Artifacts:** " + meta.Get("artifact_level"));
            if (meta.Has("source_project") || meta.Has("source_iid"))
                lines.Add("- **Origin:** " + (meta.SourceProject ?? "?") + " #" + (meta.SourceRef ?? "?"));
            if (meta.Has("product_owner")) lines.Add("- **Product Owner:** " + meta.Get("product_owner"));
            if (meta.Has("technical_focal")) lines.Add("- **Technical Focal:** " + meta.Get("technical_focal"));
            return string.Join("\n", lines);
        }

        /// <summary>meta fence + mirror + narrative. Any previous fence or mirror in the narrative is removed first.</summary>
        public static string Compose(CascadeMeta meta, string? narrative)
        {
            var body = StripMirror(CascadeMeta.StripFromBody(narrative));
            var sb = new StringBuilder();
            sb.Append(meta.ToFence()).Append("\n\n").Append(BuildMirror(meta));
            if (body.Length > 0) sb.Append("\n\n").Append(body);
            return sb.ToString();
        }

        public static string StripMirror(string? body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            return MirrorRegex.Replace(body!, string.Empty).Trim();
        }

        /// <summary>Narrative only: no fence, no mirror.</summary>
        public static string Narrative(string? body)
        {
            return StripMirror(CascadeMeta.StripFromBody(body));
        }

        /// <summary>
        /// Re-emits an existing body with updated meta while keeping the narrative intact. Also rewrites a
        /// <c>## Sizing</c> section when present so points/effort never disagree between meta and prose.
        /// </summary>
        public static string Rewrite(string? existingBody, CascadeMeta newMeta)
        {
            var narrative = Narrative(existingBody);
            narrative = SyncSizingSection(narrative, newMeta);
            return Compose(newMeta, narrative);
        }

        private static string SyncSizingSection(string narrative, CascadeMeta meta)
        {
            var sizing = new List<string>();
            if (meta.Has("points")) sizing.Add("- **Story points:** " + meta.Points);
            if (meta.Has("effort")) sizing.Add("- **Effort:** " + meta.Effort);
            if (meta.Has("sprint")) sizing.Add("- **Sprint:** " + meta.Sprint);
            var section = Regex.Match(narrative, @"^[ \t]*##[ \t]*Sizing[ \t]*\r?\n(?<body>(?:(?!^#).*\r?\n?)*)", RegexOptions.Multiline);
            if (!section.Success) return narrative;
            if (sizing.Count == 0) return narrative;
            var replacement = "## Sizing\n" + string.Join("\n", sizing) + "\n";
            return narrative.Substring(0, section.Index) + replacement + narrative.Substring(section.Index + section.Length);
        }

        /// <summary>Title convention: <c>{TYPE} :: {Short name}</c>.</summary>
        public static string FormatTitle(WorkItemType type, string name)
        {
            return WorkItemTypes.ToToken(type) + " :: " + name.Trim();
        }

        /// <summary>Strips the <c>{TYPE} ::</c> prefix and any review suffix from a title.</summary>
        public static string ShortName(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var t = Regex.Replace(title!, @"^\s*(THEME|EPIC|FEATURE|STORY|TASK|BUG)\s*::\s*", string.Empty, RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s*::\s*HUMAN APPROVAL REQUIRED\s*$", string.Empty, RegexOptions.IgnoreCase);
            return t.Trim();
        }

        /// <summary>Canonical id: <c>{TYPE}-{slug}</c>; lower-case slug, hyphen-separated, max 60 chars.</summary>
        public static string MakeId(WorkItemType type, string name)
        {
            return WorkItemTypes.ToToken(type) + "-" + Slug(name);
        }

        public static string Slug(string? text, int maxLength = 60)
        {
            if (string.IsNullOrWhiteSpace(text)) return "item";
            var s = text!.ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9]+", "-").Trim('-');
            if (s.Length > maxLength) s = s.Substring(0, maxLength).TrimEnd('-');
            return s.Length == 0 ? "item" : s;
        }
    }
}
