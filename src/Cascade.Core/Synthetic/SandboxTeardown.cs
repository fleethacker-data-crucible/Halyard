using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Model;
using Cascade.Core.Providers;
using Cascade.Core.Safety;

namespace Cascade.Core.Synthetic
{
    public enum TeardownScope
    {
        /// <summary>Only items carrying <c>cascade-run::&lt;RunId&gt;</c> (and the synthetic label).</summary>
        Run = 1,
        /// <summary>Every item carrying <c>cascade-synthetic</c>.</summary>
        AllSynthetic = 2,
        /// <summary>Every item in the sandbox, synthetic or not. Requires the confirmation phrase.</summary>
        Everything = 3,
    }

    public sealed class TeardownOptions
    {
        public TeardownScope Scope { get; set; } = TeardownScope.Run;
        public string? RunId { get; set; }
        /// <summary>Must equal <c>DELETE &lt;repository identifier&gt;</c> for <see cref="TeardownScope.Everything"/>.</summary>
        public string? ConfirmText { get; set; }
        /// <summary>List what would be deleted without deleting.</summary>
        public bool DryRun { get; set; }

        public static string ConfirmationPhrase(RepositoryRef target)
        {
            return "DELETE " + target.Identifier;
        }
    }

    public sealed class TeardownResult
    {
        public string Target { get; set; } = string.Empty;
        public TeardownScope Scope { get; set; }
        public string? RunId { get; set; }
        public bool DryRun { get; set; }
        public int Matched { get; set; }
        public int Deleted { get; set; }
        public int Skipped { get; set; }
        public List<string> SkippedKeys { get; set; } = new List<string>();
        public List<string> DeletedKeys { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
    }

    /// <summary>
    /// Permanently deletes synthetic items from the sandbox. Deletion goes through the write gateway, which
    /// refuses anything without the synthetic label unless an explicit wipe with the confirmation phrase was
    /// requested. Items are re-read from the provider before deletion so stale snapshots cannot mislead it.
    /// </summary>
    public static class SandboxTeardown
    {
        public static async Task<TeardownResult> RunAsync(IWorkItemProvider provider, WriteGateway gateway, RepositoryRef target, TeardownOptions options,
            IProgress<SeedProgress>? progress, CancellationToken cancellation)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (gateway == null) throw new ArgumentNullException("gateway");
            options = options ?? new TeardownOptions();
            var result = new TeardownResult { Target = target.Key, Scope = options.Scope, RunId = options.RunId, DryRun = options.DryRun, StartedAt = DateTimeOffset.UtcNow };

            var intent = new WriteIntent("teardown-synthetic", "Teardown " + options.Scope + (options.RunId == null ? string.Empty : " run " + options.RunId), options.RunId);
            var query = new WorkItemQuery { State = null, MaxItems = 5000 };
            switch (options.Scope)
            {
                case TeardownScope.Run:
                    if (string.IsNullOrWhiteSpace(options.RunId)) throw new ArgumentException("RunId is required for a run-scoped teardown.");
                    query.Labels.Add(Labels.Synthetic);
                    query.Labels.Add(Labels.Run(options.RunId!));
                    break;
                case TeardownScope.AllSynthetic:
                    query.Labels.Add(Labels.Synthetic);
                    break;
                case TeardownScope.Everything:
                    if (!string.Equals(options.ConfirmText, TeardownOptions.ConfirmationPhrase(target), StringComparison.Ordinal))
                        throw new WriteBlockedException("WIPE BLOCKED: type the phrase '" + TeardownOptions.ConfirmationPhrase(target) + "' to delete every item in the sandbox.", target);
                    intent.AllowDeleteOfAnyItem = true;
                    break;
            }

            // Policy check happens here, before any listing, so a wrong target fails fast.
            var writer = await gateway.OpenVerifiedAsync(provider, target, intent, cancellation).ConfigureAwait(false);
            var reader = provider.ForRepository(target);
            Report(progress, 0, 0, "Listing candidates in " + target.Key);
            var candidates = await reader.ListWorkItemsAsync(query, null, cancellation).ConfigureAwait(false);
            // Providers filter by label server-side, but verify locally as well.
            var matched = candidates.Where(c => Matches(c, options)).OrderByDescending(c => c.Number).ToList();
            result.Matched = matched.Count;
            var done = 0;
            foreach (var item in matched)
            {
                cancellation.ThrowIfCancellationRequested();
                if (options.DryRun)
                {
                    result.DeletedKeys.Add(item.Key);
                    done++;
                    Report(progress, done, matched.Count, "Would delete " + item.Key);
                    continue;
                }
                try
                {
                    if (await writer.DeleteAsync(item, cancellation).ConfigureAwait(false))
                    {
                        result.Deleted++;
                        result.DeletedKeys.Add(item.Key);
                    }
                    else
                    {
                        result.Skipped++;
                        result.SkippedKeys.Add(item.Key + " (already gone)");
                    }
                }
                catch (WriteBlockedException ex)
                {
                    result.Skipped++;
                    result.SkippedKeys.Add(item.Key + " (" + ex.Message + ")");
                }
                catch (ProviderHttpException ex)
                {
                    result.Errors.Add(item.Key + ": " + ex.Message);
                    if (ex.Status == 401 || ex.Status == 403) break;
                }
                done++;
                Report(progress, done, matched.Count, "Deleted " + item.Key);
            }
            result.FinishedAt = DateTimeOffset.UtcNow;
            return result;
        }

        private static bool Matches(WorkItem item, TeardownOptions options)
        {
            switch (options.Scope)
            {
                case TeardownScope.Run:
                    return item.HasLabel(Labels.Synthetic) && item.HasLabel(Labels.Run(options.RunId!));
                case TeardownScope.AllSynthetic:
                    return item.HasLabel(Labels.Synthetic);
                default:
                    return true;
            }
        }

        private static void Report(IProgress<SeedProgress>? progress, int done, int total, string message)
        {
            if (progress != null) progress.Report(new SeedProgress { Done = done, Total = total, Message = message });
        }
    }
}
