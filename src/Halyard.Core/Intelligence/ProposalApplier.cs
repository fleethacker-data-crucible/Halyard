using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Halyard.Core.Schema;

namespace Halyard.Core.Intelligence
{
    public sealed class ApplyResult
    {
        public string BatchId { get; set; }
        public int Applied { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<string> Messages { get; set; }

        public ApplyResult()
        {
            BatchId = string.Empty;
            Messages = new List<string>();
        }
    }

    /// <summary>What a proposal would turn an item into: the new meta and the full new body.</summary>
    public sealed class Projection
    {
        public Projection(HalyardMeta meta, string body)
        {
            Meta = meta;
            Body = body;
        }

        public HalyardMeta Meta { get; private set; }
        public string Body { get; private set; }
    }

    /// <summary>
    /// Writes approved proposals to the sandbox through the write gateway. Each item is re-read first so the
    /// patch is built on the live body; meta, mirror and the Sizing section are rewritten together so the
    /// issue never shows meta=5 and prose=TBD. <see cref="Project"/> is the single pure function that turns an
    /// item plus a proposal into the new body; the preview endpoint and the apply path both use it, so what
    /// the reviewer saw in the diff is exactly what gets written.
    /// </summary>
    public static class ProposalApplier
    {
        public static Projection Project(WorkItem item, Proposal proposal)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (proposal == null) throw new ArgumentNullException("proposal");
            var meta = item.Meta == null ? new HalyardMeta() : item.Meta.Clone();
            if (!meta.Has("type") && item.Type != WorkItemType.Unclassified) meta.Type = item.Type;
            if (!meta.Has("id") && item.Type != WorkItemType.Unclassified) meta.Id = Descriptions.MakeId(item.Type, Descriptions.ShortName(item.Title));

            switch (proposal.Kind)
            {
                case ProposalKind.StoryPoints:
                case ProposalKind.FeatureEffort:
                case ProposalKind.ParentLink:
                    meta.Set(proposal.MetaKey, proposal.ProposedValue);
                    if (proposal.Kind == ProposalKind.StoryPoints && meta.Has("effort")) meta.Remove("effort");
                    if (proposal.Kind == ProposalKind.FeatureEffort && meta.Has("points")) meta.Remove("points");
                    if (proposal.Kind != ProposalKind.ParentLink) meta.Set("sized_by", "halyard-proposal " + proposal.Id);
                    return new Projection(meta, Descriptions.Rewrite(item.Body, meta));
                case ProposalKind.Metadata:
                    ApplyMeta(meta, proposal.ProposedMeta);
                    meta.Set("refined_by", "halyard-proposal " + proposal.Id);
                    return new Projection(meta, Descriptions.Rewrite(item.Body, meta));
                case ProposalKind.Skills:
                case ProposalKind.SprintPlan:
                case ProposalKind.ThemeLink:
                    ApplyMeta(meta, proposal.ProposedMeta);
                    if (proposal.ProposedMeta == null || proposal.ProposedMeta.Count == 0) meta.Set(proposal.MetaKey, proposal.ProposedValue);
                    meta.Set(proposal.Kind == ProposalKind.Skills ? "tagged_by" : proposal.Kind == ProposalKind.SprintPlan ? "planned_by" : "themed_by", "halyard-proposal " + proposal.Id);
                    return new Projection(meta, Descriptions.Rewrite(item.Body, meta));
                case ProposalKind.Refinement:
                    {
                        ApplyMeta(meta, proposal.ProposedMeta);
                        meta.Set("refined_by", "halyard-proposal " + proposal.Id);
                        var narrative = string.IsNullOrWhiteSpace(proposal.ProposedValue) ? Descriptions.Narrative(item.Body) : proposal.ProposedValue;
                        return new Projection(meta, Descriptions.Compose(meta, Descriptions.SyncSizing(narrative, meta)));
                    }
                default:
                    throw new NotSupportedException("Unknown proposal kind " + proposal.Kind);
            }
        }

        private static void ApplyMeta(HalyardMeta meta, Dictionary<string, string> changes)
        {
            if (changes == null) return;
            foreach (var kv in changes) meta.Set(kv.Key, kv.Value);
        }

        public static Task<ApplyResult> ApplyAsync(ProposalBatch batch, ProposalStore store, IWorkItemProvider provider, WriteGateway gateway, RepositoryRef target,
            IProgress<string> progress, CancellationToken cancellation)
        {
            return ApplyAsync(batch, store, provider, gateway, target, progress, cancellation, null, null);
        }

        /// <summary>
        /// As above; when <paramref name="run"/> and <paramref name="journal"/> are given, every body that is written is
        /// recorded with the body it replaced so the run can be undone by <see cref="RollbackAsync"/>.
        /// </summary>
        public static async Task<ApplyResult> ApplyAsync(ProposalBatch batch, ProposalStore store, IWorkItemProvider provider, WriteGateway gateway, RepositoryRef target,
            IProgress<string> progress, CancellationToken cancellation, ApplyRun run, ApplyJournal journal)
        {
            if (batch == null) throw new ArgumentNullException("batch");
            var result = new ApplyResult { BatchId = batch.Id };
            var approved = batch.Proposals.Where(p => p.Decision == ProposalDecision.Approved).ToList();
            if (approved.Count == 0)
            {
                result.Messages.Add("No approved proposals in batch " + batch.Id + ".");
                return result;
            }
            var writer = await gateway.OpenVerifiedAsync(provider, target, new WriteIntent("apply-proposals", "Apply " + approved.Count + " approved " + batch.Kind + " proposals", batch.Id), cancellation).ConfigureAwait(false);
            var reader = provider.ForRepository(target);
            var done = 0;
            foreach (var proposal in approved)
            {
                cancellation.ThrowIfCancellationRequested();
                done++;
                if (progress != null) progress.Report("Applying " + done + "/" + approved.Count + ": " + proposal.ItemCanonicalId);
                try
                {
                    var item = await reader.GetWorkItemAsync(proposal.ItemNumber, cancellation).ConfigureAwait(false);
                    if (item == null || !item.Repository.Equals(target))
                    {
                        Fail(proposal, result, "Item #" + proposal.ItemNumber + " not found in " + target.Key);
                        continue;
                    }
                    // Items without meta have a synthesised canonical id (from title/labels); compare on that too.
                    if (!string.Equals(item.CanonicalId, proposal.ItemCanonicalId, StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Title, proposal.ItemTitle, StringComparison.Ordinal))
                    {
                        Fail(proposal, result, "#" + proposal.ItemNumber + " is now " + item.CanonicalId + ", not " + proposal.ItemCanonicalId + "; re-run the analysis.");
                        continue;
                    }
                    var projection = Project(item, proposal);
                    if (projection.Body == item.Body)
                    {
                        proposal.Decision = ProposalDecision.Applied;
                        proposal.AppliedAt = DateTimeOffset.UtcNow;
                        result.Skipped++;
                        result.Messages.Add(proposal.ItemCanonicalId + ": already up to date");
                        continue;
                    }
                    await writer.UpdateAsync(item, new WorkItemPatch { Body = projection.Body }, cancellation).ConfigureAwait(false);
                    proposal.Decision = ProposalDecision.Applied;
                    proposal.AppliedAt = DateTimeOffset.UtcNow;
                    result.Applied++;
                    if (run != null && journal != null)
                    {
                        run.Target = target.Key;
                        if (!run.BatchIds.Contains(batch.Id)) run.BatchIds.Add(batch.Id);
                        run.Entries.Add(new ApplyJournalEntry
                        {
                            BatchId = batch.Id, ProposalId = proposal.Id, Kind = proposal.Kind, ItemKey = item.Key, ItemNumber = item.Number, ItemCanonicalId = proposal.ItemCanonicalId,
                            ItemTitle = item.Title, PreviousBody = item.Body, AppliedBody = projection.Body, AppliedAt = proposal.AppliedAt.Value,
                        });
                        journal.Save(run);
                    }
                }
                catch (WriteBlockedException ex)
                {
                    Fail(proposal, result, ex.Message);
                    result.Messages.Add(ex.Message);
                    break;
                }
                catch (ProviderHttpException ex)
                {
                    Fail(proposal, result, ex.Message);
                    result.Messages.Add(proposal.ItemCanonicalId + ": " + ex.Message);
                    if (ex.Status == 401 || ex.Status == 403) break;
                }
                finally
                {
                    store.Save(batch);
                }
            }
            return result;
        }

        private static void Fail(Proposal proposal, ApplyResult result, string error)
        {
            proposal.Decision = ProposalDecision.Failed;
            proposal.Error = error;
            result.Failed++;
        }
    }
}
