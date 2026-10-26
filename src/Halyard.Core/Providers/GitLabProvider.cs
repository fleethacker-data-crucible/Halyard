using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Halyard.Core.Serialization;

namespace Halyard.Core.Providers
{
    /// <summary>
    /// GitLab REST v4 adapter (gitlab.com or any self-hosted instance). Project identifier is a numeric id or a
    /// URL-encodable <c>group/project</c> path. Pagination follows the <c>X-Next-Page</c> header.
    /// Native parent links are not used: halyard-meta is the hierarchy source of truth.
    /// </summary>
    public sealed class GitLabProvider : RestProviderBase, IWorkItemProvider
    {
        private const int PageSize = 100;

        public GitLabProvider(IHttpTransport http, string baseUrl, string? token, RepositoryRef repository, int writeDelayMs = -1)
            : base(http, NormalizeBase(baseUrl), token, writeDelayMs)
        {
            if (repository.Provider != ProviderKind.GitLab) throw new ArgumentException("Repository is not a GitLab project.", "repository");
            if (!IsValidIdentifier(repository.Identifier)) throw new ArgumentException("GitLab projects are a numeric id or a group/project path without '.' or '..' segments; got '" + repository.Identifier + "'.", "repository");
            Repository = repository;
            Capabilities = new ProviderCapabilities { DeleteIssues = true, NativeParentLinks = false, RecommendedWriteDelayMs = DefaultWriteDelayMs };
        }

        public static bool IsValidIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return false;
            if (System.Text.RegularExpressions.Regex.IsMatch(identifier, @"^\d+$")) return true;
            var segments = identifier.Split('/');
            return segments.Length >= 2 && segments.All(seg => seg.Length > 0 && seg != "." && seg != ".." && System.Text.RegularExpressions.Regex.IsMatch(seg, @"^[A-Za-z0-9._-]+$"));
        }

        private static string NormalizeBase(string baseUrl)
        {
            var b = baseUrl.TrimEnd('/');
            return b.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) ? b : b + "/api/v4";
        }

        public ProviderKind Kind { get { return ProviderKind.GitLab; } }
        public RepositoryRef Repository { get; private set; }
        public ProviderCapabilities Capabilities { get; private set; }
        protected override int DefaultWriteDelayMs { get { return 250; } }

        private string ProjectApi { get { return BaseUrl + "/projects/" + Esc(Repository.Identifier); } }

        protected override void Authenticate(HttpCall call)
        {
            if (!string.IsNullOrWhiteSpace(Token)) call.Headers["PRIVATE-TOKEN"] = Token!.Trim();
        }

        public IWorkItemWriter OpenWriter(RepositoryRef target)
        {
            // The writer is a separate object so a provider can never be cast to a writer; only the
            // write gateway calls this, after the policy check.
            return new Writer(target.Equals(Repository) ? this : new GitLabProvider(Http, BaseUrl, Token, target, WriteDelayMs));
        }

        private sealed class Writer : IWorkItemWriter
        {
            private readonly GitLabProvider _p;
            public Writer(GitLabProvider p) { _p = p; }
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
            return repository.Equals(Repository) ? this : new GitLabProvider(Http, BaseUrl, Token, repository, WriteDelayMs);
        }

        public async Task<ProviderIdentity> TestConnectionAsync(CancellationToken cancellation)
        {
            var user = await GetJsonAsync(BaseUrl + "/user", cancellation).ConfigureAwait(false);
            await GetJsonAsync(ProjectApi, cancellation).ConfigureAwait(false);
            return new ProviderIdentity
            {
                Provider = Kind,
                Login = (string?)user["username"] ?? string.Empty,
                Name = (string?)user["name"],
                Url = (string?)user["web_url"],
                ApiBase = BaseUrl,
            };
        }

        public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(int max, CancellationToken cancellation)
        {
            var result = new List<RepositoryRef>();
            var page = 1;
            while (result.Count < max && page > 0)
            {
                var url = BaseUrl + "/projects?membership=true&simple=true&order_by=last_activity_at&sort=desc&per_page=" + PageSize + "&page=" + page;
                var reply = await SendAsync(NewCall("GET", url), cancellation).ConfigureAwait(false);
                foreach (var p in ParseJson(reply.Body))
                {
                    var id = (long?)p["id"];
                    if (id == null) continue;
                    result.Add(new RepositoryRef(Kind, id.Value.ToString(), (string?)p["path_with_namespace"] ?? id.Value.ToString(), (string?)p["web_url"]));
                    if (result.Count >= max) break;
                }
                page = NextPage(reply);
            }
            return result;
        }

        public async Task<IReadOnlyList<string>> ListLabelsAsync(CancellationToken cancellation)
        {
            var result = new List<string>();
            var page = 1;
            while (page > 0)
            {
                var reply = await SendAsync(NewCall("GET", ProjectApi + "/labels?per_page=" + PageSize + "&page=" + page), cancellation).ConfigureAwait(false);
                foreach (var l in ParseJson(reply.Body))
                {
                    var name = (string?)l["name"];
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
                }
                page = NextPage(reply);
            }
            return result;
        }

        public async Task<IReadOnlyList<WorkItem>> ListWorkItemsAsync(WorkItemQuery query, IProgress<string>? progress, CancellationToken cancellation)
        {
            var items = new List<WorkItem>();
            var state = query.State == null ? "all" : (query.State == WorkItemState.Open ? "opened" : "closed");
            var baseUrl = ProjectApi + "/issues?state=" + state + "&per_page=" + PageSize + "&order_by=updated_at&sort=desc";
            if (query.Labels.Count > 0) baseUrl += "&labels=" + Esc(string.Join(",", query.Labels));
            if (query.UpdatedAfter != null) baseUrl += "&updated_after=" + Esc(query.UpdatedAfter.Value.UtcDateTime.ToString("o"));
            var page = 1;
            while (page > 0 && items.Count < query.MaxItems)
            {
                cancellation.ThrowIfCancellationRequested();
                var reply = await SendAsync(NewCall("GET", baseUrl + "&page=" + page), cancellation).ConfigureAwait(false);
                foreach (var j in ParseJson(reply.Body))
                {
                    items.Add(ToWorkItem(j));
                    if (items.Count >= query.MaxItems) break;
                }
                if (progress != null) progress.Report("Fetched page " + page + " (" + items.Count + " work items)");
                page = NextPage(reply);
            }
            return items;
        }

        public async Task<string> GetRepositoryIdAsync(CancellationToken cancellation)
        {
            var j = await GetJsonAsync(ProjectApi, cancellation).ConfigureAwait(false);
            var id = (long?)j["id"];
            if (id == null) throw new ProviderHttpException(0, "Repository " + Repository.Key + " returned no id.", null);
            return id.Value.ToString();
        }

        public async Task<WorkItem?> GetWorkItemAsync(long number, CancellationToken cancellation)
        {
            try
            {
                return ToWorkItem(await GetJsonAsync(ProjectApi + "/issues/" + number, cancellation).ConfigureAwait(false));
            }
            catch (ProviderHttpException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        // ---- writer (reachable only through WriteGateway) ----

        internal async Task WriteEnsureLabelAsync(LabelDefinition label, CancellationToken cancellation)
        {
            var payload = new JObject { ["name"] = label.Name, ["color"] = "#" + label.Color };
            if (!string.IsNullOrEmpty(label.Description)) payload["description"] = label.Description;
            try
            {
                await SendJsonAsync("POST", ProjectApi + "/labels", payload, cancellation).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex) when (ex.Status == 409 || ex.Status == 400)
            {
                // exists
            }
        }

        internal async Task<WorkItem> WriteCreateAsync(WorkItemDraft draft, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            var payload = new JObject { ["title"] = draft.Title, ["description"] = draft.Body, ["labels"] = string.Join(",", draft.Labels) };
            var reply = await SendJsonAsync("POST", ProjectApi + "/issues", payload, cancellation).ConfigureAwait(false);
            return ToWorkItem(ParseJson(reply.Body));
        }

        internal async Task<WorkItem> WriteUpdateAsync(long number, WorkItemPatch patch, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            var payload = new JObject();
            if (patch.Title != null) payload["title"] = patch.Title;
            if (patch.Body != null) payload["description"] = patch.Body;
            if (patch.Labels != null) payload["labels"] = string.Join(",", patch.Labels);
            if (patch.State != null) payload["state_event"] = patch.State == WorkItemState.Closed ? "close" : "reopen";
            var reply = await SendJsonAsync("PUT", ProjectApi + "/issues/" + number, payload, cancellation).ConfigureAwait(false);
            return ToWorkItem(ParseJson(reply.Body));
        }

        internal async Task<bool> WriteDeleteAsync(WorkItem item, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            try
            {
                await SendAsync(NewCall("DELETE", ProjectApi + "/issues/" + item.Number), cancellation).ConfigureAwait(false);
                return true;
            }
            catch (ProviderHttpException ex) when (ex.Status == 404)
            {
                return false; // already gone
            }
        }

        internal Task<bool> WriteLinkParentAsync(WorkItem child, WorkItem parent, CancellationToken cancellation)
        {
            return Task.FromResult(false);
        }

        private WorkItem ToWorkItem(JToken j)
        {
            var iid = (long?)j["iid"] ?? 0;
            var item = new WorkItem(Repository, iid)
            {
                ProviderId = ((long?)j["id"])?.ToString(),
                ProviderNumericId = (long?)j["id"],
                Title = (string?)j["title"] ?? string.Empty,
                Body = (string?)j["description"] ?? string.Empty,
                State = StateFromText((string?)j["state"] == "opened" ? "open" : (string?)j["state"]),
                Url = (string?)j["web_url"],
                Milestone = j["milestone"] == null || j["milestone"]!.Type == JTokenType.Null ? null : (string?)j["milestone"]!["title"],
                CreatedAt = ParseDate(j["created_at"]),
                UpdatedAt = ParseDate(j["updated_at"]),
            };
            item.Labels.AddRange(ReadLabels(j["labels"]));
            var assignees = j["assignees"] as JArray;
            if (assignees != null) item.Assignees.AddRange(assignees.Select(a => (string?)a["username"]).Where(a => !string.IsNullOrEmpty(a))!);
            return item;
        }

        private static int NextPage(HttpReply reply)
        {
            var next = reply.Header("X-Next-Page");
            int n;
            return !string.IsNullOrWhiteSpace(next) && int.TryParse(next, out n) && n > 0 ? n : 0;
        }
    }
}
