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
    /// <summary>Items handed over by PacketRat or Eight Bells (fleet-handoff v1): preview the drafts, then copy them into the sandbox through Intake.</summary>
    public sealed partial class HalyardHost
    {
        private void RegisterHandoffRoutes()
        {
            Router.Post("/api/intake/handoff/preview", r => HandoffRoute(r, false));
            Router.Post("/api/intake/handoff/run", r => HandoffRoute(r, true));
        }

        private Task<HttpResponse> HandoffRoute(HttpRequest r, bool run)
        {
            var body = Json.Body(r);
            var document = body["document"];
            if (document == null) throw new BadRequestException("document is required: the contents of the handoff file.");
            HandoffResult handoff;
            try { handoff = HandoffImport.Parse(document.Type == JTokenType.String ? (string)document : document.ToString(Formatting.None)); }
            catch (FormatException ex) { throw new BadRequestException(ex.Message); }
            if (handoff.Items.Count == 0) throw new BadRequestException("The handoff holds no usable items." + (handoff.Warnings.Count > 0 ? " " + handoff.Warnings[0] : string.Empty));
            var sandbox = SandboxOrThrow();
            var snapshotItems = Snapshots.Current == null ? new List<WorkItem>() : Snapshots.Current.Items;
            var all = snapshotItems.Concat(handoff.Items).ToList();
            var options = new IntakeOptions
            {
                Keys = new HashSet<string>(handoff.Items.Select(i => i.Key), StringComparer.OrdinalIgnoreCase),
                Area = Json.Read<string>(body, "area"), AddReviewLabel = Json.Read<bool?>(body, "addReviewLabel") ?? true, ManifestDirectory = Settings.IntakesDirectory,
            };
            var drafts = Intake.Prepare(all, sandbox, options, Taxonomy);
            if (!run) return Task.FromResult(Json.Ok(new { app = handoff.App, exportedAt = handoff.ExportedAt, drafts, count = drafts.Count, alreadyCopied = drafts.Count(d => d.ExistingKey != null), warnings = handoff.Warnings }));
            var provider = GetProvider();
            Gateway.Policy.Assert(sandbox, new WriteIntent("intake", "preflight", null));
            var job = Jobs.Start("intake", async ctx =>
            {
                var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                var manifest = await Intake.RunAsync(drafts, provider, Gateway, sandbox, options, progress, ctx.Cancellation).ConfigureAwait(false);
                manifest.Source = "handoff:" + handoff.App;
                ctx.Report(manifest.Items.Count, manifest.Requested, manifest.Items.Count + " of " + manifest.Requested + " copied into " + sandbox.Identifier);
                return await ResyncAfterWrite(ctx, provider, manifest).ConfigureAwait(false);
            });
            return Task.FromResult(Json.Accepted(new { job, count = drafts.Count, target = sandbox.Key, app = handoff.App }));
        }
    }
}
