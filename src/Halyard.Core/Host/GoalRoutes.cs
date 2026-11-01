using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Intelligence;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Planning;
using Halyard.Core.Providers;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    public sealed class GoalStep
    {
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>proposed | clean | waiting | skipped | done</summary>
        public string Status { get; set; }
        public string BatchId { get; set; }
        public int Proposals { get; set; }
        public string Note { get; set; }

        public GoalStep()
        {
            Id = string.Empty;
            Title = string.Empty;
            Status = string.Empty;
            Note = string.Empty;
        }
    }

    public sealed class GoalRun
    {
        public string Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string Target { get; set; }
        public List<GoalStep> Steps { get; set; }
        /// <summary>What the person should do next, in words.</summary>
        public string Next { get; set; }

        public GoalRun()
        {
            Id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            At = DateTimeOffset.UtcNow;
            Target = string.Empty;
            Steps = new List<GoalStep>();
            Next = string.Empty;
        }
    }

    /// <summary>
    /// Multi-step goal: "get this backlog ready". Runs the deterministic steps in order (metadata repair, parent links, skill tags, sprint
    /// plan, leadership summary) and stops at the first step that has something for a human to approve: every step makes proposals and
    /// nothing is written. Once those are approved and applied, running the goal again continues from the next step, because each
    /// step looks at the sandbox as it is now. Sizing needs a model and is only run when asked.
    /// </summary>
    public sealed partial class HalyardHost
    {
        public string GoalsDirectory { get { return Path.Combine(Settings.OutputDirectory, "goals"); } }

        private void RegisterGoalRoutes()
        {
            Router.Post("/api/goals/ready", async r => Json.Ok(await RunReadyGoalAsync(Json.Body(r)).ConfigureAwait(false)));
            Router.Get("/api/goals", r => Task.FromResult(Json.Ok(ReadGoals())));
        }

        private async Task<GoalRun> RunReadyGoalAsync(JObject body)
        {
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first.");
            var label = Json.Read<string>(body, "label") ?? Settings.LabelFilter;
            List<WorkItem> all;
            var visible = VisibleItems(label, out all);
            List<WorkItem> items;
            var target = ProposalTarget(visible, out items);
            var keys = new HashSet<string>(items.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            var report = Review.Run(visible, Taxonomy, label, all);
            var run = new GoalRun { Target = target.Key };
            var blocked = false;
            var useModel = Json.Read<bool?>(body, "useModel") ?? false;
            ILlmClient llm = useModel ? CreateLlm(Json.Read<string>(body, "profile")) : null;

            Func<string, string, Func<Task<ProposalBatch>>, Task> propose = async (id, title, make) =>
            {
                var step = new GoalStep { Id = id, Title = title };
                run.Steps.Add(step);
                if (blocked) { step.Status = "waiting"; step.Note = "Waits for the earlier proposals to be approved and applied."; return; }
                ProposalBatch batch;
                try { batch = await make().ConfigureAwait(false); }
                catch (ProviderHttpException ex)
                {
                    // A model or provider that fails stops this step with the reason; the steps after it wait rather than run on a guess.
                    step.Status = "failed"; step.Note = ex.Code + ": " + ex.Message;
                    blocked = true;
                    return;
                }
                var modelFailure = batch == null ? null : batch.Warnings.FirstOrDefault(w => w.IndexOf("call failed", StringComparison.OrdinalIgnoreCase) >= 0);
                if (modelFailure != null && batch.Proposals.Count == 0)
                {
                    step.Status = "failed"; step.Note = modelFailure;
                    blocked = true;
                    return;
                }
                if (batch == null || batch.Proposals.Count == 0)
                {
                    step.Status = "clean";
                    step.Note = batch != null && batch.Warnings.Count > 0 ? batch.Warnings[0] : "Nothing to do.";
                    return;
                }
                Proposals.Save(batch);
                step.Status = "proposed"; step.BatchId = batch.Id; step.Proposals = batch.Proposals.Count;
                step.Note = batch.Proposals.Count + " proposals to review in batch " + batch.Id + ".";
                blocked = true;
            };

            await propose("metadata", "Repair halyard-meta", () => Task.FromResult(Refiner.MetadataProposals(items, report, Taxonomy, target, null))).ConfigureAwait(false);
            await propose("parents", "Link items to their parents", () => Task.FromResult(Estimator.ParentProposals(report, target, 2.0, null, keys))).ConfigureAwait(false);
            if (llm == null)
            {
                run.Steps.Add(new GoalStep { Id = "draft", Title = "Draft missing sections", Status = "skipped", Note = "Needs a model: run the goal with useModel, or Draft sections from Proposals." });
                run.Steps.Add(new GoalStep { Id = "size", Title = "Size stories and features", Status = "skipped", Note = "Needs a model: run the goal with useModel, or Estimate from Proposals." });
            }
            else
            {
                await propose("draft", "Draft missing sections", () => Refiner.RunAsync(visible, report, llm, new RefineOptions { MaxItems = 10, OnlyKeys = keys }, target, null, System.Threading.CancellationToken.None)).ConfigureAwait(false);
                await propose("size", "Size stories and features", () => Estimator.RunAsync(items, llm, new EstimateOptions { Kind = ProposalKind.StoryPoints, OnlyUnsized = true, MaxItems = 40, RequireDefinition = true }, target, null, System.Threading.CancellationToken.None)).ConfigureAwait(false);
            }
            await propose("skills", "Tag the skills each item needs", () =>
            {
                var skills = Skills;
                if (skills.Skills.Count == 0) return Task.FromResult(new ProposalBatch { Warnings = { "config/skills.json has no skills." } });
                return Task.FromResult(SkillTagger.Deterministic(visible, report, skills, new SkillTagOptions { OnlyUntagged = true, MaxItems = 300, OnlyKeys = keys }, target));
            }).ConfigureAwait(false);

            await propose("plan", "Plan ready stories into sprints", () =>
            {
                var plan = SprintPlanner.Plan(visible, report, Team, DateTimeOffset.UtcNow, new PlanOptions { OnlyKeys = keys });
                var batch = SprintPlanner.ToProposals(plan, target);
                if (batch.Proposals.Count == 0 && plan.Notes.Count > 0) batch.Warnings.Insert(0, plan.Notes[0]);
                return Task.FromResult(batch);
            }).ConfigureAwait(false);

            var summary = new GoalStep { Id = "report", Title = "Leadership summary", Status = "done" };
            run.Steps.Add(summary);
            summary.Note = blocked ? "Based on the sandbox as it is now; run the goal again after applying." : Summary(BuildDelivery(label));

            var failed = run.Steps.FirstOrDefault(s => s.Status == "failed");
            var waiting = run.Steps.FirstOrDefault(s => s.Status == "proposed");
            if (failed != null) run.Next = "The " + failed.Title.ToLowerInvariant() + " step failed (" + failed.Note + "). Fix that, then run this goal again.";
            else run.Next = waiting == null ? "Every step is clean. Share the summary." : "Review and apply " + waiting.Proposals + " proposals in batch " + waiting.BatchId + " (" + waiting.Title + "), then run this goal again.";
            Directory.CreateDirectory(GoalsDirectory);
            File.WriteAllText(Path.Combine(GoalsDirectory, "goal-" + run.Id + ".json"), JsonConvert.SerializeObject(run, Formatting.Indented), new UTF8Encoding(false));
            Log.Info("GOAL_RUN", run.Id + ": " + string.Join(", ", run.Steps.Select(s => s.Id + "=" + s.Status)));
            return run;
        }

        private static string Summary(DeliveryReport d)
        {
            return "Features: " + d.FeaturesOnTrack + " of " + d.Features.Count + " on track or done. Velocity: " + d.AverageVelocity + " points per sprint. Ready backlog: " + d.Ready.Stories + " stories, " + d.Ready.Points + " points. Blockers: " + d.Blockers.Count + ".";
        }

        private List<GoalRun> ReadGoals()
        {
            var list = new List<GoalRun>();
            if (!Directory.Exists(GoalsDirectory)) return list;
            foreach (var file in Directory.GetFiles(GoalsDirectory, "goal-*.json"))
            {
                try { var g = JsonConvert.DeserializeObject<GoalRun>(File.ReadAllText(file)); if (g != null) list.Add(g); }
                catch (JsonException) { }
            }
            return list.OrderByDescending(g => g.At).Take(20).ToList();
        }
    }
}
