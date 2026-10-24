using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Model;
using Cascade.Core.Providers;
using Cascade.Core.Safety;
using Cascade.Core.Schema;

namespace Cascade.Core.Intelligence
{
    public sealed class ApplyResult
    {
        public string BatchId { get; set; } = string.Empty;
        public int Applied { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
    }

    /// <summary>
    /// Writes approved proposals to the sandbox through the write gateway. Each item is re-read first so the
    /// patch is built on the live body; meta, mirror and the Sizing section are rewritten together so the
    /// issue never shows meta=5 and prose=TBD.
    /// </summary>
    public static class ProposalApplier
    {
        public static async Task<ApplyResult> ApplyAsync(ProposalBatch batch, ProposalStore store, IWorkItemProvider provider, WriteGateway gateway, RepositoryRef target,
            IProgress<string>? progress, CancellationToken cancellation)
        {
            if (batch == null) throw new ArgumentNullException("batch");
            var result = new ApplyResult { BatchId = batch.Id };
            var approved = batch.Proposals.Where(p => p.Decision == ProposalDecision.Approved).ToList();
            if (approved.Count == 0)
            {
                result.Messages.Add("No approved proposals in batch " + batch.Id + ".");
                return result;
            }
            var writer = gateway.Open(provider, target, new WriteIntent("apply-proposals", "Apply " + approved.Count + " approved " + batch.Kind + " proposals", batch.Id));
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
                        proposal.Decision = ProposalDecision.Failed;
                        proposal.Error = "Item #" + proposal.ItemNumber + " not found in " + target.Key;
                        result.Failed++;
                        continue;
                    }
                    var meta = item.Meta == null ? new CascadeMeta() : item.Meta.Clone();
                    if (!meta.Has("type") && item.Type != WorkItemType.Unclassified) meta.Type = item.Type;
                    if (!meta.Has("id")) meta.Id = item.CanonicalId;
                    meta.Set(proposal.MetaKey, proposal.ProposedValue);
                    if (proposal.Kind == ProposalKind.StoryPoints && meta.Has("effort")) meta.Remove("effort");
                    if (proposal.Kind == ProposalKind.FeatureEffort && meta.Has("points")) meta.Remove("points");
                    meta.Set("sized_by", "cascade-proposal " + proposal.Id);
                    var body = Descriptions.Rewrite(item.Body, meta);
                    if (body == item.Body)
                    {
                        proposal.Decision = ProposalDecision.Applied;
                        proposal.AppliedAt = DateTimeOffset.UtcNow;
                        result.Skipped++;
                        result.Messages.Add(proposal.ItemCanonicalId + ": already up to date");
                        continue;
                    }
                    await writer.UpdateAsync(item, new WorkItemPatch { Body = body }, cancellation).ConfigureAwait(false);
                    proposal.Decision = ProposalDecision.Applied;
                    proposal.AppliedAt = DateTimeOffset.UtcNow;
                    result.Applied++;
                }
                catch (WriteBlockedException ex)
                {
                    proposal.Decision = ProposalDecision.Failed;
                    proposal.Error = ex.Message;
                    result.Failed++;
                    result.Messages.Add(ex.Message);
                    break;
                }
                catch (ProviderHttpException ex)
                {
                    proposal.Decision = ProposalDecision.Failed;
                    proposal.Error = ex.Message;
                    result.Failed++;
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
    }
}
