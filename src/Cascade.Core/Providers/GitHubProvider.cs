using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Model;
using Newtonsoft.Json.Linq;

namespace Cascade.Core.Providers
{
    /// <summary>
    /// GitHub REST v3 adapter (plus one GraphQL mutation for issue deletion, which REST lacks).
    /// Repository identifier is <c>owner/repo</c>. Pull requests are filtered out of issue listings.
    /// </summary>
    public sealed class GitHubProvider : RestProviderBase, IWorkItemProvider
    {
        private static readonly Regex NextLink = new Regex(@"<(?<url>[^>]+)>;\s*rel=""next""", RegexOptions.Compiled);
        private const int PageSize = 100;

        public GitHubProvider(IHttpTransport http, string baseUrl, string? token, RepositoryRef repository, int writeDelayMs = -1)
            : base(http, baseUrl, token, writeDelayMs)
        {
            if (repository.Provider != ProviderKind.GitHub) throw new ArgumentException("Repository is not a GitHub repository.", "repository");
            if (!IsValidIdentifier(repository.Identifier)) throw new ArgumentException("GitHub repositories are identified as owner/repo (letters, digits, '-', '_', '.'); got '" + repository.Identifier + "'.", "repository");
            Repository = repository;
            Capabilities = new ProviderCapabilities { DeleteIssues = true, NativeParentLinks = true, RecommendedWriteDelayMs = DefaultWriteDelayMs };
        }

        public ProviderKind Kind { get { return ProviderKind.GitHub; } }
        public RepositoryRef Repository { get; private set; }
        public ProviderCapabilities Capabilities { get; private set; }
        protected override int DefaultWriteDelayMs { get { return 900; } }

        private string RepoApi { get { var parts = Repository.Identifier.Split('/'); return BaseUrl + "/repos/" + Esc(parts[0]) + "/" + Esc(parts[1]); } }

        /// <summary>owner/repo only; no dot segments, so a crafted name can never resolve to another repository's URL.</summary>
        public static bool IsValidIdentifier(string identifier)
        {
            var m = System.Text.RegularExpressions.Regex.Match(identifier ?? string.Empty, @"^([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/([A-Za-z0-9._-]{1,100})$");
            return m.Success && m.Groups[2].Value != "." && m.Groups[2].Value != "..";
        }

        protected override void Authenticate(HttpCall call)
        {
            if (!string.IsNullOrWhiteSpace(Token)) call.Headers["Authorization"] = "Bearer " + Token!.Trim();
            call.Headers["Accept"] = "application/vnd.github+json";
            call.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        }

        public IWorkItemWriter OpenWriter(RepositoryRef target)
        {
            // The writer is a separate object so a provider can never be cast to a writer; only the
            // write gateway calls this, after the policy check.
            return new Writer(target.Equals(Repository) ? this : new GitHubProvider(Http, BaseUrl, Token, target, WriteDelayMs));
        }

        private sealed class Writer : IWorkItemWriter
        {
            private readonly GitHubProvider _p;
            public Writer(GitHubProvider p) { _p = p; }
            public RepositoryRef Repository { get { return _p.Repository; } }
            public Task EnsureLabelAsync(LabelDefinition label, CancellationToken c) { return _p.WriteEnsureLabelAsync(label, c); }
            public Task<WorkItem> CreateAsync(WorkItemDraft draft, CancellationToken c) { return _p.WriteCreateAsync(draft, c); }
            public Task<WorkItem> UpdateAsync(long number, WorkItemPatch patch, CancellationToken c) { return _p.WriteUpdateAsync(number, patch, c); }
            public Task<bool> DeleteAsync(WorkItem item, CancellationToken c) { return _p.WriteDeleteAsync(item, c); }
            public Task<WorkItem?> GetAsync(long number, CancellationToken c) { return _p.GetWorkItemAsync(number, c); }
            public Task<bool> LinkParentAsync(WorkItem child, WorkItem parent, CancellationToken c) { return _p.WriteLinkParentAsync(child, parent, c); }
        }

        public IWorkItemReader ForRepository(RepositoryRef repository)
        {
            return repository.Equals(Repository) ? this : new GitHubProvider(Http, BaseUrl, Token, repository, WriteDelayMs);
        }

        public async Task<ProviderIdentity> TestConnectionAsync(CancellationToken cancellation)
        {
            var user = await GetJsonAsync(BaseUrl + "/user", cancellation).ConfigureAwait(false);
            // Also confirm the repository is reachable with this token.
            await GetJsonAsync(RepoApi, cancellation).ConfigureAwait(false);
            return new ProviderIdentity
            {
                Provider = Kind,
                Login = (string?)user["login"] ?? string.Empty,
                Name = (string?)user["name"],
                Url = (string?)user["html_url"],
                ApiBase = BaseUrl,
            };
        }

        public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(int max, CancellationToken cancellation)
        {
            var result = new List<RepositoryRef>();
            var url = BaseUrl + "/user/repos?per_page=" + PageSize + "&sort=updated&affiliation=owner,collaborator,organization_member";
            while (url != null && result.Count < max)
            {
                var reply = await SendAsync(NewCall("GET", url), cancellation).ConfigureAwait(false);
                foreach (var r in ParseJson(reply.Body))
                {
                    var full = (string?)r["full_name"];
                    if (string.IsNullOrWhiteSpace(full)) continue;
                    result.Add(new RepositoryRef(Kind, full!, full, (string?)r["html_url"]));
                    if (result.Count >= max) break;
                }
                url = Next(reply);
            }
            return result;
        }

        public async Task<IReadOnlyList<string>> ListLabelsAsync(CancellationToken cancellation)
        {
            var result = new List<string>();
            var url = RepoApi + "/labels?per_page=" + PageSize;
            while (url != null)
            {
                var reply = await SendAsync(NewCall("GET", url), cancellation).ConfigureAwait(false);
                foreach (var l in ParseJson(reply.Body))
                {
                    var name = (string?)l["name"];
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
                }
                url = Next(reply);
            }
            return result;
        }

        public async Task<IReadOnlyList<WorkItem>> ListWorkItemsAsync(WorkItemQuery query, IProgress<string>? progress, CancellationToken cancellation)
        {
            var items = new List<WorkItem>();
            var state = query.State == null ? "all" : (query.State == WorkItemState.Open ? "open" : "closed");
            var url = RepoApi + "/issues?state=" + state + "&per_page=" + PageSize + "&sort=updated&direction=desc";
            if (query.Labels.Count > 0) url += "&labels=" + Esc(string.Join(",", query.Labels));
            if (query.UpdatedAfter != null) url += "&since=" + Esc(query.UpdatedAfter.Value.UtcDateTime.ToString("o"));
            var page = 0;
            while (url != null && items.Count < query.MaxItems)
            {
                cancellation.ThrowIfCancellationRequested();
                page++;
                var reply = await SendAsync(NewCall("GET", url), cancellation).ConfigureAwait(false);
                foreach (var j in ParseJson(reply.Body))
                {
                    if (j["pull_request"] != null) continue;
                    items.Add(ToWorkItem(j));
                    if (items.Count >= query.MaxItems) break;
                }
                if (progress != null) progress.Report("Fetched page " + page + " (" + items.Count + " work items)");
                url = Next(reply);
            }
            return items;
        }

        public async Task<string> GetRepositoryIdAsync(CancellationToken cancellation)
        {
            var j = await GetJsonAsync(RepoApi, cancellation).ConfigureAwait(false);
            var id = (long?)j["id"];
            if (id == null) throw new ProviderHttpException(0, "Repository " + Repository.Key + " returned no id.", null);
            return id.Value.ToString();
        }

        public async Task<WorkItem?> GetWorkItemAsync(long number, CancellationToken cancellation)
        {
            try
            {
                var j = await GetJsonAsync(RepoApi + "/issues/" + number, cancellation).ConfigureAwait(false);
                return j["pull_request"] != null ? null : ToWorkItem(j);
            }
            catch (ProviderHttpException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        // ---- writer (reachable only through WriteGateway) ----

        internal async Task WriteEnsureLabelAsync(LabelDefinition label, CancellationToken cancellation)
        {
            var payload = new JObject { ["name"] = label.Name, ["color"] = label.Color };
            if (!string.IsNullOrEmpty(label.Description)) payload["description"] = Truncate(label.Description!, 100);
            try
            {
                await SendJsonAsync("POST", RepoApi + "/labels", payload, cancellation).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex) when (ex.Status == 422 || ex.Status == 409)
            {
                // already exists
            }
        }

        internal async Task<WorkItem> WriteCreateAsync(WorkItemDraft draft, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            var payload = new JObject { ["title"] = draft.Title, ["body"] = draft.Body, ["labels"] = new JArray(draft.Labels) };
            var reply = await SendJsonAsync("POST", RepoApi + "/issues", payload, cancellation).ConfigureAwait(false);
            return ToWorkItem(ParseJson(reply.Body));
        }

        internal async Task<WorkItem> WriteUpdateAsync(long number, WorkItemPatch patch, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            var payload = new JObject();
            if (patch.Title != null) payload["title"] = patch.Title;
            if (patch.Body != null) payload["body"] = patch.Body;
            if (patch.Labels != null) payload["labels"] = new JArray(patch.Labels);
            if (patch.State != null) payload["state"] = patch.State == WorkItemState.Closed ? "closed" : "open";
            var reply = await SendJsonAsync("PATCH", RepoApi + "/issues/" + number, payload, cancellation).ConfigureAwait(false);
            return ToWorkItem(ParseJson(reply.Body));
        }

        /// <summary>
        /// Deletes issue <paramref name="item"/>.Number in the bound repository. The node id is always taken from a
        /// fresh read of this repository, and confirmed through GraphQL to belong to this repository and number
        /// before the mutation, so a transferred or mismatched issue is never deleted. Returns false if already gone.
        /// </summary>
        internal async Task<bool> WriteDeleteAsync(WorkItem item, CancellationToken cancellation)
        {
            var fresh = await GetWorkItemAsync(item.Number, cancellation).ConfigureAwait(false);
            if (fresh == null || string.IsNullOrEmpty(fresh.ProviderId)) return false;
            var nodeId = fresh.ProviderId!;
            var check = new JObject
            {
                ["query"] = "query($id: ID!) { node(id: $id) { ... on Issue { number repository { nameWithOwner } } } }",
                ["variables"] = new JObject { ["id"] = nodeId },
            };
            var checkReply = await SendJsonAsync("POST", BaseUrl + "/graphql", check, cancellation).ConfigureAwait(false);
            var node = (ParseJson(checkReply.Body) as JObject)?["data"]?["node"];
            var owner = (string?)node?["repository"]?["nameWithOwner"];
            var number = (long?)node?["number"];
            if (node == null || node.Type == JTokenType.Null) return false;
            if (!string.Equals(owner, Repository.Identifier, StringComparison.OrdinalIgnoreCase) || number != item.Number)
                throw new ProviderHttpException(409, "Refusing to delete: node " + nodeId + " is " + (owner ?? "?") + "#" + (number?.ToString() ?? "?") + ", not " + Repository.Identifier + "#" + item.Number + ".", null);
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            var payload = new JObject
            {
                ["query"] = "mutation($id: ID!) { deleteIssue(input: { issueId: $id }) { clientMutationId } }",
                ["variables"] = new JObject { ["id"] = nodeId },
            };
            var reply = await SendJsonAsync("POST", BaseUrl + "/graphql", payload, cancellation).ConfigureAwait(false);
            var errors = (ParseJson(reply.Body) as JObject)?["errors"] as JArray;
            if (errors != null && errors.Count > 0)
            {
                var msg = string.Join("; ", errors.Select(e => (string?)e["message"] ?? "unknown"));
                if (msg.IndexOf("Could not resolve", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                throw new ProviderHttpException(reply.Status, "GitHub refused to delete issue #" + item.Number + ": " + msg, reply.Body);
            }
            return true;
        }

        internal async Task<bool> WriteLinkParentAsync(WorkItem child, WorkItem parent, CancellationToken cancellation)
        {
            var childId = child.ProviderNumericId;
            if (childId == null)
            {
                var fresh = await GetWorkItemAsync(child.Number, cancellation).ConfigureAwait(false);
                childId = fresh == null ? null : fresh.ProviderNumericId;
            }
            if (childId == null) return false;
            try
            {
                await PaceWriteAsync(cancellation).ConfigureAwait(false);
                var payload = new JObject { ["sub_issue_id"] = childId.Value };
                await SendJsonAsync("POST", RepoApi + "/issues/" + parent.Number + "/sub_issues", payload, cancellation).ConfigureAwait(false);
                return true;
            }
            catch (ProviderHttpException ex) when (ex.Status == 404 || ex.Status == 422 || ex.Status == 403 || ex.Status == 410)
            {
                return false; // sub-issues unavailable on this repo/plan, or already linked
            }
        }

        private WorkItem ToWorkItem(JToken j)
        {
            var number = (long?)j["number"] ?? 0;
            var item = new WorkItem(Repository, number)
            {
                ProviderId = (string?)j["node_id"],
                Title = (string?)j["title"] ?? string.Empty,
                Body = (string?)j["body"] ?? string.Empty,
                State = StateFromText((string?)j["state"]),
                Url = (string?)j["html_url"],
                Milestone = j["milestone"] == null || j["milestone"]!.Type == JTokenType.Null ? null : (string?)j["milestone"]!["title"],
                CreatedAt = ParseDate(j["created_at"]),
                UpdatedAt = ParseDate(j["updated_at"]),
            };
            item.Labels.AddRange(ReadLabels(j["labels"]));
            var assignees = j["assignees"] as JArray;
            if (assignees != null) item.Assignees.AddRange(assignees.Select(a => (string?)a["login"]).Where(a => !string.IsNullOrEmpty(a))!);
            item.ProviderNumericId = (long?)j["id"];
            return item;
        }

        private static string? Next(HttpReply reply)
        {
            var link = reply.Header("Link");
            if (string.IsNullOrEmpty(link)) return null;
            var m = NextLink.Match(link!);
            return m.Success ? m.Groups["url"].Value : null;
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
