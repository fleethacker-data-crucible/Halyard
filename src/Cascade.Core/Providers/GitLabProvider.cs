using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Model;
using Newtonsoft.Json.Linq;

namespace Cascade.Core.Providers
{
    /// <summary>
    /// GitLab REST v4 adapter (web.git.mil or any instance). Project identifier is a numeric id or a
    /// URL-encodable <c>group/project</c> path. Pagination follows the <c>X-Next-Page</c> header.
    /// Native parent links are not used: cascade-meta is the hierarchy source of truth.
    /// </summary>
    public sealed class GitLabProvider : RestProviderBase, IWorkItemProvider, IWorkItemWriter
    {
        private const int PageSize = 100;

        public GitLabProvider(IHttpTransport http, string baseUrl, string? token, RepositoryRef repository, int writeDelayMs = -1)
            : base(http, NormalizeBase(baseUrl), token, writeDelayMs)
        {
            if (repository.Provider != ProviderKind.GitLab) throw new ArgumentException("Repository is not a GitLab project.", "repository");
            Repository = repository;
            Capabilities = new ProviderCapabilities { DeleteIssues = true, NativeParentLinks = false, RecommendedWriteDelayMs = DefaultWriteDelayMs };
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
            return target.Equals(Repository) ? this : new GitLabProvider(Http, BaseUrl, Token, target, WriteDelayMs);
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

        async Task IWorkItemWriter.EnsureLabelAsync(LabelDefinition label, CancellationToken cancellation)
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

        async Task<WorkItem> IWorkItemWriter.CreateAsync(WorkItemDraft draft, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            var payload = new JObject { ["title"] = draft.Title, ["description"] = draft.Body, ["labels"] = string.Join(",", draft.Labels) };
            var reply = await SendJsonAsync("POST", ProjectApi + "/issues", payload, cancellation).ConfigureAwait(false);
            return ToWorkItem(ParseJson(reply.Body));
        }

        async Task<WorkItem> IWorkItemWriter.UpdateAsync(long number, WorkItemPatch patch, CancellationToken cancellation)
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

        async Task IWorkItemWriter.DeleteAsync(WorkItem item, CancellationToken cancellation)
        {
            await PaceWriteAsync(cancellation).ConfigureAwait(false);
            try
            {
                await SendAsync(NewCall("DELETE", ProjectApi + "/issues/" + item.Number), cancellation).ConfigureAwait(false);
            }
            catch (ProviderHttpException ex) when (ex.Status == 404)
            {
                // already gone
            }
        }

        Task<bool> IWorkItemWriter.LinkParentAsync(WorkItem child, WorkItem parent, CancellationToken cancellation)
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
