# Cascade Autonomous Product Owner — target architecture and delivery contract

Status: **design specification**, not proof of deployed unattended automation.

**Implementation blueprint:** [Cascade Autonomous PO Technical Blueprint](architecture/CASCADE_AUTONOMOUS_PO_TECHNICAL_BLUEPRINT.md) — 21 sections with system/sequence diagrams, API contracts, draft relational schema, deployment boundaries, reviewer memory, event replay, publication safety and acceptance criteria. Existing deterministic local discovery and read-only intelligence review remain the starting point. The deployment is contingent on approved Git.mil, compute, AI endpoint, and credential access.

## Product promise

When an engineer contributes to an authorized repository, Cascade should discover that work, understand its engineering intent, reconstruct the appropriate Agile work items (including already implemented work), propose hierarchy, assess qualitative risk and complexity, estimate *hypothetical* AI-assisted and unaided implementation effort, and put decisions in a ceremony review queue. The engineer should not have to become the project's bookkeeper. Git history must never be represented as employee timekeeping, actual hours, acceptance, or productivity scores.

## Current capabilities (checked against repository)

- `src/Cascade/Autonomy.ps1`: offline Git discovery clustering, evidence-based work-category heuristics, reconciliation against previously recorded evidence keys, local JSON plan, read-only leadership brief; sandbox-only explicit issue creation.
- `src/Cascade/IntelligenceReview.ps1` plus the browser dialog: deterministic normalization, human edits and section approvals, relationship candidates, **session-local** decisions and JSON export. No autonomous LLM call, server-side review memory, or production publication step.
- `ui/app.js`: Work Intelligence is still local discovery/reconciliation triage; the Intelligence Workbench does not hand approved records into it as a shared transaction. Existing sync is a synchronous HTTP request, not a job queue.
- `src/Cascade/GenAI.ps1`: existing ARC-backed model profiles and `Invoke-CascadeChat` exist, but must be integrated into a governed asynchronous workflow.
- `.github/workflows/cascade-checks.yml`: GitHub Actions runs tests; this is **not** an internal Git.mil production worker.

## Execution environment

Preferred: dedicated *authorized internal* Cascade worker/service plus durable event queue and approved GitLab API access. Optional Git.mil self-managed runner can trigger or operate the worker. The source of truth is Git.mil, not GitHub.com.

Runner vs worker distinction:
- GitLab CI runners poll for eligible pipeline jobs and execute them; they do not independently infer a developer's intent.
- GitLab push, merge-request, pipeline and issue hooks can enqueue events to an authorized, authenticated callback, with scheduled GitLab API reconciliation as the reliability backstop.
- A persistent worker is preferable for large diff processing, review-memory retrieval, retries and ceremony scheduling; runners may be short-lived.
- When webhook or runner administration is unavailable, a centrally scheduled **read-only** poller can ingest event cursors, but must use authorized authentication and compute.
- No automatic Git.mil -> public GitHub or personal-machine data transfer. Do not register or run a runner on managed Navy endpoints without approval. Do not install blocked tooling, evade controls, or forward internal evidence to an unapproved AI endpoint.
- On an authorized Windows PowerShell 5.1 host, use PS 5.1 and signed/approved .NET capabilities; do not require Python/Node/Go/Rust installations.

## Event-to-work pipeline

1. **Observe:** capture project ID, event UUID, branch, MR IID, commit SHA, pipeline ID, actor as source metadata only; acknowledge and enqueue quickly. Make project/branch allowlists explicit.
2. **Reconcile:** periodically poll authorized Git.mil API for commit/MR/issue/pipeline gaps. Use durable per-project cursors, backoff and dedup by event UUID or `(project,kind,object_sha,revision)`.
3. **Evidence package:** collect changed paths, bounded code diffs, README/ADRs, test changes, CI result, MR discussion, linked issue data and approved taxonomy. Avoid executing untrusted repository code during analysis. Preserve immutable SHA provenance; redact secrets and exclude restricted files.
4. **Reconstruct intent:** cluster related commits/MRs by repository, branches, dependency context and semantic evidence. Distinguish implementation observed from accepted/delivered.
5. **Reconcile existing work first:** identify existing issue by explicit issue refs, Cascade evidence keys, canonical IDs, MR links and scoped fuzzy candidate matching. Never duplicate an existing item on weaker semantic similarity alone.
6. **Normalize:** synthesize type-specific proposed title, objective, content, definition of done, acceptance criteria, test evidence and provenance. Generate retrospective items when work is already in code, with an explicit `RETROACTIVE_CAPTURE` source tag and observed date; do not rewrite history.
7. **Associate:** Theme -> Epic -> Feature -> Story -> Task hierarchy with unique same-project eligible IDs, acyclic links, accepted scope rules and evidence-ranked candidates. Keep crosscutting `relates-to`, `blocks`, and `duplicates` links distinct from parents. Unresolved associations go to the review queue; no arbitrary parent.
8. **Estimate:** produce a proposed Fibonacci story-point complexity band and separate *hypothetical* AI-assisted and unaided human effort ranges, including assumptions and confidence. Do **not** claim actual worked hours, compare developer performance or infer time from commits/LOC. Recalibrate to team-agreed reference stories and approved historical retrospectives.
9. **Stage:** construct immutable, versioned, diffable create/update/link/status proposals with per-field confidence, evidence links, source fingerprint, and permitted write target. Validate schema, permissions, scope, conflict and idempotency.
10. **Review and promote:** show high-confidence proposals in an auto-prepared queue and exceptions in the Intelligence Workbench. Keep final apply separate: work item snapshot -> reviewer authorization -> optimistic concurrency and duplicate recheck -> authorized Git.mil patch -> audit receipt. No automatic close absent explicit policy and acceptance evidence.
11. **Learn:** convert **explicitly approved** corrections to scoped review memories; retrieve them for the next proposal, never treating unreviewed output as ground truth.
12. **Ceremony:** generate an agenda with retrospective captures, unresolved questions, hierarchy conflicts, estimate changes and items awaiting approval, grouped by project/team/sprint. Completed-code does not imply accepted-story status.

## UX workflow

- **Autonomous Inbox:** live status counts, newly observed work, reconstructed clusters, queue freshness, failed ingestion and retry.
- **Intelligence Workbench:** inspect model proposals, approve/edit/reject *individual fields*, and confirm parents, related links, estimates and evidence. Autosave reviewers' work to approved storage; current session-local state must be replaced.
- **Work Intelligence:** authoritative staged work-item reconciler. Show before/after Git.mil diff, grouped Theme/Epic/Feature hierarchy, duplicate detection, policy checks, linked MRs, and readiness to publish. A review approval should flow here only after the whole candidate validates.
- **Publication:** separate privileged operation with explicit project/scope, dry-run, authorized action, optimistic concurrency and rollback/change receipt. Do not call a JSON export an actual Git.mil publication.
- **Ceremony Queue:** time-window-specific review agenda. Filter `retroactive`, `new`, `blocked`, `estimate disputed`, `possible duplicate`, `accepted`. Notifications must identify the meeting calendar/source of truth if one is connected.

Proposed state machine: `OBSERVED -> EVIDENCE_READY -> PROPOSED -> FIELD_REVIEW -> RECONCILED -> APPROVED_FOR_PUBLISH -> PUBLISHED`, with explicit `NEEDS_EVIDENCE`, `CONFLICT`, `REJECTED`, `STALE`, `FAILED_RETRYABLE` side states. Never auto-promote an unverified `PROPOSED` record directly to published or completed.

## Reviewer memory (not bulk prompt stuffing)

Memory should be a **scoped retrieval service**, not one ever-growing mega-prompt. Start with approved, versioned feedback records. A record shape:

```json
{
  "schema": "cascade.review_memory.v1",
  "id": "memory-uuid",
  "scope": {"project_id": 16629, "team_label": "team::Data-Crucible"},
  "kind": "field_correction",
  "field": "parent_id",
  "before": "FEATURE-incorrect",
  "after": "FEATURE-batch-ingestion",
  "rationale": "Domain ownership confirmed by reviewer",
  "evidence": [{"project_id": 16629, "issue_iid": 41, "commit_sha": "example-only"}],
  "review_state": "human_approved",
  "approved_at_utc": "example",
  "supersedes": null,
  "confidence": "verified_in_scope"
}
```

Always namespace by project and allowed team labels. Store only necessary evidence and approved corrections; use audit trails, per-record provenance, data retention and revocation. Do not record secrets, token material or unapproved protected content. Store on approved encrypted internal storage with least-privilege service ACLs, backups and a migration strategy. SQLite with indexes and JSON fields is a strong local/dedicated-worker MVP if approved; a managed internal database is preferable for concurrent multi-user workers. An in-memory browser review map is not durable product memory.

Memory types:
- **Rules:** canonical hierarchy and taxonomy, hard constraints. Highest priority; humans author/version rules.
- **Approved examples:** edited Title/AC/type/parent/estimate pairs with proven scope.
- **Project vocabulary:** domain terms, repo/path ownership and acronyms curated by reviewer.
- **Corrections:** recurring semantic misclassifications, with frequency, negative examples and reviewer rationale.
- **Episodic:** relevant prior decisions, linked to source revision and expiration. Retrieve only when useful.

Retrieval order: hard policy -> scope-filtered exact known mappings -> evidence-based relevant examples -> optional embedding/lexical search -> bounded LLM context. Never retrieve out-of-scope projects, allow untrusted content to override system rules or use AI-proposed memories as approved rules.

After each review, produce a **learning delta** rather than training on every click: only review-approved changes, avoid treating no-edit approval as a universal rule, allow reviewer to discard/revert learning, log which memory IDs affected future suggestions, and use quality metrics like correction rate, acceptance rate, duplicates avoided and relationship accuracy. No productivity scoring by named engineer.

### Token and latency budget

`max_tokens` in the existing GenAI client primarily limits **generated output**, not the combined context capacity. Context-window support is endpoint/model-specific and should be probed on authorized infrastructure rather than assumed. Large context is valuable for code evidence but **more memory tokens is not inherently smarter**.

Suggested *configurable initial budget* (not a claim about GenAi or Grok limits):
- ~2-4k tokens system, schema and policy.
- ~2-5k tokens task, taxonomy, repo map and explicit existing links.
- ~4-8k tokens top-ranked approved memory/examples.
- ~8-24k tokens carefully chunked changed-code evidence.
- ~2-4k tokens reserved model response, as validated by the endpoint.
- Expand context only when capability tests and evaluation show benefit.

Stage the calls: cheap deterministic evidence extraction; classification/retrieval; optional targeted code-analysis calls; then strictly typed proposal output; validator/human review. Cache evidence by source SHA and memory/version hash to avoid repeated large payloads. Summarize huge diffs hierarchically; never upload every file or entire memory history. Support retry/backoff, per-project spend/token limits, prompt injection defenses, audit IDs and incremental reruns.

## Estimation contract

A Story's Fibonacci points represent relative complexity/uncertainty, **not elapsed hours**. Effort scenarios are optional planning hypotheses and never authoritative actuals:
- `story_points_suggested`: 1,2,3,5,8,13 or unresolved.
- `human_unassisted_estimate`: range, e.g., `2-4 workdays`, based on explicit team reference stories; `estimated` not observed.
- `ai_assisted_estimate`: range, e.g., `1-3 workdays`, with a listed tool/workflow assumption; `estimated` not observed.
- `uncertainty`: high/medium/low; drivers include technical novelty, dependencies, test coverage, risk, review, hidden work and environment.
- `evidence_status`: observed change vs tests passed vs MR merged vs owner accepted, all separate.
- Disable people-level productivity rankings; pair programming, AI-generated code, tests and commits do not provide a reliable clock.

If retrospective/accepted work reveals actual effort data **voluntarily recorded for planning**, use team-level calibrated reference ranges only as permitted; never backsolve individual worked hours from a commit count.

## UI synchronization progress

Immediate improvement: show an indeterminate, accessible CSS spinner for the entire `/api/gitmil/sync` request and clear it in `finally`, with button disabled to prevent duplicate calls. This is honest feedback but not proof of detailed progress.

**Real stages require backend refactor:** the current PowerShell HTTP host processes synchronous requests; implement `POST /api/jobs/gitmil-sync` -> `202 {jobId}`, plus `GET /api/jobs/{jobId}` status and stage/count updates (or approved SSE). The job worker records project, page cursor, issues/MRs fetched, completion and failures. UI progress then reports truthful stage names and counts; never invent a percentage. Jobs need cancellation, retry, independent sessions and stale-timeout recovery.

## Security and publication controls

- Approved service credentials, not browser/localStorage tokens, and no interactive CAC prompt from a background request. ARC's interactive session must not be assumed to survive on an unattended server.
- Authenticate and replay-protect webhook events, enforce project allowlists and scopes, use read-only source identity vs narrowly scoped write service identity.
- Reject disallowed repository content and prevent AI prompt injection from deciding write permissions. Analyze code as data; do not execute diffs or third-party scripts.
- Idempotent writes and source revision checks; dry run before apply; audit each item, per-field source and reviewer authorization.
- Require explicit authorized production deployment and mission-data-handling review. No personal workstation, public GitHub runner or unapproved cloud GenAI for internal Navy repository data.

## Milestone acceptance gates

1. **UI now:** spinner + accessibility and test; no backend job progress claim.
2. **Worker MVP:** authorized fixture-only event queue and polling adapter, duplicate/cursor tests; no live Git.mil writes.
3. **Understanding:** repo/MR/CI provenance -> typed structured proposals, existing issue match, hierarchy and dual hypothetical estimates; regression corpus.
4. **Memory v1:** durable scoped approved review memory, retrieval provenance and reviewer controls; demonstration that correction rates fall on held-out examples.
5. **Unified review:** one transactional flow from Intelligence Workbench to Work Intelligence, with reviewer decisions and stale-source conflict checks.
6. **Sandbox write:** integration-tested, idempotent create/update/link, audit and rollback strategy.
7. **Controlled production:** approved internal runner/service, limited pilot, alerting, data-handling approval, queue and ceremony agenda, guarded publication policies.

Owner decisions required for production: approved worker location, Git.mil webhook/CI permissions, review-to-production authorizations, GenAi/Grok deployment authorization, persistent storage location, team taxonomy, ceremony cadence and whether only drafts or selected fields may ever autopublish.
