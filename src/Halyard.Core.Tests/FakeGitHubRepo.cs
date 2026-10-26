using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Tests
{
    /// <summary>Stateful fake of one GitHub repository's issue, read-by-number and GraphQL node/delete endpoints.</summary>
    public sealed class FakeGitHubRepo
    {
        private readonly Dictionary<long, (string Title, List<string> Labels)> _issues = new Dictionary<long, (string, List<string>)>();
        public FakeTransport Http { get; } = new FakeTransport();
        public string Repo { get; }
        public string? NodeRepositoryOverride { get; set; }
        public bool IgnoreLabelFilter { get; set; }

        public FakeGitHubRepo(string repo)
        {
            Repo = repo;
            Http.On("GET", "/repos/" + repo + "/issues?", call =>
            {
                var wanted = new List<string>();
                var q = call.Url.IndexOf("labels=", StringComparison.Ordinal);
                if (q >= 0 && !IgnoreLabelFilter)
                {
                    var raw = call.Url.Substring(q + 7);
                    var amp = raw.IndexOf('&');
                    wanted = Uri.UnescapeDataString(amp < 0 ? raw : raw.Substring(0, amp)).Split(',').ToList();
                }
                var rows = _issues.Where(kv => wanted.All(w => kv.Value.Labels.Contains(w))).Select(kv => Json(kv.Key)).ToList();
                return new HttpReply(200, new JArray(rows).ToString(), new Dictionary<string, string>());
            });
            Http.On("GET", "/repos/" + repo + "/issues/", call =>
            {
                var n = long.Parse(call.Url.Substring(call.Url.LastIndexOf('/') + 1));
                return _issues.ContainsKey(n) ? new HttpReply(200, Json(n).ToString(), new Dictionary<string, string>()) : new HttpReply(404, "{\"message\":\"Not Found\"}", new Dictionary<string, string>());
            });
            Http.On("POST", "/graphql", call =>
            {
                var body = JObject.Parse(call.JsonBody!);
                var id = (string)body["variables"]!["id"]!;
                var n = long.Parse(id.Substring(2));
                var query = (string)body["query"]!;
                if (query.StartsWith("query"))
                {
                    var node = _issues.ContainsKey(n) ? new JObject { ["number"] = n, ["repository"] = new JObject { ["nameWithOwner"] = NodeRepositoryOverride ?? Repo } } : null;
                    return new HttpReply(200, new JObject { ["data"] = new JObject { ["node"] = node } }.ToString(), new Dictionary<string, string>());
                }
                if (!_issues.Remove(n)) return new HttpReply(200, "{\"errors\":[{\"message\":\"Could not resolve to a node\"}]}", new Dictionary<string, string>());
                return new HttpReply(200, "{\"data\":{\"deleteIssue\":{}}}", new Dictionary<string, string>());
            });
        }

        public void Add(long number, string title, params string[] labels)
        {
            _issues[number] = (title, labels.ToList());
        }

        public bool Exists(long number)
        {
            return _issues.ContainsKey(number);
        }

        private JObject Json(long n)
        {
            var i = _issues[n];
            return new JObject
            {
                ["number"] = n, ["id"] = 1000 + n, ["node_id"] = "I_" + n, ["title"] = i.Title, ["body"] = "", ["state"] = "open",
                ["labels"] = new JArray(i.Labels.Select(l => new JObject { ["name"] = l })),
            };
        }
    }
}
