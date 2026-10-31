using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;

namespace Halyard.Core.Intelligence
{
    public sealed class PromotionRow
    {
        public string SourceKey { get; set; }
        public long SourceNumber { get; set; }
        public string CanonicalId { get; set; }
        public string Title { get; set; }
        /// <summary>create | update | unchanged</summary>
        public string Action { get; set; }
        public long? TargetNumber { get; set; }
        /// <summary>The production body the diff was made against (null for create); a changed body at run time fails the row.</summary>
        public string CurrentBody { get; set; }
        public string NewBody { get; set; }
        public List<string> Labels { get; set; }
        public string Outcome { get; set; }

        public PromotionRow()
        {
            SourceKey = string.Empty;
            CanonicalId = string.Empty;
            Title = string.Empty;
            Action = string.Empty;
            NewBody = string.Empty;
            Labels = new List<string>();
        }
    }

    public sealed class PromotionPlan
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public List<PromotionRow> Rows { get; set; }
        public List<string> Warnings { get; set; }
        public int Creates { get { return Rows.Count(r => r.Action == "create"); } }
        public int Updates { get { return Rows.Count(r => r.Action == "update"); } }
        public int Unchanged { get { return Rows.Count(r => r.Action == "unchanged"); } }
        /// <summary>The exact text that must be typed to run this plan.</summary>
        public string ConfirmPhrase { get { return Promoter.ConfirmationPhrase(Target, Creates + Updates); } }

        public PromotionPlan()
        {
            Source = string.Empty;
            Target = string.Empty;
            Rows = new List<PromotionRow>();
            Warnings = new List<string>();
        }
    }

    public sealed class PromotionResult
    {
        public string RunId { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Failed { get; set; }
        public List<PromotionRow> Rows { get; set; }
        public List<string> Messages { get; set; }

        public PromotionResult()
        {
            RunId = string.Empty;
            Rows = new List<PromotionRow>();
            Messages = new List<string>();
        }
    }

    /// <summary>
    /// Production promote: copies reviewed sandbox items into the approved production target. Never a default path. It needs the
    /// whole production gate (HALYARD_WRITE_MODE=production, HALYARD_ALLOW_PRODUCTION=1, an explicit per-call opt-in and the
    /// approved target), a preview with a diff per item, and a typed confirmation phrase. Synthetic items are never promoted.
    /// Items are matched by their halyard-meta id; items without one cannot be matched and are listed, not guessed.
    /// </summary>
    public static class Promoter
    {
        public static string ConfirmationPhrase(string target, int count)
        {
            return "PROMOTE " + count + " ITEMS TO " + target;
        }

        public static PromotionPlan Plan(IEnumerable<WorkItem> sandboxItems, IEnumerable<WorkItem> productionItems, RepositoryRef sandbox, RepositoryRef production)
        {
            var plan = new PromotionPlan { Source = sandbox.Key, Target = production.Key };
            var existing = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in productionItems.Where(i => i.Repository.Equals(production)))
            {
                var id = p.ExplicitId;
                if (!string.IsNullOrWhiteSpace(id) && !existing.ContainsKey(id)) existing[id] = p;
            }
            foreach (var item in sandboxItems.Where(i => i.Repository.Equals(sandbox)))
            {
                if (item.HasLabel(Labels.Synthetic)) { plan.Warnings.Add(item.Key + ": synthetic items are never promoted."); continue; }
                var id = item.ExplicitId;
                if (string.IsNullOrWhiteSpace(id)) { plan.Warnings.Add(item.Key + ": no halyard-meta id, so it cannot be matched in production; repair its metadata first."); continue; }
                var labels = item.Labels.Where(l => !l.StartsWith(Labels.RunPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(l, Labels.Synthetic, StringComparison.OrdinalIgnoreCase)
                                                    && !string.Equals(l, Labels.ReviewQueue, StringComparison.OrdinalIgnoreCase)).ToList();
                var row = new PromotionRow { SourceKey = item.Key, SourceNumber = item.Number, CanonicalId = id, Title = item.Title, NewBody = item.Body, Labels = labels };
                WorkItem match;
                if (existing.TryGetValue(id, out match))
                {
                    row.TargetNumber = match.Number;
                    row.CurrentBody = match.Body;
                    row.Action = match.Body == item.Body && match.Title == item.Title ? "unchanged" : "update";
                }
                else row.Action = "create";
                plan.Rows.Add(row);
            }
            return plan;
        }

        public static async Task<PromotionResult> RunAsync(PromotionPlan plan, string confirmText, IWorkItemProvider provider, WriteGateway gateway, RepositoryRef production,
            IProgress<string> progress, CancellationToken cancellation)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (!string.Equals(plan.Target, production.Key, StringComparison.OrdinalIgnoreCase))
                throw new WriteBlockedException("PROMOTE BLOCKED: the plan was made for " + plan.Target + ", not " + production.Key + ".", production);
            if (!string.Equals(confirmText, plan.ConfirmPhrase, StringComparison.Ordinal))
                throw new WriteBlockedException("PROMOTE BLOCKED: type '" + plan.ConfirmPhrase + "' to confirm.", production);
            var todo = plan.Rows.Where(r => r.Action != "unchanged").ToList();
            var result = new PromotionResult { RunId = "promote-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") };
            if (todo.Count == 0) { result.Messages.Add("Nothing to promote."); return result; }
            var intent = new WriteIntent("promote", "Promote " + todo.Count + " reviewed items from " + plan.Source, result.RunId) { AllowProduction = true };
            var writer = await gateway.OpenVerifiedAsync(provider, production, intent, cancellation).ConfigureAwait(false);
            var reader = provider.ForRepository(production);
            var done = 0;
            foreach (var row in todo)
            {
                cancellation.ThrowIfCancellationRequested();
                done++;
                if (progress != null) progress.Report("Promoting " + done + "/" + todo.Count + ": " + row.CanonicalId);
                result.Rows.Add(row);
                try
                {
                    if (row.Action == "create")
                    {
                        var draft = new WorkItemDraft { Title = row.Title, Body = row.NewBody };
                        draft.Labels.AddRange(row.Labels);
                        var created = await writer.CreateAsync(draft, cancellation).ConfigureAwait(false);
                        row.TargetNumber = created.Number;
                        row.Outcome = "created #" + created.Number;
                        result.Created++;
                    }
                    else
                    {
                        var live = await reader.GetWorkItemAsync(row.TargetNumber.Value, cancellation).ConfigureAwait(false);
                        if (live == null) { Fail(row, result, "#" + row.TargetNumber + " no longer exists in production."); continue; }
                        if (live.Body != row.CurrentBody) { Fail(row, result, "#" + row.TargetNumber + " changed in production since the preview; preview again."); continue; }
                        await writer.UpdateAsync(live, new WorkItemPatch { Title = row.Title, Body = row.NewBody }, cancellation).ConfigureAwait(false);
                        row.Outcome = "updated #" + row.TargetNumber;
                        result.Updated++;
                    }
                }
                catch (ProviderHttpException ex)
                {
                    Fail(row, result, ex.Message);
                    if (ex.Status == 401 || ex.Status == 403) break;
                }
            }
            return result;
        }

        private static void Fail(PromotionRow row, PromotionResult result, string error)
        {
            row.Outcome = "failed: " + error;
            result.Failed++;
            result.Messages.Add(row.CanonicalId + ": " + error);
        }
    }
}
