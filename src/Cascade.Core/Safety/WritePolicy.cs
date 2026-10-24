using System;
using System.Collections.Generic;
using System.Linq;
using Cascade.Core.Model;

namespace Cascade.Core.Safety
{
    public enum WriteMode
    {
        Sandbox = 1,
        Production = 2,
    }

    /// <summary>
    /// Which repositories may be written to. Default is sandbox-only. Production needs three things at
    /// once: mode = Production, <see cref="AllowProduction"/> = true (env CASCADE_ALLOW_PRODUCTION=1) and a
    /// per-call <see cref="WriteIntent.AllowProduction"/>. Any one missing blocks the write.
    /// </summary>
    public sealed class WritePolicy
    {
        public WritePolicy()
        {
            SandboxTargets = new List<RepositoryRef>();
            ProtectedTargets = new List<RepositoryRef>();
        }

        public WriteMode Mode { get; set; } = WriteMode.Sandbox;
        public bool AllowProduction { get; set; }
        public List<RepositoryRef> SandboxTargets { get; private set; }
        public RepositoryRef? ProductionTarget { get; set; }
        /// <summary>Never writable, whatever the mode (e.g. the production backlog while it is only being read).</summary>
        public List<RepositoryRef> ProtectedTargets { get; private set; }
        /// <summary>Deletes are refused unless the item carries one of these labels. Empty list = any item in a sandbox.</summary>
        public List<string> DeleteRequiresAnyLabel { get; set; } = new List<string> { Labels.Synthetic };
        /// <summary>Label stamped on every item Cascade creates so its writes are always identifiable.</summary>
        public string ManagedLabel { get; set; } = Labels.Managed;

        public bool IsSandbox(RepositoryRef target)
        {
            return SandboxTargets.Any(t => t.Equals(target));
        }

        public bool IsProtected(RepositoryRef target)
        {
            return ProtectedTargets.Any(t => t.Equals(target));
        }

        /// <summary>Throws <see cref="WriteBlockedException"/> unless <paramref name="target"/> may be written under <paramref name="intent"/>.</summary>
        public void Assert(RepositoryRef target, WriteIntent intent)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (IsProtected(target) && !(IsSandbox(target)))
                throw new WriteBlockedException("WRITE BLOCKED: " + target.Key + " is a protected (read-only) repository.", target);
            if (Mode == WriteMode.Sandbox)
            {
                if (SandboxTargets.Count == 0)
                    throw new WriteBlockedException("WRITE BLOCKED: no sandbox repository is configured (CASCADE_SANDBOX_REPO).", target);
                if (!IsSandbox(target))
                    throw new WriteBlockedException("WRITE BLOCKED: sandbox mode only permits " + string.Join(", ", SandboxTargets.Select(t => t.Key)) + "; requested " + target.Key + ".", target);
                return;
            }
            if (IsSandbox(target)) return;
            if (!AllowProduction)
                throw new WriteBlockedException("WRITE BLOCKED: production writes require CASCADE_ALLOW_PRODUCTION=1.", target);
            if (!intent.AllowProduction)
                throw new WriteBlockedException("WRITE BLOCKED: this operation did not explicitly opt in to a production write.", target);
            if (ProductionTarget == null || !ProductionTarget.Equals(target))
                throw new WriteBlockedException("WRITE BLOCKED: " + target.Key + " is not the approved production target" + (ProductionTarget == null ? "." : " (" + ProductionTarget.Key + ")."), target);
        }

        public string Describe()
        {
            var sandbox = SandboxTargets.Count == 0 ? "(none)" : string.Join(", ", SandboxTargets.Select(t => t.Key));
            return "mode=" + Mode.ToString().ToLowerInvariant() + " sandbox=" + sandbox + " production=" + (ProductionTarget == null ? "(none)" : ProductionTarget.Key) + " allowProduction=" + AllowProduction;
        }
    }

    /// <summary>Why a write is happening. Carried into the audit log.</summary>
    public sealed class WriteIntent
    {
        public WriteIntent(string operation, string reason, string? runId = null)
        {
            Operation = operation;
            Reason = reason;
            RunId = runId;
        }

        public string Operation { get; private set; }
        public string Reason { get; private set; }
        public string? RunId { get; private set; }
        public bool AllowProduction { get; set; }
        /// <summary>Allows deleting items that lack the synthetic label (explicit sandbox wipe only).</summary>
        public bool AllowDeleteOfAnyItem { get; set; }
    }

    public sealed class WriteBlockedException : InvalidOperationException
    {
        public WriteBlockedException(string message, RepositoryRef target) : base(message)
        {
            Target = target;
        }

        public RepositoryRef Target { get; private set; }
    }

    /// <summary>Label names Cascade relies on. Changing these changes what teardown is allowed to delete.</summary>
    public static class Labels
    {
        public const string Managed = "cascade";
        public const string Synthetic = "cascade-synthetic";
        public const string RunPrefix = "cascade-run::";
        public const string ReviewQueue = "refine::sandbox-review";
        public const string TypePrefix = "type::";
        public const string ThemePrefix = "theme::";
        public const string AreaPrefix = "area::";

        public static string Run(string runId)
        {
            return RunPrefix + runId;
        }
    }
}
