namespace Halyard.Core.Synthetic
{
    internal sealed class StorySeed
    {
        public StorySeed(string name, string role, string goal, string benefit, string[] criteria, string[] tasks)
        {
            Name = name; Role = role; Goal = goal; Benefit = benefit; Criteria = criteria; Tasks = tasks;
        }

        public string Name;
        public string Role;
        public string Goal;
        public string Benefit;
        public string[] Criteria;
        public string[] Tasks;
    }

    internal sealed class FeatureSeed
    {
        public FeatureSeed(string name, string description, string problem, string approach, string[] scopeIn, string[] doneWhen, StorySeed[] stories)
        {
            Name = name; Description = description; Problem = problem; Approach = approach; ScopeIn = scopeIn; DoneWhen = doneWhen; Stories = stories;
        }

        public string Name;
        public string Description;
        public string Problem;
        public string Approach;
        public string[] ScopeIn;
        public string[] DoneWhen;
        public StorySeed[] Stories;
    }

    internal sealed class EpicSeed
    {
        public EpicSeed(string name, string outcome, string[] successPicture, string[] risks, FeatureSeed[] features)
        {
            Name = name; Outcome = outcome; SuccessPicture = successPicture; Risks = risks; Features = features;
        }

        public string Name;
        public string Outcome;
        public string[] SuccessPicture;
        public string[] Risks;
        public FeatureSeed[] Features;
    }

    internal sealed class ThemeSeed
    {
        public ThemeSeed(string name, string purpose, string[] inScope, string[] outOfScope, EpicSeed[] epics)
        {
            Name = name; Purpose = purpose; InScope = inScope; OutOfScope = outOfScope; Epics = epics;
        }

        public string Name;
        public string Purpose;
        public string[] InScope;
        public string[] OutOfScope;
        public EpicSeed[] Epics;
    }

    /// <summary>
    /// Hand-written, entirely fictional backlog content for a data-platform team. Three Epics per Theme, three Features per Epic, three Story seeds per Feature;
    /// the generator cycles and suffixes when asked for more.
    /// </summary>
    internal static class Vocabulary
    {
        public static readonly string[] TargetMonths = { "2026-11", "2026-12", "2027-01", "2027-02" };

        /// <summary>Skill ids (Shellback taxonomy) a feature in this vocabulary plausibly needs; stories pick from their feature's pair.</summary>
        public static readonly string[][] SkillPool =
        {
            new[] { "sql-pipelines", "data-quality" },
            new[] { "etl-elt", "python-development" },
            new[] { "identity-access", "security-controls" },
            new[] { "infrastructure-as-code", "ci-cd-pipelines" },
            new[] { "monitoring-alerting", "cloud-observability" },
            new[] { "test-automation", "acceptance-criteria" },
            new[] { "technical-writing", "documentation-stewardship" },
            new[] { "streaming-data", "api-design" },
            new[] { "incident-readiness", "resilience-recovery" },
            new[] { "metrics-definition", "dashboard-design" },
            new[] { "configuration-management", "environment-management" },
            new[] { "vulnerability-triage", "audit-evidence" },
        };

        public static readonly string[] OpsBlockers =
        {
            "Waiting on a firewall change request",
            "Service account approval pending",
            "Sandbox environment rebuild in progress",
        };

        public static readonly string[] DependencyBlockers =
        {
            "Upstream schema change not yet published",
            "Needs the ingestion contract from the source team",
            "Blocked on a library upgrade owned by another team",
        };

        public static readonly string[] Milestones = { "FY26-Q1-Sandbox-Baseline", "FY26-Q2-Evidence-Desk", "FY26-Q3-Platform-Hardening", "FY26-Q4-Portfolio-Review" };

        public static readonly string[] TaskVerbs = { "Implement", "Verify", "Document", "Review" };

        private static StorySeed Story(string name, string role, string goal, string benefit, string c1, string c2, string t1, string t2, string t3)
        {
            return new StorySeed(name, role, goal, benefit, new[] { c1, c2 }, new[] { t1, t2, t3 });
        }

        public static readonly ThemeSeed[] Themes =
        {
            new ThemeSeed("Data Platform Foundations",
                "Shared data platform services, environments and operational automation that several product Epics depend on.",
                new[] { "Ingestion contracts and landing zones", "Service identities and least-privilege access", "Reproducible environments and configuration" },
                new[] { "One-off report builds owned by product teams", "Enterprise network tickets owned by other organisations" },
                new[]
                {
                    new EpicSeed("Reliable Data Ingestion",
                        "Verified sample records land in repeatable Bronze workflows with every rejection explained.",
                        new[] { "Every accepted feed carries a schema version and an owner", "Schema failures are quarantined with actionable diagnostics", "Batch counts reconcile to the input on every run" },
                        new[] { "Unannounced upstream changes; mitigate with contract checks", "Quarantine growth without review; mitigate with a weekly triage" },
                        new[]
                        {
                            new FeatureSeed("Source Feed Contracts",
                                "Document and validate each approved sample source before it is ingested.",
                                "Malformed sample fields currently reach Bronze and are discovered downstream.",
                                "Declare a contract per feed and reject records that violate it at the boundary.",
                                new[] { "Contract schema", "Validation report", "Owner sign-off" },
                                new[] { "The contract rejects malformed sample fields with a reason code", "Owners can review successful and failed validations", "Three sample feeds run under contract end to end" },
                                new[]
                                {
                                    Story("Validate required source fields", "data steward", "reject records that lack mandatory identifiers", "invalid records never enter the Bronze feed",
                                        "Samples lacking source_id are rejected with a deterministic reason code", "Valid records keep schema_version and source_id in the validation output",
                                        "Define the required-field list for the sample feed", "Implement the boundary check", "Record a positive and a negative example"),
                                    Story("Detect schema version changes", "pipeline operator", "see when a feed schema changes", "upstream changes are reviewed before promotion",
                                        "Changing schema_version triggers a recorded compatibility check", "A breaking change marks the batch as requiring review",
                                        "Store the last accepted schema version per feed", "Compare on each batch", "Raise a review flag on mismatch"),
                                    Story("Publish contract validation report", "feed owner", "read a per-feed validation summary", "I can fix my feed without asking the platform team",
                                        "The report lists accepted and rejected counts per batch", "Rejection reasons link to the contract clause that failed",
                                        "Design the report layout", "Generate it from the validation log", "Review with one feed owner"),
                                }),
                            new FeatureSeed("Bronze Ingestion Ledger",
                                "Trace every sample ingestion batch and its record counts.",
                                "Recovery work cannot tell which batch a rejected record came from.",
                                "Assign a correlation id per batch and record start, finish and outcome counts.",
                                new[] { "Batch ledger table", "Retry linkage", "Operator query" },
                                new[] { "A batch record tracks start, finish and validation outcome", "A reviewer can follow a rejected record to its source batch", "Retries never duplicate output" },
                                new[]
                                {
                                    Story("Record batch ingestion outcomes", "watchstanding analyst", "find the status of a sample batch", "recovery uses a reproducible audit trail",
                                        "Every batch receives a unique correlation id and outcome", "Successful and rejected counts reconcile to the input count",
                                        "Add the ledger table", "Write outcome rows at batch end", "Verify reconciliation on two batches"),
                                    Story("Retry failed batches safely", "pipeline operator", "retry a failed batch without duplicates", "the Bronze ledger stays consistent",
                                        "A repeated request for a completed batch does not duplicate output", "A retry records a new attempt linked to the original id",
                                        "Make the batch writer idempotent", "Link retry attempts", "Test a double submission"),
                                    Story("Query ledger by correlation id", "support engineer", "look up a batch by its correlation id", "incident notes can cite exact evidence",
                                        "A lookup returns the batch, its counts and its attempts", "Unknown ids return a clear not-found result",
                                        "Expose a lookup function", "Index the correlation id", "Document the lookup in the runbook"),
                                }),
                            new FeatureSeed("Quarantine Triage Workflow",
                                "Give reviewers a bounded queue of quarantined records with next actions.",
                                "Quarantined records accumulate with nobody accountable for clearing them.",
                                "Group quarantined records by reason code and assign a weekly triage owner.",
                                new[] { "Reason-code grouping", "Owner assignment", "Ageing report" },
                                new[] { "Quarantine is grouped by reason code with counts", "Each group has an owner and an age", "Groups older than 14 days are flagged" },
                                new[]
                                {
                                    Story("Group quarantined records by reason", "data steward", "see quarantine grouped by reason code", "I can clear whole classes of failure at once",
                                        "Groups show reason code, count and oldest record age", "Records with no reason code appear in an explicit unknown group",
                                        "Define the grouping query", "Render the grouped view", "Check the unknown group path"),
                                    Story("Assign triage owners per group", "team lead", "assign an owner to each quarantine group", "accountability is visible",
                                        "An owner can be set and changed per group", "Unowned groups are listed first",
                                        "Add the owner field", "Sort unowned first", "Record owner changes"),
                                    Story("Flag ageing quarantine groups", "pipeline operator", "be warned about groups older than two weeks", "stale failures get attention",
                                        "Groups older than 14 days carry a visible flag", "The flag clears when the group empties",
                                        "Compute group age", "Apply the threshold", "Verify the clear path"),
                                }),
                        }),
                    new EpicSeed("Secure Platform Foundations",
                        "A repeatable least-privilege environment for the demonstration data services.",
                        new[] { "Every sample workload has an accountable service identity", "Configuration changes can be reproduced and reviewed", "Denied operations leave an auditable trail" },
                        new[] { "Over-broad permissions; mitigate with explicit test cases", "Configuration drift; mitigate with a versioned manifest" },
                        new[]
                        {
                            new FeatureSeed("Service Identity and Access",
                                "Define service roles and verify sample permissions.",
                                "Shared credentials make it impossible to tell which workload did what.",
                                "Create one identity per workload with documented operations and test denials.",
                                new[] { "Role catalogue", "Permission tests", "Denial audit" },
                                new[] { "Approved example roles provide only the documented operations", "Denied operations produce auditable explanations", "Each workload maps to exactly one identity" },
                                new[]
                                {
                                    Story("Limit sample reader permissions", "platform engineer", "grant a reader access only to published sample objects", "unauthorised modifications are denied",
                                        "The reader role may list and read approved test objects", "The reader role cannot update or delete any sample object",
                                        "Define the reader policy", "Apply it in the test environment", "Run the allow and deny checks"),
                                    Story("Audit access denials", "security reviewer", "review denied access attempts", "the enforcement path is visible",
                                        "Each denied test action records the operation and role", "Audit output excludes credential material",
                                        "Enable denial logging", "Redact secrets from the log", "Review a day of output"),
                                    Story("Map workloads to identities", "platform engineer", "see which identity each sample workload uses", "ownership is unambiguous",
                                        "A table lists each workload with one identity", "Workloads without an identity are flagged",
                                        "Inventory the workloads", "Assign identities", "Publish the mapping"),
                                }),
                            new FeatureSeed("Reproducible Test Environments",
                                "Provision and validate the same approved configuration repeatedly.",
                                "Environments are tuned by hand and cannot be rebuilt the same way twice.",
                                "Capture settings in a versioned manifest and report drift against it.",
                                new[] { "Configuration manifest", "Drift report", "Rebuild procedure" },
                                new[] { "A clean environment reproduces the configuration without manual tuning", "A validation report flags drift and rollback options", "A rebuild takes under one hour" },
                                new[]
                                {
                                    Story("Define a reproducible configuration manifest", "release engineer", "capture approved environment settings", "re-creation is auditable",
                                        "The manifest declares service settings and their version", "A schema check rejects unknown or contradictory fields",
                                        "Draft the manifest schema", "Populate it from the test environment", "Add the schema check"),
                                    Story("Report environment drift", "platform operator", "compare live settings with the manifest", "unexpected changes receive review",
                                        "The report identifies which declared settings differ", "It includes the manifest version and timestamp used",
                                        "Collect live settings", "Diff against the manifest", "Format the report"),
                                    Story("Rebuild the sandbox from the manifest", "release engineer", "rebuild the sandbox from scratch", "recovery does not depend on tribal knowledge",
                                        "A rebuild from the manifest passes the drift report cleanly", "The procedure is documented with timings",
                                        "Script the rebuild", "Time a full run", "Write the runbook entry"),
                                }),
                            new FeatureSeed("Secrets Handling Baseline",
                                "Keep every secret in the approved vault and out of code, logs and issues.",
                                "Tokens occasionally appear in scripts and pasted logs.",
                                "Scan for secret patterns, route all retrieval through the vault and redact logs.",
                                new[] { "Secret pattern scan", "Vault-only retrieval", "Log redaction" },
                                new[] { "No secret pattern is found in the repository or issue bodies", "All retrieval paths use the vault helper", "Logs redact token-shaped strings" },
                                new[]
                                {
                                    Story("Scan repository for secret patterns", "security reviewer", "detect token-shaped strings in the repository", "leaks are caught before review",
                                        "Known token patterns are detected in files and issue bodies", "Findings include file and line without echoing the secret",
                                        "Collect the pattern list", "Implement the scan", "Run it against the sandbox"),
                                    Story("Route token retrieval through the vault", "platform engineer", "retrieve tokens only through the vault helper", "no script holds a secret at rest",
                                        "All sample scripts call the vault helper", "A script with a literal token fails review",
                                        "Inventory retrieval points", "Replace literals with helper calls", "Add a review check"),
                                    Story("Redact tokens from diagnostic logs", "support engineer", "share logs without exposing tokens", "incident notes are safe to circulate",
                                        "Token-shaped strings are replaced with a marker", "Redaction is applied before the log is written",
                                        "Add the redaction filter", "Test with sample tokens", "Document the marker format"),
                                }),
                        }),
                    new EpicSeed("Delivery Assurance and Traceability",
                        "Capabilities are planned, verified and accepted with traceable evidence.",
                        new[] { "Each Story maps to a Feature and has observable acceptance criteria", "Release decisions reference a test result and reviewer evidence", "Topology gaps are visible before planning" },
                        new[] { "Ticket metadata drift; mitigate with a preflight schema audit", "Evidence scattered across tools; mitigate with one packet format" },
                        new[]
                        {
                            new FeatureSeed("Work-Item Traceability",
                                "Present Theme-to-Task links with stable identifiers and reviewable coverage.",
                                "Parents are implied by titles and labels rather than declared.",
                                "Declare parents in cascade-meta and validate the chain on every sync.",
                                new[] { "Parent-chain navigation", "Schema readiness report", "Cycle detection" },
                                new[] { "Every work item can be traced to its declared parent", "Topology validation detects missing and circular relationships", "The chain renders in the browser" },
                                new[]
                                {
                                    Story("Navigate the declared parent chain", "delivery lead", "inspect each Story's Feature, Epic and Theme", "progress reports stay traceable",
                                        "Following any Story parent reaches its Feature, Epic and Theme", "A parent outside the filter is marked as context, not reassigned",
                                        "Build the chain walker", "Render breadcrumbs", "Test the out-of-filter case"),
                                    Story("Review required metadata before planning", "product owner", "see incomplete work-item fields", "planning uses complete requirements",
                                        "Missing required metadata is reported by type", "The original description can be inspected without modification",
                                        "List required keys per type", "Compute the gaps", "Show them beside the item"),
                                    Story("Detect circular parent links", "delivery lead", "be told when parent links form a loop", "rollups never hang on bad data",
                                        "Each member of a cycle is reported once", "Unrelated ancestors of a cycle are not reported",
                                        "Implement cycle detection", "Write the two test cases", "Surface the finding in the audit"),
                                }),
                            new FeatureSeed("Release Evidence Desk",
                                "Package test and review evidence for a release decision.",
                                "Release approval depends on verbal assurances.",
                                "Assemble a packet of Story ids, test outcomes and reviewer decisions per release.",
                                new[] { "Evidence packet", "Acceptance record", "Unverified flag" },
                                new[] { "A packet links the approved Stories and test outcomes", "Unverified delivery claims are identified as unverified", "A packet can be exported for review" },
                                new[]
                                {
                                    Story("Assemble a test evidence packet", "release reviewer", "collect the evidence for a sample release", "approval does not rest on unsupported claims",
                                        "The packet lists related Story ids and pass or fail outcomes", "Missing tests are shown as not verified, never as passed",
                                        "Define the packet format", "Collect outcomes per Story", "Export a sample packet"),
                                    Story("Record acceptance decisions", "product owner", "record human approval for a capability", "completion reflects an explicit decision",
                                        "Each approval records a reviewer role, timestamp and outcome", "A rejected capability stays unaccepted until a new review",
                                        "Add the decision record", "Wire it to the packet", "Test the reject path"),
                                    Story("Export the packet for stakeholder review", "delivery lead", "export a packet as a shareable table", "stakeholders can review without tool access",
                                        "The export lists id, type, points, effort, parent and confidence", "The export carries the generation timestamp",
                                        "Choose the export format", "Generate it from the packet", "Review with one stakeholder"),
                                }),
                            new FeatureSeed("Sprint Capacity Signals",
                                "Show committed points against capacity so planning stays honest.",
                                "Sprints are overcommitted because nobody sees the running total.",
                                "Sum Story points per sprint and compare with the configured capacity bands.",
                                new[] { "Points rollup", "Capacity bands", "Carry-over warning" },
                                new[] { "Points per sprint roll up from Stories only", "Capacity bands come from configuration, not guesses", "Carry-over above the threshold is warned" },
                                new[]
                                {
                                    Story("Roll up story points per sprint", "scrum lead", "see committed points per sprint", "overcommitment is visible at planning",
                                        "Only Story points are summed; Feature effort is excluded", "Stories with TBD points are counted separately",
                                        "Implement the rollup", "Exclude non-Story items", "Show the TBD count"),
                                    Story("Apply configured capacity bands", "scrum lead", "compare commitment against capacity bands", "planning uses agreed limits",
                                        "Committed, buffer and improvement bands come from configuration", "Exceeding the committed band is highlighted",
                                        "Read the capacity config", "Compute the bands", "Highlight the breach"),
                                    Story("Warn on carry-over above threshold", "product owner", "be warned when carry-over exceeds the threshold", "chronic slippage is addressed",
                                        "Carry-over above the configured percentage raises a warning", "The warning names the Stories involved",
                                        "Detect carried-over Stories", "Compare with the threshold", "List the Stories"),
                                }),
                        }),
                }),
            new ThemeSeed("Observability and Reliability",
                "Logs, metrics, alerts and recovery practice for the demonstration platform.",
                new[] { "Structured diagnostics for every service", "Alert routing and on-call practice", "Backup and restore drills" },
                new[] { "Enterprise monitoring platforms owned elsewhere", "Vendor contract negotiations" },
                new[]
                {
                    new EpicSeed("Structured Diagnostics",
                        "Every service emits structured, correlated diagnostics that an operator can follow end to end.",
                        new[] { "Every request carries a correlation id through all services", "Log lines are JSON with a fixed schema", "An operator can reconstruct a failed request in under five minutes" },
                        new[] { "Log volume growth; mitigate with sampling rules", "Schema drift; mitigate with a validator in CI" },
                        new[]
                        {
                            new FeatureSeed("Correlation Ids Everywhere",
                                "Propagate one correlation id across every hop of a request.",
                                "Failures cannot be followed across service boundaries.",
                                "Generate an id at the edge and forward it in headers and log lines.",
                                new[] { "Edge generation", "Header propagation", "Log inclusion" },
                                new[] { "Every log line for a request carries the same id", "The id is returned to the caller", "A missing id is generated rather than failing" },
                                new[]
                                {
                                    Story("Generate correlation ids at the edge", "platform engineer", "assign a correlation id to each incoming request", "downstream logs can be joined",
                                        "Each request without an id receives a new unique id", "The id is echoed in the response headers",
                                        "Add the edge middleware", "Echo the header", "Test with and without an incoming id"),
                                    Story("Propagate ids across service calls", "platform engineer", "forward the correlation id on every outbound call", "the trail is unbroken",
                                        "Outbound calls include the id header", "A dropped header is detected by a test",
                                        "Wrap the outbound client", "Add the test", "Verify across two services"),
                                    Story("Include ids in every log line", "support engineer", "filter logs by correlation id", "incident analysis is fast",
                                        "All structured log lines carry the id field", "Lines without an id fail the schema validator",
                                        "Extend the log schema", "Update the writers", "Run the validator"),
                                }),
                            new FeatureSeed("Log Schema and Validation",
                                "Fix a JSON log schema and validate it in CI.",
                                "Free-text logs cannot be queried reliably.",
                                "Publish a schema, convert writers to it and reject violations in CI.",
                                new[] { "Schema document", "Writer conversion", "CI validator" },
                                new[] { "A published schema describes every field", "All writers emit valid lines", "CI fails on a schema violation" },
                                new[]
                                {
                                    Story("Publish the log schema", "platform engineer", "read a single schema for log lines", "writers and readers agree",
                                        "The schema lists required and optional fields with types", "It is versioned alongside the code",
                                        "Draft the schema", "Review with operators", "Commit it"),
                                    Story("Convert writers to structured output", "platform engineer", "emit schema-valid JSON from every service", "logs are queryable",
                                        "Each service emits valid JSON lines", "Free-text lines are gone from the sample run",
                                        "Replace the formatter", "Run a sample workload", "Check the output"),
                                    Story("Validate logs in CI", "release engineer", "fail CI on log schema violations", "drift is caught early",
                                        "A sample run's logs are validated in CI", "A deliberate violation fails the build",
                                        "Add the validator step", "Seed a violation", "Confirm the failure"),
                                }),
                            new FeatureSeed("Operator Request Replay",
                                "Let an operator reconstruct a failed request from its diagnostics.",
                                "Reconstructing a failure means reading several tools by hand.",
                                "Join all lines for a correlation id and render them as a timeline.",
                                new[] { "Timeline query", "Rendering", "Export" },
                                new[] { "A timeline shows every hop for a correlation id", "Gaps are shown explicitly", "The timeline can be exported" },
                                new[]
                                {
                                    Story("Query all lines for a correlation id", "support engineer", "fetch every log line for a request", "analysis starts from complete data",
                                        "The query returns lines across services ordered by time", "Lines from other requests are excluded",
                                        "Write the query", "Index the id", "Test with overlapping requests"),
                                    Story("Render a request timeline", "support engineer", "see a request as a timeline of hops", "failures are obvious",
                                        "Each hop shows service, duration and outcome", "Missing hops are shown as gaps",
                                        "Design the timeline", "Render from the query", "Check the gap display"),
                                    Story("Export a timeline for incident notes", "incident lead", "attach a timeline to an incident", "evidence is preserved",
                                        "The export contains the ordered hops and the id", "Secrets are redacted in the export",
                                        "Choose the export format", "Apply redaction", "Attach to a sample incident"),
                                }),
                        }),
                    new EpicSeed("Alerting That Means Something",
                        "Alerts page a human only for conditions that need a human, with a runbook attached.",
                        new[] { "Every alert links to a runbook", "Noisy alerts are tuned or removed within a sprint", "On-call acknowledges within the target" },
                        new[] { "Alert fatigue; mitigate with a weekly noise review", "Runbook rot; mitigate with an owner per runbook" },
                        new[]
                        {
                            new FeatureSeed("Alert Catalogue and Runbooks",
                                "Inventory every alert and attach a runbook with an owner.",
                                "Alerts fire without guidance on what to do.",
                                "List the alerts, write a runbook per alert and assign an owner.",
                                new[] { "Alert inventory", "Runbook template", "Owner assignment" },
                                new[] { "Every alert has a runbook link", "Every runbook has an owner", "Orphan alerts are removed" },
                                new[]
                                {
                                    Story("Inventory existing alerts", "operations lead", "see every configured alert in one list", "nothing fires unexpectedly",
                                        "The inventory lists each alert with its condition and target", "Alerts with no target are flagged",
                                        "Export the alert config", "Normalise it into the inventory", "Flag untargeted alerts"),
                                    Story("Attach a runbook to each alert", "on-call engineer", "open the runbook from the alert", "I know what to do at 3 a.m.",
                                        "Each alert carries a runbook link", "The runbook follows the template",
                                        "Write the template", "Draft runbooks", "Link them"),
                                    Story("Assign runbook owners", "operations lead", "know who maintains each runbook", "runbooks stay current",
                                        "Each runbook names an owner role", "Unowned runbooks are listed",
                                        "Add the owner field", "Fill it in", "List the gaps"),
                                }),
                            new FeatureSeed("Noise Reduction Review",
                                "Review alert volume weekly and tune or retire noisy alerts.",
                                "Most pages are ignored because most pages are noise.",
                                "Measure pages per alert, review weekly and act on the top offenders.",
                                new[] { "Volume report", "Weekly review", "Tuning changes" },
                                new[] { "A weekly report ranks alerts by pages", "The top three are tuned or retired each week", "Pages per week fall below the target" },
                                new[]
                                {
                                    Story("Report pages per alert per week", "operations lead", "see which alerts page most", "tuning targets the real noise",
                                        "The report ranks alerts by pages in the last seven days", "It shows acknowledgements and no-action outcomes",
                                        "Collect page events", "Aggregate weekly", "Render the ranking"),
                                    Story("Tune a noisy alert threshold", "on-call engineer", "raise the threshold on a noisy alert", "pages drop without losing coverage",
                                        "The change is recorded with the before and after thresholds", "Pages for the alert fall in the next week",
                                        "Pick the top offender", "Adjust the threshold", "Compare the following week"),
                                    Story("Retire an alert with no action history", "operations lead", "retire alerts nobody acts on", "on-call trusts the remaining alerts",
                                        "An alert with zero actions in 30 days can be retired", "Retirement is recorded in the inventory",
                                        "Find no-action alerts", "Confirm with the owner", "Retire and record"),
                                }),
                            new FeatureSeed("On-Call Acknowledgement Tracking",
                                "Track time to acknowledge and resolve for each page.",
                                "Nobody knows whether pages are answered within the target.",
                                "Record page, acknowledge and resolve timestamps and report against targets.",
                                new[] { "Timestamp capture", "Target comparison", "Trend report" },
                                new[] { "Each page has acknowledge and resolve times", "A report compares them with the target", "Trends are visible over eight weeks" },
                                new[]
                                {
                                    Story("Capture acknowledge and resolve times", "on-call engineer", "have my acknowledgements recorded automatically", "reporting is accurate without manual entry",
                                        "Acknowledge and resolve timestamps are stored per page", "Missing acknowledgements are shown as open",
                                        "Hook the acknowledgement event", "Store the timestamps", "Test an unacknowledged page"),
                                    Story("Compare response times with targets", "operations lead", "see pages that missed the target", "coaching is based on data",
                                        "Pages exceeding the target are listed with the overrun", "The target is read from configuration",
                                        "Read the target", "Compute overruns", "List them"),
                                    Story("Show an eight-week response trend", "operations lead", "see whether response times are improving", "the team sees progress",
                                        "A chart shows weekly median acknowledge time", "Weeks with no pages are shown as empty, not zero",
                                        "Aggregate weekly medians", "Render the chart", "Check the empty-week case"),
                                }),
                        }),
                    new EpicSeed("Backup and Recovery Practice",
                        "Backups are proven by restore drills, and recovery objectives are met on paper and in practice.",
                        new[] { "Every critical store has a tested restore procedure", "A quarterly drill meets the recovery time objective", "Drill results are recorded with evidence" },
                        new[] { "Drills skipped under delivery pressure; mitigate with a calendar gate", "Backups that silently fail; mitigate with verification jobs" },
                        new[]
                        {
                            new FeatureSeed("Backup Verification Jobs",
                                "Verify every backup automatically after it completes.",
                                "A backup that cannot be restored is not a backup.",
                                "Run an integrity check and a sample restore after each backup.",
                                new[] { "Integrity check", "Sample restore", "Failure alert" },
                                new[] { "Each backup is integrity-checked", "A sample restore runs daily", "A failed verification raises an alert" },
                                new[]
                                {
                                    Story("Integrity-check each backup", "platform operator", "know a backup file is intact", "restores do not fail on corrupt files",
                                        "Each backup's checksum is verified after write", "A mismatch is recorded and alerted",
                                        "Compute the checksum", "Verify after write", "Alert on mismatch"),
                                    Story("Run a daily sample restore", "platform operator", "restore a sample from the latest backup every day", "restore paths are exercised continuously",
                                        "A sample table is restored to an isolated target daily", "The restored row count matches the source",
                                        "Script the sample restore", "Schedule it", "Compare counts"),
                                    Story("Alert on verification failure", "on-call engineer", "be paged when a backup verification fails", "silent failures are impossible",
                                        "A failed check pages on-call with the backup id", "The alert links to the verification runbook",
                                        "Define the alert", "Link the runbook", "Test with a forced failure"),
                                }),
                            new FeatureSeed("Restore Drill Programme",
                                "Run quarterly restore drills against the recovery objectives.",
                                "Recovery objectives exist on paper only.",
                                "Schedule drills, time them and record evidence against the objectives.",
                                new[] { "Drill calendar", "Timing capture", "Evidence record" },
                                new[] { "A drill is scheduled every quarter", "Each drill records time to recover", "Evidence is attached to the drill record" },
                                new[]
                                {
                                    Story("Schedule quarterly restore drills", "operations lead", "have drills on the calendar", "they are not skipped under pressure",
                                        "A drill entry exists for each quarter", "The entry names a scenario and an owner",
                                        "Create the calendar entries", "Assign owners", "Publish the schedule"),
                                    Story("Time a full restore drill", "platform operator", "measure time to recover during a drill", "the objective is tested, not assumed",
                                        "Start and finish times are recorded", "The result is compared with the objective",
                                        "Run the drill", "Capture the timings", "Compare with the objective"),
                                    Story("Record drill evidence", "operations lead", "keep evidence from each drill", "audits have something to read",
                                        "The record links logs, timings and the outcome", "Secrets are absent from the evidence",
                                        "Define the evidence record", "Collect artefacts", "Redact and store"),
                                }),
                            new FeatureSeed("Recovery Objective Review",
                                "Review recovery objectives against drill results and adjust.",
                                "Objectives were set once and never revisited.",
                                "Compare drill timings with objectives each quarter and propose changes.",
                                new[] { "Objective register", "Gap analysis", "Change proposal" },
                                new[] { "Objectives are listed per store", "Gaps between drill results and objectives are shown", "Proposed changes are reviewed" },
                                new[]
                                {
                                    Story("Maintain the recovery objective register", "operations lead", "see the recovery objectives per store", "everyone works from the same numbers",
                                        "Each critical store has a recovery time and point objective", "Stores without objectives are flagged",
                                        "List the stores", "Record the objectives", "Flag the gaps"),
                                    Story("Compare drill results with objectives", "operations lead", "see where drills missed the objective", "improvement is targeted",
                                        "Each drill is compared with its store's objective", "Misses are listed with the overrun",
                                        "Join drills to objectives", "Compute overruns", "List the misses"),
                                    Story("Propose objective changes for review", "operations lead", "propose a revised objective with evidence", "objectives stay realistic",
                                        "A proposal cites the drill results it is based on", "Proposals require a reviewer decision",
                                        "Draft the proposal template", "Attach evidence", "Route for review"),
                                }),
                        }),
                }),
            new ThemeSeed("Automation and Agentic Workflows",
                "Scripting, bots and AI-assisted operators that remove manual toil under human gates.",
                new[] { "Backlog intake and refinement assistants", "Sandbox-first write automation", "Evidence packaging" },
                new[] { "Unattended writes to systems of record", "Model training or fine-tuning" },
                new[]
                {
                    new EpicSeed("Human-Gated Backlog Assistant",
                        "An assistant proposes structure and sizing; a human approves before anything is written.",
                        new[] { "Every proposal shows its evidence", "Nothing reaches the sandbox without an approval record", "Rejected proposals are kept for learning" },
                        new[] { "Over-trust in proposals; mitigate with confidence labels", "Prompt drift; mitigate with versioned prompts" },
                        new[]
                        {
                            new FeatureSeed("Story Point Proposals",
                                "Propose story points for unsized Stories with reasons.",
                                "Half the backlog has no size, so velocity is a guess.",
                                "Send definable Stories to the model, parse the result, clamp it to policy and queue for approval.",
                                new[] { "Definability gate", "Proposal parsing", "Approval queue" },
                                new[] { "Only Stories with acceptance criteria are sent", "Each proposal carries points, confidence and a reason", "Approved proposals apply to the sandbox only" },
                                new[]
                                {
                                    Story("Gate proposals on story definability", "product owner", "skip Stories that lack acceptance criteria", "sizes are not invented from thin descriptions",
                                        "Stories without acceptance criteria are marked TBD with the reason", "Definable Stories proceed to proposal",
                                        "Implement the definability check", "Mark the TBD reason", "Test both paths"),
                                    Story("Parse and clamp point proposals", "delivery lead", "receive proposals that follow the points policy", "13s and 21s never land",
                                        "Values above 8 are clamped to 8 with a note", "Unparseable output yields TBD, never a guess",
                                        "Implement the parser", "Add the clamp", "Test the unparseable case"),
                                    Story("Queue proposals for approval", "product owner", "approve or reject each proposal", "nothing is applied without me",
                                        "Each proposal can be approved or rejected with a note", "Only approved proposals are eligible to apply",
                                        "Build the queue", "Record decisions", "Filter eligible proposals"),
                                }),
                            new FeatureSeed("Parent Link Suggestions",
                                "Suggest parents for orphaned Stories and Features with evidence.",
                                "Orphans accumulate and rollups are wrong.",
                                "Rank candidate parents by shared keywords, theme and labels; never assign automatically.",
                                new[] { "Candidate ranking", "Evidence display", "Apply on approval" },
                                new[] { "Each orphan shows ranked candidates with evidence", "A candidate is applied only after approval", "The applied link updates meta and mirror together" },
                                new[]
                                {
                                    Story("Rank candidate parents by evidence", "product owner", "see likely parents for an orphan", "I decide from evidence",
                                        "Candidates are ranked with a visible score and reasons", "Candidates of the wrong type are excluded",
                                        "Implement scoring", "Filter by expected type", "Render the evidence"),
                                    Story("Apply an approved parent link", "product owner", "apply the parent I approved", "meta and body stay in sync",
                                        "The parent id is written to meta and the Halyard mirror", "The sandbox issue shows the updated parent",
                                        "Build the patch", "Apply through the gateway", "Verify the mirror"),
                                    Story("Keep rejected suggestions out of future runs", "product owner", "not see a suggestion I rejected", "review time is not wasted",
                                        "Rejected candidates are stored per item", "They are excluded from the next ranking",
                                        "Store rejections", "Filter them", "Test a repeated run"),
                                }),
                            new FeatureSeed("Proposal Evidence Trail",
                                "Keep the prompt, response and decision for every proposal.",
                                "Nobody can explain why a size was proposed last month.",
                                "Store request and response transcripts with the decision, redacted.",
                                new[] { "Transcript storage", "Redaction", "Decision linkage" },
                                new[] { "Each proposal links to its transcript", "Transcripts contain no tokens", "Decisions reference the proposal id" },
                                new[]
                                {
                                    Story("Store proposal transcripts", "delivery lead", "read the exchange behind a proposal", "sizes are explainable",
                                        "Request and response are stored per proposal", "The transcript is linked from the proposal",
                                        "Write transcripts", "Link them", "Open one from a proposal"),
                                    Story("Redact tokens from transcripts", "security reviewer", "be sure transcripts hold no secrets", "they can be shared",
                                        "Authorization headers are never written", "Token-shaped strings in bodies are masked",
                                        "Exclude headers", "Mask bodies", "Scan a sample set"),
                                    Story("Link decisions to proposals", "product owner", "see my decision next to the proposal", "the audit trail is complete",
                                        "Each decision stores proposal id, outcome and note", "The proposal view shows its decision",
                                        "Add the decision store", "Join on proposal id", "Render it"),
                                }),
                        }),
                    new EpicSeed("Sandbox-First Write Automation",
                        "Automation writes only to the sandbox, through one gate, with a full audit.",
                        new[] { "One code path performs every write", "Every write is audited with intent and outcome", "Production writes are impossible without three explicit switches" },
                        new[] { "Bypass through a convenience script; mitigate with a static check", "Audit log growth; mitigate with rotation" },
                        new[]
                        {
                            new FeatureSeed("Single Write Gateway",
                                "Route every create, update and delete through one policy-checked gateway.",
                                "Several scripts call the API directly, each with its own safety check.",
                                "Make the writer interface private to the gateway and fail any other caller.",
                                new[] { "Gateway class", "Policy checks", "Static enforcement" },
                                new[] { "No code outside the gateway can obtain a writer", "A write to a non-sandbox target throws", "A static test fails on bypass" },
                                new[]
                                {
                                    Story("Block writes outside the sandbox", "security reviewer", "see non-sandbox writes refused", "production is safe by construction",
                                        "A write to a non-sandbox repository throws before any call", "The refusal is audited",
                                        "Implement the policy check", "Throw before the call", "Audit the refusal"),
                                    Story("Require three switches for production", "security reviewer", "see production writes need mode, env and intent", "no single mistake can reach production",
                                        "Any missing switch blocks the write", "All three present permit the approved target only",
                                        "Add the three checks", "Test each missing combination", "Test the approved path"),
                                    Story("Fail the build on gateway bypass", "release engineer", "catch direct API writes in CI", "the gate cannot rot",
                                        "A write call outside the gateway fails a static test", "The test lists the offending file",
                                        "Write the static scan", "Add it to CI", "Seed a bypass to verify"),
                                }),
                            new FeatureSeed("Write Audit Log",
                                "Record every write attempt with intent, target and outcome.",
                                "After a bad batch nobody can list exactly what changed.",
                                "Append an audit entry per operation and expose it in the browser.",
                                new[] { "Audit entries", "Browser view", "Rotation" },
                                new[] { "Every operation leaves an entry, success or failure", "The browser shows the latest entries", "The log rotates without losing recent entries" },
                                new[]
                                {
                                    Story("Record every write attempt", "delivery lead", "see each write with its outcome", "batches are reviewable",
                                        "Successful and failed operations both appear", "Entries carry operation, target, number and reason",
                                        "Add the entry model", "Write on each operation", "Test a failure"),
                                    Story("Show the audit log in the browser", "product owner", "review recent writes in the UI", "I do not need the console",
                                        "The latest entries are listed newest first", "Failures are highlighted",
                                        "Add the endpoint", "Render the table", "Style failures"),
                                    Story("Rotate the audit log", "platform operator", "keep the log bounded", "the host stays responsive",
                                        "The in-memory log is capped", "Older entries are flushed to a file",
                                        "Cap the list", "Flush to file", "Test the cap"),
                                }),
                            new FeatureSeed("Synthetic Sandbox Lifecycle",
                                "Seed and tear down a synthetic hierarchy on demand.",
                                "Testing against real backlogs is slow and risky.",
                                "Generate a deterministic catalogue, seed it with run labels and delete by run.",
                                new[] { "Deterministic catalogue", "Seed with run label", "Teardown by run" },
                                new[] { "The same seed produces the same catalogue", "Every seeded item carries the run label", "Teardown removes only labelled items" },
                                new[]
                                {
                                    Story("Generate a deterministic catalogue", "developer", "regenerate the same synthetic backlog", "fixtures can be diffed",
                                        "The same seed and options produce identical output", "The catalogue validates against the schema",
                                        "Implement the generator", "Snapshot the output", "Compare two runs"),
                                    Story("Seed the sandbox with run labels", "developer", "seed a run I can identify later", "teardown is precise",
                                        "Every created item carries the synthetic and run labels", "The seed manifest lists every created number",
                                        "Ensure the labels", "Create in parent order", "Write the manifest"),
                                    Story("Tear down a run without touching anything else", "developer", "delete exactly one run", "other work is untouched",
                                        "Only items with the run label are deleted", "Unlabelled items are skipped and reported",
                                        "Query by label", "Delete through the gateway", "Report skips"),
                                }),
                        }),
                    new EpicSeed("Evidence Packaging",
                        "Reviews and leadership updates are generated from evidence, not retyped.",
                        new[] { "A review pack is one click from the current snapshot", "Leadership views show progress by Theme and sprint", "Exports are reproducible" },
                        new[] { "Stale snapshots; mitigate with a visible snapshot age", "Over-aggregation; mitigate with drill-down links" },
                        new[]
                        {
                            new FeatureSeed("Review Pack Export",
                                "Export the review table for a stakeholder session.",
                                "Review prep is a manual copy-paste exercise.",
                                "Render id, type, points, effort, parent and confidence from the snapshot.",
                                new[] { "Table export", "Filters", "Timestamp" },
                                new[] { "The export matches the filtered view", "It carries the snapshot time", "It opens in a spreadsheet" },
                                new[]
                                {
                                    Story("Export the filtered review table", "product owner", "export what I am looking at", "the session uses the same data",
                                        "The export contains exactly the filtered rows", "Columns match the on-screen table",
                                        "Capture the filter", "Render rows", "Download"),
                                    Story("Stamp exports with snapshot time", "delivery lead", "know how old an export is", "stale data is obvious",
                                        "The export header shows the snapshot time", "A snapshot older than a day is flagged",
                                        "Add the header", "Compute age", "Flag old snapshots"),
                                    Story("Open exports in a spreadsheet", "stakeholder", "open the export without special tools", "review is frictionless",
                                        "The export is valid CSV with a header row", "Multi-line text is escaped",
                                        "Choose CSV", "Escape fields", "Open in a spreadsheet"),
                                }),
                            new FeatureSeed("Leadership Progress View",
                                "Show progress by Theme and sprint without exposing noise.",
                                "Leadership asks for status and receives a backlog dump.",
                                "Aggregate points and states by Theme and sprint with drill-down.",
                                new[] { "Theme rollup", "Sprint slices", "Drill-down" },
                                new[] { "Points roll up by Theme", "Sprint slices show finished increments", "Every number drills to its Stories" },
                                new[]
                                {
                                    Story("Roll up points by Theme", "portfolio lead", "see points by Theme", "investment is visible",
                                        "Each Theme shows total and completed points", "Themes with no Stories show zero, not blank",
                                        "Walk the hierarchy", "Sum per Theme", "Render"),
                                    Story("Show sprint slices as finished increments", "portfolio lead", "see what finished each sprint", "progress means shipped slices",
                                        "Closed Stories are grouped by sprint", "Open Stories are not counted as progress",
                                        "Group by sprint", "Filter by state", "Render the slices"),
                                    Story("Drill from a number to its Stories", "portfolio lead", "click a number and see the Stories behind it", "nothing is hidden",
                                        "Every aggregate links to the contributing Stories", "The drill-down keeps the current filter",
                                        "Add links", "Carry the filter", "Test a drill"),
                                }),
                            new FeatureSeed("Reproducible Exports",
                                "Make every export reproducible from a snapshot id.",
                                "Two people export different numbers for the same meeting.",
                                "Key every export on a snapshot id and regenerate on demand.",
                                new[] { "Snapshot ids", "Regeneration", "Comparison" },
                                new[] { "Each export names its snapshot id", "The same id regenerates the same export", "Two exports can be compared" },
                                new[]
                                {
                                    Story("Name the snapshot in every export", "delivery lead", "see which snapshot an export came from", "disagreements are resolvable",
                                        "The snapshot id appears in the export header", "Exports without an id are rejected",
                                        "Add the id", "Reject missing ids", "Check an export"),
                                    Story("Regenerate an export from its snapshot id", "delivery lead", "regenerate a past export", "lost files are not a problem",
                                        "Regeneration from the id yields identical content", "Unknown ids return a clear error",
                                        "Store snapshots by id", "Regenerate", "Test an unknown id"),
                                    Story("Compare two exports", "product owner", "see what changed between two exports", "progress is explainable",
                                        "Added, removed and changed rows are listed", "Unchanged rows are hidden by default",
                                        "Diff the rows", "Render the categories", "Hide unchanged"),
                                }),
                        }),
                }),
        };
    }
}
