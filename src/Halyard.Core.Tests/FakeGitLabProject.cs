using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Tests
{
    /// <summary>
    /// Stateful fake of one GitLab project: issues by iid (paged with X-Next-Page like the real API), labels,
    /// the current user and the project record. Every request goes through <see cref="Http"/> so tests can
    /// inspect URLs, headers and bodies.
    /// </summary>
    public sealed class FakeGitLabProject
    {
        private sealed class Issue
        {
            public long Iid;
            public string Title = string.Empty;
            public string Description = string.Empty;
            public string State = "opened";
            public List<string> Labels = new List<string>();
            public string? Milestone;
            public List<string> Assignees = new List<string>();
        }

        private readonly Dictionary<long, Issue> _issues = new Dictionary<long, Issue>();
        private readonly List<string> _labels = new List<string>();
        private long _next = 1;
        public FakeTransport Http { get; } = new FakeTransport();
        public string ProjectPath { get; }
        public long ProjectId { get; }
        public int PageSize { get; set; } = 100;

        /// <summary>Addresses the project by its url-encoded path instead of its numeric id.</summary>
        public FakeGitLabProject(long projectId, string projectPath, bool addressByPath = false)
        {
            ProjectId = projectId;
            ProjectPath = projectPath;
            var project = "/api/v4/projects/" + (addressByPath ? Uri.EscapeDataString(projectPath) : projectId.ToString());
            Http.On("GET", "/api/v4/user", 200, new JObject { ["username"] = "halyard-bot", ["name"] = "Halyard Bot", ["web_url"] = "https://gitlab.example.com/halyard-bot" }.ToString());
            Http.On("GET", project + "/issues?", call => Paged(call, Filter(call).Select(Json).ToList()));
            Http.On("GET", project + "/issues/", call =>
            {
                var n = long.Parse(call.Url.Substring(call.Url.LastIndexOf('/') + 1));
                return _issues.ContainsKey(n) ? Ok(Json(_issues[n]).ToString()) : NotFound();
            });
            Http.On("GET", project + "/labels", call => Paged(call, _labels.Select(l => (JToken)new JObject { ["name"] = l }).ToList()));
            Http.On("POST", project + "/labels", call =>
            {
                var body = JObject.Parse(call.JsonBody!);
                var name = (string)body["name"]!;
                if (_labels.Contains(name)) return new HttpReply(409, "{\"message\":\"Label already exists\"}", new Dictionary<string, string>());
                _labels.Add(name);
                return new HttpReply(201, body.ToString(), new Dictionary<string, string>());
            });
            Http.On("POST", project + "/issues", call =>
            {
                var body = JObject.Parse(call.JsonBody!);
                var issue = new Issue { Iid = _next++, Title = (string)body["title"]!, Description = (string?)body["description"] ?? string.Empty };
                issue.Labels = SplitLabels((string?)body["labels"]);
                _issues[issue.Iid] = issue;
                return new HttpReply(201, Json(issue).ToString(), new Dictionary<string, string>());
            });
            Http.On("PUT", project + "/issues/", call =>
            {
                var n = long.Parse(call.Url.Substring(call.Url.LastIndexOf('/') + 1));
                if (!_issues.ContainsKey(n)) return NotFound();
                var body = JObject.Parse(call.JsonBody!);
                var issue = _issues[n];
                if (body["title"] != null) issue.Title = (string)body["title"]!;
                if (body["description"] != null) issue.Description = (string)body["description"]!;
                if (body["labels"] != null) issue.Labels = SplitLabels((string)body["labels"]!);
                var ev = (string?)body["state_event"];
                if (ev == "close") issue.State = "closed";
                if (ev == "reopen") issue.State = "opened";
                return Ok(Json(issue).ToString());
            });
            Http.On("DELETE", project + "/issues/", call =>
            {
                var n = long.Parse(call.Url.Substring(call.Url.LastIndexOf('/') + 1));
                return _issues.Remove(n) ? new HttpReply(204, string.Empty, new Dictionary<string, string>()) : NotFound();
            });
            Http.On("GET", project, 200, new JObject { ["id"] = projectId, ["path_with_namespace"] = projectPath }.ToString());
            Http.On("GET", "/api/v4/projects?", call => Paged(call, new List<JToken> { new JObject { ["id"] = projectId, ["path_with_namespace"] = projectPath, ["web_url"] = "https://gitlab.example.com/" + projectPath } }));
        }

        public long Add(string title, string description = "", string state = "opened", params string[] labels)
        {
            var issue = new Issue { Iid = _next++, Title = title, Description = description, State = state, Labels = labels.ToList() };
            _issues[issue.Iid] = issue;
            return issue.Iid;
        }

        public bool Exists(long iid) { return _issues.ContainsKey(iid); }
        public string BodyOf(long iid) { return _issues[iid].Description; }
        public string StateOf(long iid) { return _issues[iid].State; }
        public IReadOnlyList<string> LabelsOf(long iid) { return _issues[iid].Labels; }
        public int Count { get { return _issues.Count; } }
        public IReadOnlyList<string> KnownLabels { get { return _labels; } }
        public void SetMilestone(long iid, string title) { _issues[iid].Milestone = title; }
        public void SetAssignees(long iid, params string[] users) { _issues[iid].Assignees = users.ToList(); }

        private static List<string> SplitLabels(string? labels)
        {
            return string.IsNullOrEmpty(labels) ? new List<string>() : labels.Split(',').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        private IEnumerable<Issue> Filter(HttpCall call)
        {
            var query = ParseQuery(call.Url);
            IEnumerable<Issue> rows = _issues.Values.OrderBy(i => i.Iid);
            string? state;
            if (query.TryGetValue("state", out state) && state != "all") rows = rows.Where(i => i.State == state);
            string? labels;
            if (query.TryGetValue("labels", out labels)) { var wanted = SplitLabels(labels); rows = rows.Where(i => wanted.All(w => i.Labels.Contains(w))); }
            return rows;
        }

        private HttpReply Paged(HttpCall call, List<JToken> rows)
        {
            var query = ParseQuery(call.Url);
            var page = query.ContainsKey("page") ? int.Parse(query["page"]) : 1;
            var slice = rows.Skip((page - 1) * PageSize).Take(PageSize).ToList();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["X-Next-Page"] = page * PageSize < rows.Count ? (page + 1).ToString() : string.Empty;
            return new HttpReply(200, new JArray(slice).ToString(), headers);
        }

        private HttpReply Paged(HttpCall call, List<JObject> rows)
        {
            return Paged(call, rows.Cast<JToken>().ToList());
        }

        private static HttpReply Ok(string body) { return new HttpReply(200, body, new Dictionary<string, string>()); }
        private static HttpReply NotFound() { return new HttpReply(404, "{\"message\":\"404 Not found\"}", new Dictionary<string, string>()); }

        private static Dictionary<string, string> ParseQuery(string url)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var q = url.IndexOf('?');
            if (q < 0) return result;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) result[pair.Substring(0, eq)] = Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return result;
        }

        private static JObject Json(Issue i)
        {
            var o = new JObject
            {
                ["iid"] = i.Iid, ["id"] = 9000 + i.Iid, ["title"] = i.Title, ["description"] = i.Description, ["state"] = i.State,
                ["labels"] = new JArray(i.Labels.Cast<object>().ToArray()), ["web_url"] = "https://gitlab.example.com/x/-/issues/" + i.Iid,
                ["created_at"] = "2026-10-01T10:00:00Z", ["updated_at"] = "2026-10-02T10:00:00Z",
                ["assignees"] = new JArray(i.Assignees.Select(a => (object)new JObject { ["username"] = a }).ToArray()),
            };
            o["milestone"] = i.Milestone == null ? JValue.CreateNull() : new JObject { ["title"] = i.Milestone };
            return o;
        }
    }
}
