using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Safety;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Production promote: a preview with a diff, then a gated, confirmed run. Off unless production mode is fully enabled.</summary>
    public sealed partial class HalyardHost
    {
        private void RegisterPromoteRoutes()
        {
            Router.Get("/api/promote/status", r => Task.FromResult(Json.Ok(PromoteStatus())));
            Router.Post("/api/promote/preview", r => Task.FromResult(Json.Ok(MakePromotionPlan(Json.Body(r)))));
            Router.Post("/api/promote/run", r =>
            {
                var body = Json.Body(r);
                var production = RequirePromotionGate();
                var plan = MakePromotionPlan(body);
                var confirm = Json.Read<string>(body, "confirmText");
                if (!string.Equals(confirm, plan.ConfirmPhrase, StringComparison.Ordinal))
                    throw new WriteBlockedException("PROMOTE BLOCKED: type '" + plan.ConfirmPhrase + "' to confirm.", production);
                var provider = GetProvider();
                var job = Jobs.Start("promote", async ctx =>
                {
                    var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                    var result = await Promoter.RunAsync(plan, confirm, provider, Gateway, production, progress, ctx.Cancellation).ConfigureAwait(false);
                    Log.Info("PROMOTED", plan.Source + " -> " + plan.Target + ": " + result.Created + " created, " + result.Updated + " updated, " + result.Failed + " failed");
                    return await ResyncAfterWrite(ctx, provider, result).ConfigureAwait(false);
                });
                return Task.FromResult(Json.Accepted(new { job, target = production.Key, creates = plan.Creates, updates = plan.Updates }));
            });
        }

        private const string OffReason = "Promote is off. It needs HALYARD_WRITE_MODE=production, HALYARD_ALLOW_PRODUCTION=1 and a source repository to promote into, all set in config/halyard.env before launch.";

        private object PromoteStatus()
        {
            var policy = Gateway.Policy;
            var open = policy.Mode == WriteMode.Production && policy.AllowProduction && policy.ProductionTarget != null;
            return new
            {
                enabled = open, mode = policy.Mode.ToString().ToLowerInvariant(), allowProduction = policy.AllowProduction,
                target = policy.ProductionTarget == null ? null : policy.ProductionTarget.Key,
                reason = open ? null : OffReason,
            };
        }

        private Model.RepositoryRef RequirePromotionGate()
        {
            var policy = Gateway.Policy;
            if (policy.Mode != WriteMode.Production || !policy.AllowProduction || policy.ProductionTarget == null)
                throw new WriteBlockedException("PROMOTE BLOCKED: " + OffReason, policy.ProductionTarget ?? SandboxOrThrow());
            return policy.ProductionTarget;
        }

        private PromotionPlan MakePromotionPlan(JObject body)
        {
            var production = Gateway.Policy.ProductionTarget;
            if (production == null) throw new BadRequestException("No production target: set HALYARD_WRITE_MODE=production and a source repository first.");
            var sandbox = SandboxOrThrow();
            if (Snapshots.Current == null) throw new BadRequestException("Synchronize the sandbox and the production repository first.");
            List<WorkItem> all;
            var visible = VisibleItems(Json.Read<string>(body, "label") ?? Settings.LabelFilter, out all);
            var numbers = ReadNumbers(body);
            var sandboxItems = SandboxItems(visible).Where(i => numbers == null || numbers.Contains(i.Number)).ToList();
            return Promoter.Plan(sandboxItems, all, sandbox, production);
        }
    }
}
