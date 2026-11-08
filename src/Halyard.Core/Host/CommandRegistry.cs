using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    public sealed class CommandInfo
    {
        /// <summary>Fleet CLI parity name: <c>halyard &lt;noun&gt; &lt;verb&gt;</c>.</summary>
        public string Name { get; set; }
        public string Method { get; set; }
        public string Route { get; set; }
        public string Summary { get; set; }
        /// <summary>Writes to the sandbox (or production, for promote) or stops the engine: needs confirmation and an approved plan.</summary>
        public bool Writes { get; set; }
        /// <summary>Returns before the work finishes: poll <c>halyard job show</c>.</summary>
        public bool Async { get; set; }

        public CommandInfo()
        {
            Name = string.Empty;
            Method = string.Empty;
            Route = string.Empty;
            Summary = string.Empty;
        }
    }

    /// <summary>
    /// The one list of everything Halyard can do, as named commands (Fleet CLI parity, requirement 7). Each entry is the loopback API
    /// route the browser UI calls. <c>halyard commands --json</c> prints it; a test fails when a route has no entry here or an entry has
    /// no route, so a new action cannot ship without its command.
    /// </summary>
    public static class CommandRegistry
    {
        public const int SchemaVersion = 1;

        private static CommandInfo C(string name, string method, string route, string summary, bool writes = false, bool isAsync = false)
        {
            return new CommandInfo { Name = name, Method = method, Route = route, Summary = summary, Writes = writes, Async = isAsync };
        }

        public static readonly IReadOnlyList<CommandInfo> All = new List<CommandInfo>
        {
            C("halyard health check", "GET", "/api/health", "Is the engine up (open, no session token)."),
            C("halyard status show", "GET", "/api/status", "Engine, provider, snapshot and job state."),
            C("halyard session stop", "POST", "/api/session/stop", "Stop the engine.", true),
            C("halyard heartbeat send", "POST", "/api/heartbeat", "Tell the engine the browser tab is still open."),
            C("halyard commands list", "GET", "/api/commands", "List every command with its method and route."),
            C("halyard config show", "GET", "/api/config", "Settings with secrets left out."),
            C("halyard config set", "POST", "/api/config", "Change the settings the browser may change."),
            C("halyard schema show", "GET", "/api/schema", "Work item schema, required meta and deficiency rules."),
            C("halyard diagnostics show", "GET", "/api/diagnostics", "Recent log lines and runtime facts."),
            C("halyard writes list", "GET", "/api/writes", "Write policy and the write audit."),
            C("halyard provider test", "POST", "/api/provider/test", "Check the provider connection and token."),
            C("halyard provider repositories", "GET", "/api/provider/repositories", "Repositories the token can see."),
            C("halyard provider labels", "GET", "/api/provider/labels", "Labels in a repository."),
            C("halyard snapshot sync", "POST", "/api/sync", "Synchronize repositories into a snapshot.", false, true),
            C("halyard snapshot show", "GET", "/api/snapshot", "The current snapshot."),
            C("halyard snapshot list", "GET", "/api/snapshots", "Saved snapshots."),
            C("halyard review show", "GET", "/api/review", "Audit findings and deficiencies per item."),
            C("halyard hierarchy show", "GET", "/api/hierarchy", "The work item tree with rollups."),
            C("halyard item show", "GET", "/api/item", "One work item by key."),
            C("halyard item get", "GET", "/api/items/{number}", "One sandbox work item by number."),
            C("halyard item links", "GET", "/api/items/{number}/links", "Native GitLab issue links of an item (needs HALYARD_GITLAB_VERSION)."),
            C("halyard leadership show", "GET", "/api/leadership", "Feature progress, velocity, readiness, capacity, blockers."),
            C("halyard leadership report", "GET", "/api/leadership/report.html", "The leadership report as self-contained HTML."),
            C("halyard team show", "GET", "/api/team", "Teams, people, skills and the sprint calendar."),
            C("halyard export semaphore", "POST", "/api/export/semaphore", "Write the backlog-health CSV tables for Semaphore to output/exports/semaphore/."),
            C("halyard export table", "GET", "/api/export/semaphore/{table}", "One backlog-health table (health, items, features, velocity) as CSV."),
            C("halyard team refresh", "POST", "/api/team/shellback/refresh", "Re-read the Shellback export or endpoint (consented cards only)."),
            C("halyard sprint review", "GET", "/api/sprint-review", "Planned versus delivered per finished sprint and the recommended commitment."),
            C("halyard goal ready", "POST", "/api/goals/ready", "Run the ordered backlog-ready steps; stops at the first with proposals."),
            C("halyard goal list", "GET", "/api/goals", "Recent goal runs."),
            C("halyard scrum brief", "GET", "/api/scrum/brief", "Sprint burn, standup list, risks, ceremonies, retro prompts and next best actions."),
            C("halyard agent ask", "POST", "/api/agent/ask", "Ask the crew (product owner and scrum master); it reads and proposes, never writes."),
            C("halyard agent watch", "POST", "/api/agent/watch", "The weekly watch: ready goal, scrum brief and note, saved under output/watch/ (proposals only)."),
            C("halyard agent watches", "GET", "/api/agent/watches", "Recent weekly watch reports."),
            C("halyard voice status", "GET", "/api/voice/status", "Whether TalkForge (spoken answers) and its Whisper files (voice input) are ready."),
            C("halyard voice start", "POST", "/api/voice/start", "Start Halyard's TalkForge copy when it is not running."),
            C("halyard voice voices", "GET", "/api/voice/voices", "The TalkForge voices installed on this PC, its default first."),
            C("halyard voice speak", "POST", "/api/voice/speak", "Speak one piece of text through TalkForge; returns WAV audio."),
            C("halyard handoff preview", "POST", "/api/intake/handoff/preview", "Drafts for a fleet-handoff file (PacketRat, Eight Bells, dictation)."),
            C("halyard handoff run", "POST", "/api/intake/handoff/run", "Copy a fleet-handoff file into the sandbox through Intake.", true, true),
            C("halyard leadership notify", "POST", "/api/leadership/notify", "Post the leadership summary to the configured Teams/Slack webhook (dryRun previews).", true),
            C("halyard sandbox preview", "GET", "/api/sandbox/preview", "What a seed would create."),
            C("halyard sandbox seed", "POST", "/api/sandbox/seed", "Seed the sandbox with synthetic items.", true, true),
            C("halyard sandbox teardown", "POST", "/api/sandbox/teardown", "Delete synthetic items from the sandbox.", true, true),
            C("halyard sandbox runs", "GET", "/api/sandbox/runs", "Seed runs and their manifests."),
            C("halyard estimate run", "POST", "/api/intelligence/estimate", "Propose story points or feature effort (model).", false, true),
            C("halyard parents propose", "POST", "/api/intelligence/parents", "Propose parent links from titles and labels."),
            C("halyard refine run", "POST", "/api/intelligence/refine", "Draft missing sections (model).", false, true),
            C("halyard metadata propose", "POST", "/api/intelligence/metadata", "Propose halyard-meta repairs."),
            C("halyard skills tag", "POST", "/api/intelligence/skills", "Propose skills per item.", false, true),
            C("halyard intake sources", "GET", "/api/intake/sources", "Source tickets that could be copied in."),
            C("halyard intake preview", "POST", "/api/intake/preview", "Drafts for the selected source tickets."),
            C("halyard intake run", "POST", "/api/intake/run", "Copy source tickets into the sandbox.", true, true),
            C("halyard intake runs", "GET", "/api/intake/runs", "Intake manifests."),
            C("halyard vision list", "GET", "/api/vision", "Vision drafts."),
            C("halyard vision show", "GET", "/api/vision/{id}", "One vision draft."),
            C("halyard vision draft", "POST", "/api/vision/draft", "Draft a vision and theme links.", false, true),
            C("halyard plan preview", "POST", "/api/plan/preview", "What the sprint planner would place."),
            C("halyard plan propose", "POST", "/api/plan/propose", "Turn the plan into a proposal batch."),
            C("halyard proposal list", "GET", "/api/proposals", "Proposal batches."),
            C("halyard proposal add", "POST", "/api/proposals/manual", "Record a manual edit as a pre-approved proposal."),
            C("halyard proposal show", "GET", "/api/proposals/{batchId}", "One batch with its proposals."),
            C("halyard proposal decide", "POST", "/api/proposals/{batchId}/decide", "Approve, reject or reset proposals."),
            C("halyard proposal apply", "POST", "/api/proposals/{batchId}/apply", "Apply the approved proposals of a batch.", true, true),
            C("halyard proposal apply-all", "POST", "/api/proposals/apply-all", "Apply every approved proposal.", true, true),
            C("halyard proposal delete", "POST", "/api/proposals/{batchId}/delete", "Delete a batch file (the sandbox is untouched)."),
            C("halyard proposal preview", "GET", "/api/proposals/{batchId}/{proposalId}/preview", "Body before and after one proposal."),
            C("halyard apply list", "GET", "/api/applies", "What each apply wrote."),
            C("halyard apply show", "GET", "/api/applies/{runId}", "One apply run with the bodies it replaced."),
            C("halyard apply rollback", "POST", "/api/applies/{runId}/rollback", "Undo an apply run (dryRun previews).", true, true),
            C("halyard apply rollback-last", "POST", "/api/applies/rollback-last", "Undo the latest apply run (dryRun previews).", true, true),
            C("halyard promote status", "GET", "/api/promote/status", "Is production promote enabled."),
            C("halyard promote preview", "POST", "/api/promote/preview", "Create/update/unchanged per sandbox item, with a diff basis."),
            C("halyard promote run", "POST", "/api/promote/run", "Promote into production (typed confirmation, full production gate).", true, true),
            C("halyard job list", "GET", "/api/jobs", "Background jobs."),
            C("halyard job show", "GET", "/api/jobs/{id}", "One job with progress and result."),
            C("halyard job cancel", "POST", "/api/jobs/{id}/cancel", "Cancel a running job."),
            C("halyard llm test", "POST", "/api/llm/test", "Send a test prompt through Fairlead."),
            C("halyard fairlead status", "GET", "/api/fairlead", "Is Fairlead answering, and can Halyard start it."),
            C("halyard fairlead start", "POST", "/api/fairlead/start", "Start Fairlead (its tray app) when it is not running."),
        };

        public static string Json()
        {
            var commands = new JArray(All.Select(c => (object)new JObject
            {
                { "name", c.Name }, { "method", c.Method }, { "route", c.Route }, { "summary", c.Summary }, { "writes", c.Writes }, { "async", c.Async },
            }).ToArray());
            return new JObject { { "app", "halyard" }, { "schemaVersion", SchemaVersion }, { "commands", commands } }.ToString(Formatting.Indented);
        }
    }

    public sealed partial class HalyardHost
    {
        private void RegisterCommandRoutes()
        {
            Router.Get("/api/commands", r => System.Threading.Tasks.Task.FromResult(HttpResponse.Text(200, CommandRegistry.Json(), "application/json; charset=utf-8")));
        }
    }
}
