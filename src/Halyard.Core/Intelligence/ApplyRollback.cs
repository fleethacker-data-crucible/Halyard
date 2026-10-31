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
    public enum RollbackStatus
    {
        /// <summary>Dry run: would be restored.</summary>
        Restore = 0,
        Restored = 1,
        AlreadyRestored = 2,
        /// <summary>The live body is neither what Halyard wrote nor the earlier body.</summary>
        Conflict = 3,
        Missing = 4,
        Failed = 5,
    }

    public sealed class RollbackRow
    {
        public string BatchId { get; set; }
        public string ProposalId { get; set; }
        public long ItemNumber { get; set; }
        public string ItemCanonicalId { get; set; }
        public string ItemTitle { get; set; }
        public RollbackStatus Status { get; set; }
        public string Note { get; set; }

        public RollbackRow()
        {
            BatchId = string.Empty;
            ProposalId = string.Empty;
            ItemCanonicalId = string.Empty;
            ItemTitle = string.Empty;
            Note = string.Empty;
        }
    }

    public sealed class RollbackResult
    {
        public string RunId { get; set; }
        public bool DryRun { get; set; }
        /// <summary>Restored (or, in a dry run, restorable).</summary>
        public int Restored { get; set; }
        public int AlreadyRestored { get; set; }
        public int Conflicts { get; set; }
        public int Missing { get; set; }
        public int Failed { get; set; }
        public List<RollbackRow> Rows { get; set; }
        public List<string> Messages { get; set; }

        public RollbackResult()
        {
            RunId = string.Empty;
            Rows = new List<RollbackRow>();
            Messages = new List<string>();
        }
    }

    /// <summary>
    /// Undoes an apply run from its journal. Entries are walked newest first (reverse of the order they were written), so several proposals that touched one
    /// item unwind in order. An item is restored only while its live body is still exactly what Halyard wrote; if someone
    /// edited it since, the entry is a conflict and is left alone unless <c>force</c> is set. A dry run writes nothing and
    /// does not open a writer. Restored proposals go back to Pending so they must be reviewed again before re-applying.
    /// </summary>
    public static class ApplyRollback
    {
        public static async Task<RollbackResult> RollbackAsync(ApplyRun run, ApplyJournal journal, ProposalStore store, IWorkItemProvider provider, WriteGateway gateway, RepositoryRef target,
            bool force, bool dryRun, IProgress<string> progress, CancellationToken cancellation)
        {
            if (run == null) throw new ArgumentNullException("run");
            var result = new RollbackResult { RunId = run.Id, DryRun = dryRun };
            if (!string.Equals(run.Target, target.Key, StringComparison.OrdinalIgnoreCase))
                throw new WriteBlockedException("ROLLBACK BLOCKED: apply run " + run.Id + " wrote to " + run.Target + ", not the sandbox " + target.Key + ".", target);
            var open = run.Entries.Where(e => e.RolledBackAt == null).Reverse().ToList(); // entries are appended in apply order; timestamps can tie on .NET Framework
            if (open.Count == 0)
            {
                result.Messages.Add("Apply run " + run.Id + " has nothing left to undo.");
                return result;
            }
            GuardedWriter writer = null;
            if (!dryRun) writer = await gateway.OpenVerifiedAsync(provider, target, new WriteIntent("rollback-apply", "Undo " + open.Count + " applied changes from apply run " + run.Id, run.Id), cancellation).ConfigureAwait(false);
            var reader = provider.ForRepository(target);
            // The body each item is expected to hold at this point of the unwind (a dry run has to predict its own earlier steps).
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            foreach (var entry in open)
            {
                cancellation.ThrowIfCancellationRequested();
                done++;
                if (progress != null) progress.Report((dryRun ? "Checking " : "Restoring ") + done + "/" + open.Count + ": " + entry.ItemCanonicalId);
                var row = new RollbackRow { ProposalId = entry.ProposalId, BatchId = entry.BatchId, ItemNumber = entry.ItemNumber, ItemCanonicalId = entry.ItemCanonicalId, ItemTitle = entry.ItemTitle };
                result.Rows.Add(row);
                try
                {
                    WorkItem item = null;
                    string liveBody;
                    if (dryRun && expected.TryGetValue(entry.ItemKey, out liveBody)) { }
                    else
                    {
                        item = await reader.GetWorkItemAsync(entry.ItemNumber, cancellation).ConfigureAwait(false);
                        if (item == null || !item.Repository.Equals(target))
                        {
                            row.Status = RollbackStatus.Missing;
                            row.Note = "Item #" + entry.ItemNumber + " no longer exists in " + target.Key + ".";
                            result.Missing++;
                            continue;
                        }
                        liveBody = item.Body;
                    }
                    if (liveBody == entry.PreviousBody)
                    {
                        row.Status = RollbackStatus.AlreadyRestored;
                        row.Note = "Already has the earlier body.";
                        result.AlreadyRestored++;
                        if (!dryRun) MarkRolledBack(entry, "already restored", store);
                        continue;
                    }
                    if (liveBody != entry.AppliedBody && !force)
                    {
                        row.Status = RollbackStatus.Conflict;
                        row.Note = "Edited since Halyard wrote it; left alone. Use force to overwrite it with the earlier body.";
                        result.Conflicts++;
                        continue;
                    }
                    var overwritesEdit = liveBody != entry.AppliedBody;
                    row.Note = overwritesEdit ? "Edited since the apply; overwritten because force was set." : "Restores the body from before the apply.";
                    if (dryRun) row.Status = RollbackStatus.Restore;
                    else
                    {
                        await writer.UpdateAsync(item, new WorkItemPatch { Body = entry.PreviousBody }, cancellation).ConfigureAwait(false);
                        row.Status = RollbackStatus.Restored;
                        MarkRolledBack(entry, overwritesEdit ? "forced over a later edit" : "restored", store);
                    }
                    expected[entry.ItemKey] = entry.PreviousBody;
                    result.Restored++;
                }
                catch (WriteBlockedException ex)
                {
                    row.Status = RollbackStatus.Failed; row.Note = ex.Message; result.Failed++; result.Messages.Add(ex.Message);
                    break;
                }
                catch (ProviderHttpException ex)
                {
                    row.Status = RollbackStatus.Failed; row.Note = ex.Message; result.Failed++; result.Messages.Add(entry.ItemCanonicalId + ": " + ex.Message);
                    if (ex.Status == 401 || ex.Status == 403) break;
                }
                finally
                {
                    if (!dryRun) journal.Save(run);
                }
            }
            return result;
        }

        private static void MarkRolledBack(ApplyJournalEntry entry, string note, ProposalStore store)
        {
            entry.RolledBackAt = DateTimeOffset.UtcNow;
            entry.RollbackNote = note;
            if (store == null) return;
            var batch = store.Load(entry.BatchId);
            if (batch == null) return;
            var proposal = batch.Proposals.FirstOrDefault(p => p.Id == entry.ProposalId);
            if (proposal == null || proposal.Decision != ProposalDecision.Applied) return;
            proposal.Decision = ProposalDecision.Pending;
            proposal.AppliedAt = null;
            proposal.DecisionNote = "Rolled back " + entry.RolledBackAt.Value.ToString("u");
            proposal.Error = null;
            store.Save(batch);
        }
    }
}
