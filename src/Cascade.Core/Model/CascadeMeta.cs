using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cascade.Core.Model
{
    /// <summary>
    /// The <c>~~~cascade-meta</c> block that makes a work item machine-readable on any Git provider.
    /// Keys are lower-case, insertion-ordered, and compared case-insensitively. The block is the
    /// source of truth for type, canonical id and parent; labels and native links are mirrors.
    /// </summary>
    public sealed class CascadeMeta
    {
        public const string Fence = "~~~cascade-meta";
        public const string FenceEnd = "~~~";

        private static readonly Regex FenceRegex = new Regex(@"~~~cascade-meta[ \t]*\r?\n(?<body>.*?)\r?\n[ \t]*~~~", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex CommentRegex = new Regex(@"<!--\s*(?<body>.*?)\s*-->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex LineRegex = new Regex(@"^\s*(?<key>[A-Za-z0-9_\-\.]+)\s*:\s*(?<value>.*?)\s*$", RegexOptions.Compiled);

        private readonly List<KeyValuePair<string, string>> _entries = new List<KeyValuePair<string, string>>();

        public CascadeMeta() { }

        public CascadeMeta(IEnumerable<KeyValuePair<string, string>> entries)
        {
            foreach (var e in entries) Set(e.Key, e.Value);
        }

        public int Count { get { return _entries.Count; } }

        public IReadOnlyList<KeyValuePair<string, string>> Entries { get { return _entries; } }

        public string? this[string key]
        {
            get { return Get(key); }
            set { Set(key, value); }
        }

        public bool Has(string key)
        {
            return !string.IsNullOrWhiteSpace(Get(key));
        }

        public string? Get(string key)
        {
            var k = Normalize(key);
            foreach (var e in _entries)
            {
                if (e.Key == k) return e.Value;
            }
            return null;
        }

        /// <summary>Sets a value. A null value removes the key; an empty string keeps the key with an empty value (used for TBD placeholders).</summary>
        public CascadeMeta Set(string key, string? value)
        {
            var k = Normalize(key);
            if (value == null)
            {
                _entries.RemoveAll(e => e.Key == k);
                return this;
            }
            var v = value.Trim();
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Key == k)
                {
                    _entries[i] = new KeyValuePair<string, string>(k, v);
                    return this;
                }
            }
            _entries.Add(new KeyValuePair<string, string>(k, v));
            return this;
        }

        public CascadeMeta Remove(string key)
        {
            return Set(key, null);
        }

        // Typed accessors for the well-known keys.
        public WorkItemType Type { get { return WorkItemTypes.Parse(Get("type")); } set { Set("type", WorkItemTypes.ToToken(value)); } }
        public string? Id { get { return Get("id"); } set { Set("id", value); } }
        public string? Parent { get { return Get("parent"); } set { Set("parent", value); } }
        public string? Theme { get { return Get("theme"); } set { Set("theme", value); } }
        public string? State { get { return Get("state"); } set { Set("state", value); } }
        public string? Status { get { return Get("status"); } set { Set("status", value); } }
        public string? Area { get { return Get("area"); } set { Set("area", value); } }
        public string? Effort { get { return Get("effort"); } set { Set("effort", value); } }
        public string? Points { get { return Get("points") ?? Get("story_points"); } set { Set("points", value); } }
        public string? Sprint { get { return Get("sprint"); } set { Set("sprint", value); } }
        public string? Milestone { get { return Get("milestone"); } set { Set("milestone", value); } }
        public string? SourceProject { get { return Get("source_project"); } set { Set("source_project", value); } }
        public string? SourceRef { get { return Get("source_iid") ?? Get("source_ref"); } set { Set("source_iid", value); } }

        /// <summary>True when the block identifies a work item: a known type and a non-empty id.</summary>
        public bool IsValid
        {
            get { return Type != WorkItemType.Unclassified && !string.IsNullOrWhiteSpace(Id); }
        }

        /// <summary>
        /// Parses the first cascade-meta fence in a body; falls back to the legacy HTML-comment block.
        /// Returns null when no block with a <c>type</c> or <c>id</c> key is present.
        /// </summary>
        public static CascadeMeta? Parse(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            var m = FenceRegex.Match(body);
            string? block = null;
            if (m.Success) block = m.Groups["body"].Value;
            else
            {
                var c = CommentRegex.Match(body);
                if (c.Success) block = c.Groups["body"].Value;
            }
            if (block == null) return null;
            var meta = ParseBlock(block);
            return meta.Has("type") || meta.Has("id") ? meta : null;
        }

        public static CascadeMeta ParseBlock(string block)
        {
            var meta = new CascadeMeta();
            foreach (var raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var lm = LineRegex.Match(line);
                if (!lm.Success) continue;
                meta.Set(lm.Groups["key"].Value, lm.Groups["value"].Value);
            }
            return meta;
        }

        /// <summary>Renders the fenced block. Key order is insertion order so diffs stay readable.</summary>
        public string ToFence()
        {
            var sb = new StringBuilder();
            sb.Append(Fence).Append('\n');
            foreach (var e in _entries)
            {
                sb.Append(e.Key).Append(": ").Append(e.Value).Append('\n');
            }
            sb.Append(FenceEnd);
            return sb.ToString();
        }

        public CascadeMeta Clone()
        {
            return new CascadeMeta(_entries);
        }

        /// <summary>Removes any cascade-meta fence or leading HTML-comment block from a body.</summary>
        public static string StripFromBody(string? body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            var text = FenceRegex.Replace(body!, string.Empty, 1);
            text = Regex.Replace(text, @"\A\s*<!--.*?-->\s*", string.Empty, RegexOptions.Singleline);
            return text.Trim();
        }

        /// <summary>Replaces (or inserts at the top) the fence in a body, preserving the rest of the text.</summary>
        public static string ApplyToBody(string? body, CascadeMeta meta)
        {
            var rest = StripFromBody(body);
            return rest.Length == 0 ? meta.ToFence() : meta.ToFence() + "\n\n" + rest;
        }

        private static string Normalize(string key)
        {
            if (key == null) throw new ArgumentNullException("key");
            return key.Trim().ToLowerInvariant();
        }

        public override string ToString()
        {
            return ToFence();
        }

        public Dictionary<string, string> ToDictionary()
        {
            return _entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        }
    }
}
