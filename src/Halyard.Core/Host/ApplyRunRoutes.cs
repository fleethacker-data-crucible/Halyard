using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Safety;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Apply history and rollback: what each apply wrote, and undoing the last one (or any earlier one) with a preview first.</summary>
    public sealed partial class HalyardHost
    {
        private void RegisterApplyRunRoutes()
        {
            Router.Get("/api/applies", r => Task.FromResult(Json.Ok(Journal.LoadAll().Select(ApplyRunSummary))));
            Router.Get("/api/applies/{runId}", r =>
            {
                var run = Journal.Load(r.RouteValues["runId"]);
                if (run == null) throw new NotFoundException("No such apply run.");
                return Task.FromResult(Json.Ok(run));
            });
            Router.Post("/api/applies/rollback-last", r => RollbackRoute(r, null));
            Router.Post("/api/applies/{runId}/rollback", r => RollbackRoute(r, r.RouteValues["runId"]));
        }

        private static object ApplyRunSummary(ApplyRun run)
        {
            return new
            {
                run.Id, run.Target, run.StartedAt, run.BatchIds,
                entries = run.Entries.Count, pending = run.Pending, rolledBack = run.Entries.Count - run.Pending, canRollBack = run.CanRollBack,
            };
        }

        /// <summary>
        /// <c>dryRun: true</c> answers at once with what would be restored and what would be left alone (edited since). Otherwise it is
        /// a job that writes through the gateway. <c>force: true</c> overwrites items that were edited after the apply.
        /// </summary>
        private async Task<HttpResponse> RollbackRoute(HttpRequest r, string runId)
        {
            var body = Json.Body(r);
            var force = Json.Read<bool?>(body, "force") ?? false;
            var dryRun = Json.Read<bool?>(body, "dryRun") ?? false;
            var sandbox = SandboxOrThrow();
            ApplyRun run;
            if (runId == null)
            {
                run = Journal.Latest(sandbox.Key);
                if (run == null) throw new BadRequestException("There is no applied change to undo.");
            }
            else
            {
                run = Journal.Load(runId);
                if (run == null) throw new NotFoundException("No such apply run.");
            }
            if (!string.Equals(run.Target, sandbox.Key, StringComparison.OrdinalIgnoreCase))
                throw new WriteBlockedException("ROLLBACK BLOCKED: apply run " + run.Id + " wrote to " + run.Target + ", not the sandbox " + sandbox.Key + ".", sandbox);
            if (!run.CanRollBack) throw new BadRequestException("Apply run " + run.Id + " has already been undone.");
            var provider = GetProvider();
            if (dryRun)
            {
                var preview = await ApplyRollback.RollbackAsync(run, Journal, Proposals, provider, Gateway, sandbox, force, true, null, CancellationToken.None).ConfigureAwait(false);
                return Json.Ok(new { run = ApplyRunSummary(run), preview });
            }
            Gateway.Policy.Assert(sandbox, new WriteIntent("rollback-apply", "preflight", run.Id));
            var job = Jobs.Start("rollback", async ctx =>
            {
                var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                var result = await ApplyRollback.RollbackAsync(run, Journal, Proposals, provider, Gateway, sandbox, force, false, progress, ctx.Cancellation).ConfigureAwait(false);
                Log.Info("APPLY_ROLLED_BACK", run.Id + ": " + result.Restored + " restored, " + result.AlreadyRestored + " already, " + result.Conflicts + " conflicts, " + result.Failed + " failed");
                return await ResyncAfterWrite(ctx, provider, result).ConfigureAwait(false);
            });
            return Json.Accepted(new { job, runId = run.Id, pending = run.Pending, force });
        }
    }
}
