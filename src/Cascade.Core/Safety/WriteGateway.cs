using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Model;
using Cascade.Core.Providers;

namespace Cascade.Core.Safety
{
    public sealed class WriteAuditEntry
    {
        public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
        public string Operation { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public long? Number { get; set; }
        public string? Title { get; set; }
        public string? Reason { get; set; }
        public string? RunId { get; set; }
        public bool Succeeded { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// The only path to a provider's write methods. It checks the write policy, stamps the managed label
    /// on everything created, refuses deletes of items that are not marked synthetic (unless a wipe was
    /// explicitly requested) and records every attempt in an audit log the UI can show.
    /// </summary>
    public sealed class WriteGateway
    {
        private readonly List<WriteAuditEntry> _audit = new List<WriteAuditEntry>();
        private readonly object _lock = new object();

        public WriteGateway(WritePolicy policy)
        {
            Policy = policy ?? throw new ArgumentNullException("policy");
        }

        public WritePolicy Policy { get; private set; }

        public IReadOnlyList<WriteAuditEntry> Audit
        {
            get { lock (_lock) return _audit.ToList(); }
        }

        /// <summary>Opens a guarded writer for <paramref name="target"/>. Throws <see cref="WriteBlockedException"/> when the policy says no.</summary>
        public GuardedWriter Open(IWorkItemProvider provider, RepositoryRef target, WriteIntent intent)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (intent == null) throw new ArgumentNullException("intent");
            Policy.Assert(target, intent);
            return new GuardedWriter(this, provider.OpenWriter(target), target, intent);
        }

        internal void Record(WriteAuditEntry entry)
        {
            lock (_lock)
            {
                _audit.Add(entry);
                if (_audit.Count > 5000) _audit.RemoveRange(0, _audit.Count - 5000);
            }
        }

        public void ClearAudit()
        {
            lock (_lock) _audit.Clear();
        }
    }

    /// <summary>Writer handed out by <see cref="WriteGateway"/>; every call is policy-checked and audited.</summary>
    public sealed class GuardedWriter
    {
        private readonly WriteGateway _gateway;
        private readonly IWorkItemWriter _writer;
        private readonly WriteIntent _intent;

        internal GuardedWriter(WriteGateway gateway, IWorkItemWriter writer, RepositoryRef target, WriteIntent intent)
        {
            _gateway = gateway;
            _writer = writer;
            _intent = intent;
            Target = target;
        }

        public RepositoryRef Target { get; private set; }
        public WriteIntent Intent { get { return _intent; } }

        public Task EnsureLabelAsync(LabelDefinition label, CancellationToken cancellation)
        {
            return Guard("ensure-label", null, label.Name, () => _writer.EnsureLabelAsync(label, cancellation));
        }

        public async Task<WorkItem> CreateAsync(WorkItemDraft draft, CancellationToken cancellation)
        {
            var managed = _gateway.Policy.ManagedLabel;
            if (!string.IsNullOrWhiteSpace(managed) && !draft.Labels.Any(l => string.Equals(l, managed, StringComparison.OrdinalIgnoreCase)))
                draft.Labels.Insert(0, managed);
            WorkItem? created = null;
            await Guard("create", null, draft.Title, async () => { created = await _writer.CreateAsync(draft, cancellation).ConfigureAwait(false); }).ConfigureAwait(false);
            return created!;
        }

        public async Task<WorkItem> UpdateAsync(WorkItem existing, WorkItemPatch patch, CancellationToken cancellation)
        {
            if (existing == null) throw new ArgumentNullException("existing");
            if (!existing.Repository.Equals(Target))
                throw new WriteBlockedException("WRITE BLOCKED: item " + existing.Key + " does not belong to the opened target " + Target.Key + ".", Target);
            WorkItem? updated = null;
            await Guard("update", existing.Number, existing.Title, async () => { updated = await _writer.UpdateAsync(existing.Number, patch, cancellation).ConfigureAwait(false); }).ConfigureAwait(false);
            return updated!;
        }

        public Task DeleteAsync(WorkItem item, CancellationToken cancellation)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (!item.Repository.Equals(Target))
                throw new WriteBlockedException("DELETE BLOCKED: item " + item.Key + " does not belong to the opened target " + Target.Key + ".", Target);
            var required = _gateway.Policy.DeleteRequiresAnyLabel;
            if (!_intent.AllowDeleteOfAnyItem && required.Count > 0 && !required.Any(item.HasLabel))
                throw new WriteBlockedException("DELETE BLOCKED: " + item.Key + " is not labelled " + string.Join(" or ", required) + "; only synthetic items can be deleted without an explicit wipe.", Target);
            return Guard("delete", item.Number, item.Title, () => _writer.DeleteAsync(item, cancellation));
        }

        public async Task<bool> LinkParentAsync(WorkItem child, WorkItem parent, CancellationToken cancellation)
        {
            var ok = false;
            await Guard("link-parent", child.Number, child.Title + " → #" + parent.Number, async () => { ok = await _writer.LinkParentAsync(child, parent, cancellation).ConfigureAwait(false); }).ConfigureAwait(false);
            return ok;
        }

        private async Task Guard(string operation, long? number, string? title, Func<Task> action)
        {
            var entry = new WriteAuditEntry { Operation = operation, Target = Target.Key, Number = number, Title = title, Reason = _intent.Reason, RunId = _intent.RunId };
            try
            {
                await action().ConfigureAwait(false);
                entry.Succeeded = true;
            }
            catch (Exception ex)
            {
                entry.Succeeded = false;
                entry.Error = ex.Message;
                throw;
            }
            finally
            {
                _gateway.Record(entry);
            }
        }
    }
}
