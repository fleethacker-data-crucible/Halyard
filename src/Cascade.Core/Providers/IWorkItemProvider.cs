using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Model;

namespace Cascade.Core.Providers
{
    public sealed class ProviderIdentity
    {
        public string Login { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string ApiBase { get; set; } = string.Empty;
        public ProviderKind Provider { get; set; }
    }

    public sealed class LabelDefinition
    {
        public LabelDefinition(string name, string color, string? description = null)
        {
            Name = name;
            Color = color.TrimStart('#');
            Description = description;
        }

        public string Name { get; private set; }
        /// <summary>Six hex digits, no '#'.</summary>
        public string Color { get; private set; }
        public string? Description { get; private set; }
    }

    public sealed class ProviderCapabilities
    {
        public bool DeleteIssues { get; set; }
        public bool NativeParentLinks { get; set; }
        public bool LabelColors { get; set; } = true;
        public int RecommendedWriteDelayMs { get; set; }
    }

    /// <summary>Read side. Safe to hand to any caller.</summary>
    public interface IWorkItemReader
    {
        ProviderKind Kind { get; }
        RepositoryRef Repository { get; }
        ProviderCapabilities Capabilities { get; }
        Task<ProviderIdentity> TestConnectionAsync(CancellationToken cancellation);
        Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(int max, CancellationToken cancellation);
        Task<IReadOnlyList<string>> ListLabelsAsync(CancellationToken cancellation);
        Task<IReadOnlyList<WorkItem>> ListWorkItemsAsync(WorkItemQuery query, IProgress<string>? progress, CancellationToken cancellation);
        Task<WorkItem?> GetWorkItemAsync(long number, CancellationToken cancellation);
        /// <summary>The host's stable numeric id for the repository (survives renames and path/id spellings).</summary>
        Task<string> GetRepositoryIdAsync(CancellationToken cancellation);
    }

    /// <summary>
    /// Write side. Only <see cref="Safety.WriteGateway"/> hands this out, and only after the target
    /// repository passed the write policy. Nothing else in the code base should hold a reference.
    /// </summary>
    public interface IWorkItemWriter
    {
        RepositoryRef Repository { get; }
        Task EnsureLabelAsync(LabelDefinition label, CancellationToken cancellation);
        Task<WorkItem> CreateAsync(WorkItemDraft draft, CancellationToken cancellation);
        Task<WorkItem> UpdateAsync(long number, WorkItemPatch patch, CancellationToken cancellation);
        /// <summary>Permanently deletes the item. Returns false when it no longer exists.</summary>
        Task<bool> DeleteAsync(WorkItem item, CancellationToken cancellation);
        /// <summary>Fresh read from the bound repository, used to re-check an item immediately before writing.</summary>
        Task<WorkItem?> GetAsync(long number, CancellationToken cancellation);
        /// <summary>Best-effort native parent link (GitHub sub-issues). No-op when unsupported.</summary>
        Task<bool> LinkParentAsync(WorkItem child, WorkItem parent, CancellationToken cancellation);
    }

    /// <summary>
    /// A provider is a reader that can also produce a writer. The writer is deliberately not a public
    /// property: <see cref="Safety.WriteGateway"/> is the only caller of <see cref="OpenWriter"/>.
    /// </summary>
    public interface IWorkItemProvider : IWorkItemReader
    {
        /// <summary>Returns a writer bound to <paramref name="target"/>. Called only by the write gateway.</summary>
        IWorkItemWriter OpenWriter(RepositoryRef target);
        /// <summary>A reader bound to another repository on the same provider/credentials.</summary>
        IWorkItemReader ForRepository(RepositoryRef repository);
    }
}
