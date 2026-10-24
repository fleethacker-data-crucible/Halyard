# Cascade Autonomous Product Owner — Technical Architecture Blueprint

**Document class:** Proposed implementation architecture (not deployed functionality)  
**Version:** 1.0 — 2026-10-23  
**Related design:** [AUTONOMOUS_PRODUCT_OWNER_ARCHITECTURE.md](../AUTONOMOUS_PRODUCT_OWNER_ARCHITECTURE.md)  
**Owner:** Cascade engineering  
**Decision state:** Proposed; requires Git.mil security, infrastructure, and Product Owner approval before production deployment.

> **Read this first:** The existing repository has deterministic local discovery, normalization previews, relationship suggestions, browser review, and explicit sandbox-only writes. It does **not** yet have an always-on worker, production Git.mil write-back, durable review memories, or actual-duration inference. All diagrams, APIs, schemas and deployment steps in this blueprint describe a **target design** unless specifically marked "existing."

## 1. Executive architecture decision

Build Cascade as an **evidence-first, event-driven, human-governed autonomous Product Owner**. Its job is to observe authorized engineering activity and prepare high-quality Agile records while minimizing developer administration. It must not misrepresent source activity as accepted product delivery or measure individuals' time/productivity from code activity.

Primary architectural decisions:

1. **Authoritative systems:** Git.mil GitLab remains the source of engineering and work-item facts. Cascade stores imported evidence, proposed changes, review decisions and publication receipts; it never silently becomes the authoritative owner of GitLab issues.
2. **Compute:** A centrally operated, approved **worker service** is preferred. A self-managed GitLab runner is an optional trigger/executor for limited jobs, not the permanent memory store or source of truth.
3. **Ingestion:** Signed/secret-verified internal webhooks plus periodic API reconciliation, persistent checkpoints, retries, duplicate protection and backfill.
4. **Intelligence:** Deterministic discovery, duplicate checks and hierarchy rules wrapped around optional authorized GenAI analysis. LLM output is untrusted proposed data, never a privileged command.
5. **Human control:** Field-level approve/edit/reject is separate from permission to publish. Nothing writes to a production work item without an explicitly authorized publication policy and a versioned changeset.
6. **Learning:** Store *review-approved* memories and retrieve relevant records by project scope and evidence. Do **not** send the entire memory database to each model call.
7. **Estimated effort:** Story points are relative complexity. AI-assisted and unaided effort are hypothetical ranges; Git evidence is **not** a time clock.

## 2. Existing code and target additions

| Existing repository component | Present capability | Target extension |
|---|---|---|
| src/Cascade/Autonomy.ps1 | Local commit discovery, grouping and evidence-key reconciliation | Durable cross-repository MR/commit evidence graph and semantic clusters |
| src/Cascade/GitLab.ps1 | Git.mil API integration | Scoped incremental source adapter, webhook replay checks and write revalidation |
| src/Cascade/Normalize.ps1, Schema.ps1, TopologyAudit.ps1 | Deterministic issue transformations and topology audit | Versioned canonical schemas and migration-safe round trips |
| src/Cascade/RelationshipReview.ps1 | Read-only parent candidates | Parent/child and lateral dependency graph with scoped explanations and conflict checks |
| src/Cascade/IntelligenceReview.ps1 | Snapshot-based, bounded read-only review | Persistent proposal and field decision service |
| src/Cascade/GenAI.ps1 | ARC-backed model profiles | Structured, bounded, traceable AI inference orchestration |
| ui/intelligence-review-dialog.js | Session-local field decisions and export | Server-persisted reviews, diff and reviewer provenance |
| ui/app.js | Work Intelligence triage; synchronous Git.mil sync | Unified inbox, staging, asynchronous jobs and publication receipts |
| scripts/Start-CascadeWeb.ps1 | Local PowerShell HTTP backend | Retain local GUI host; add approved internal worker API and job-status proxy |
| .github/workflows/cascade-checks.yml | Public-side development CI tests | **Not** a production Git.mil processing runner |

**Migration rule:** Preserve supported PowerShell 5.1, Windows/C#/.NET/browser capabilities and ARC authentication boundaries. Do not require unauthorized software installations, personal endpoints, public cloud processing of internal source, or credential/CAC bypasses.

## 3. System context / trust boundaries

~~~mermaid
flowchart LR
    subgraph GitMil["Authorized Git.mil environment"]
      REPOS["GitLab repos / MRs / issues / CI"]
      HOOK["Approved webhook relay"]
    end
    subgraph Approved["Approved internal Cascade compute boundary"]
      ING["Ingest API + poll adapter"]
      Q["Durable event/outbox queue"]
      WORK["Background evidence and proposal workers"]
      DB[("Approved encrypted local/service DB")]
      AI["Approved GenAI endpoint adapter"]
      REVIEW["Review & publication API"]
      UI["Cascade browser GUI"]
    end
    USER["Authorized reviewer / PO"]
    REPOS --> HOOK --> ING
    REPOS <-->|"scoped API calls"| ING
    ING --> Q --> WORK
    WORK <--> DB
    WORK <-->|"sanitized, policy-filtered evidence"| AI
    WORK --> REVIEW
    REVIEW <--> DB
    UI <--> REVIEW
    USER <--> UI
    REVIEW -->|"explicit authorized changeset only"| REPOS
~~~

**Trust boundaries:**
- The repo, commit message, README, MR description, issue body and AI response are **untrusted input**. They must not set permissions or override hierarchy policy.
- The webhook sender is verified and replay-checked; **Git.mil permission alone is not proof** that a given repository is in Cascade's enabled scope.
- The browser uses authenticated same-origin review APIs. No private tokens, CAC PINs or raw secrets are written to browser local storage.
- The worker uses separately approved read and narrowly scoped write credentials managed by enterprise-approved secret storage. ARC's interactive workstation session is **not** assumed to operate unattended.
- Diagnostic logs redact credentials, internal URLs and source text as required by organizational policy; restricted material never crosses unapproved network boundaries.

## 4. Deployment patterns and runner responsibilities

### 4.1 Target: approved dedicated internal worker

- Host: service-managed Windows or other **authorized** runtime supporting the installed, approved dependencies.
- Components: event receiver or approved relay, durable queue and workers, metadata DB, scheduled reconciler, metrics/health API and review/publish API.
- Network: outbound calls only to allowlisted Git.mil API and authorized model endpoints. Incoming browser/webhook relay access is restricted by authenticated internal ingress.
- Operations: service identity, deployment/version management, recovery, backups, audit logs, readiness and health checks.

### 4.2 Alternative: GitLab self-managed runner

A GitLab runner **polls for eligible CI jobs**. A pipeline triggered by a push/MR may run Cascade discovery against source it is authorized to read. The runner itself does not invent an ongoing daemon, maintain a permanent database, or receive all unrelated project events.

A runner can submit a signed event envelope to the internal queue; a persistent worker handles long-running context assembly, retry, AI analysis and learning. Runners must be registered and configured by authorized Git.mil administrators.

### 4.3 Fallback: centrally scheduled read-only poller

Where webhook registration is unavailable, an authorized scheduled job polls permitted projects and persists cursors. Polling interval is an operational configuration, with rate-limited retries and a backfill window. Production writes remain disabled until explicit authorization.

**Do not** run a public GitHub-hosted job against Git.mil data, forward work tokens to GitHub Actions, or install an unapproved runner on Nautilus.

## 5. Logical component design

~~~mermaid
flowchart TB
    SRC["Source adapters: commits / MRs / issues / CI"]
    ING["Event intake + cursor reconciliation"]
    STORE[("Evidence store and audit provenance")]
    PACK["Evidence packager / secret filter"]
    MATCH["Existing work matcher + deterministic taxonomy"]
    RET["Scoped review-memory retriever"]
    LLM["Authorized AI analysis adapter"]
    VAL["Schema, policy and hierarchy validator"]
    PROP[("Versioned proposal store")]
    RV["Human field review"]
    STAGE["Work Intelligence staged changeset"]
    GATE["Publication authorization gate"]
    PUB["Idempotent Git.mil reconciler"]
    CER["Ceremony briefing / exceptions"]
    SRC-->ING-->STORE-->PACK-->MATCH
    MATCH-->RET-->LLM-->VAL-->PROP
    MATCH-->VAL
    PROP-->RV-->STAGE-->GATE-->PUB
    PROP-->CER
    RV-->|"approved correction only"|RET
    PUB-->|"receipts and fresh evidence"|STORE
~~~

Components and ownership:

| Component | Inputs | Output | Failure behavior |
|---|---|---|---|
| Intake gateway | Verified hook or scheduled source query | Immutable normalized source event | Reject untrusted sender; dead-letter malformed envelope |
| Durable queue | Event key + payload reference | Leased jobs with retry count | Retry exponential backoff; DLQ after bounded attempts |
| Evidence normalizer | GitLab project, SHA, MR, CI, issue references | Versioned evidence package with source URLs/SHAs | Missing permissions -> NEEDS_EVIDENCE, not fabricated completion |
| Existing-item matcher | Scoped issues, IDs, references, evidence key | Unique match / scored candidates / conflict | Ambiguity blocks create until reviewed |
| Hierarchy engine | Taxonomy, scoped catalog, proposed type | Parent and lateral link candidates with evidence | No guessed fallback; reject cycles/duplicate IDs |
| LLM orchestrator | Minimal scoped evidence, retrieved approved memories | Strict JSON proposals with citations | Malformed output -> retry limited, then deterministic fallback/review |
| Proposal validator | Candidate JSON, policies, references | Validated and explainable staged records | Fail closed; no publication |
| Review API | Proposal + revision + reviewer decisions | Field approvals, change notes and learning deltas | Conflict if source or proposed revision changed |
| Publisher | Immutable authorized changeset | GitLab issue changes and audit receipt | Re-read, duplicate check, partial failure reconciliation |
| Ceremony builder | Approved and pending items, time window | Team review agenda | Clearly mark data freshness and open questions |

## 6. End-to-end event sequence

~~~mermaid
sequenceDiagram
    participant G as Git.mil
    participant I as Intake
    participant Q as Durable Queue
    participant W as Worker
    participant A as Approved AI
    participant R as Reviewer
    participant P as Publisher
    G->>I: Push/MR/pipeline hook (or scheduled poll)
    I->>I: Authenticate, scope check, deduplicate
    I->>Q: Commit event + source reference
    I-->>G: Accepted quickly
    Q->>W: Lease event
    W->>G: Retrieve bounded diff, MRs, existing issues, CI
    W->>W: Resolve provenance and existing references
    W->>A: Minimal sanitized evidence + relevant approved memories
    A-->>W: Structured, source-linked proposal
    W->>W: Validate hierarchy, schema and duplication
    W->>R: Review-queue item
    R->>R: Edit / approve fields / select parent
    R->>P: Authorized staged changeset
    P->>G: Re-read current issue and commit changes
    G-->>P: Issue IDs / revision data
    P-->>R: Per-item audit receipt
~~~

**Asynchronous reliability:** intake acknowledges after durable enqueue (not after analysis). A time-consuming model request must not hold the webhook or GUI connection open.

## 7. Queue, idempotency and source revisions

Event identity is scoped to project and provider (e.g., project ID + event UUID, or project ID + event kind + object SHA + revision). The incoming event may be delivered twice or out of order; a deduplicating event inbox is required.

Use the **inbox + outbox pattern**:
1. Verify signature/source/project and write event into durable inbox with unique key.
2. In the same durable transaction, enqueue work or persist an outbox record.
3. Lease pending work with a lease expiry, attempt counter and heartbeat.
4. Process idempotently; attach evidence to immutable source revision.
5. Commit proposal and complete event atomically where practical.
6. On transient failures, backoff and retry; on policy errors, quarantine.
7. Recover expired leases after worker restart; never lose acknowledged events.

Reconciliation jobs compare Git.mil source timelines with stored high-water marks. Use overlap/backfill to accommodate delayed Git events and force-push behavior, then deduplicate by stable Git object identifiers. Repo deletion or changed access is recorded as a scope/permission event, not silently ignored.

## 8. Canonical domain model

- **SourceEvent:** provider, project ID, delivery key, source revision, timestamp, type and receipt.
- **EvidenceArtifact:** immutable source Git SHA/URL, redacted excerpt or approved artifact reference, checksum, provenance type, freshness and approved scope.
- **WorkCluster:** evidence IDs, repository scope, problem statement, probable intent, observed status and stable fingerprint.
- **Proposal:** stable proposal ID, source revision/fingerprint, canonical type, hierarchy and field-specific candidates, confidence, rule-version, memory IDs consulted, state, created timestamp.
- **ProposalField:** proposed/approved value, evidence IDs, reviewer decision, reason and revision.
- **HierarchyLink:** child, proposed parent, kind (parent/related/blocks/duplicate), source ID and verification.
- **EstimateScenario:** points, assisted and unassisted hypothetical ranges, assumptions, uncertainty and source evidence.
- **LearningMemory:** scope, pattern, reviewer-approved correction/rationale, provenance, state and supersession.
- **Changeset:** exact before/after values, target Git.mil project/IID, source fingerprint, intended operation, authorized actor and expiry.
- **PublicationReceipt:** attempted and successful actions, returned IDs, conflicts, audit timestamps and reconciliation state.
- **JobRecord:** ingestion, discovery, normalization, publishing and ceremony jobs with cursor, stage, totals and last error code.

### Hierarchy relationships

- Theme -> Epic -> Feature -> Story -> Task is the canonical tree.
- Bugs may be standalone or linked under an approved policy; do not force an arbitrary parent.
- There is at most one canonical parent per non-root item; lateral relationships may be many-to-many.
- Parent and child must match an approved project/scope relationship rule.
- A parent type must be exactly the expected level unless a versioned taxonomy explicitly allows otherwise.
- Never link to a duplicate canonical ID, cross unauthorized scope or form a cycle.
- Maintain evidence for why a parent was suggested. Preserve user rejection as a negative example but not necessarily as a global rule.

## 9. Proposed database schema (SQLite MVP, subject to authorization)

The following SQL is a **starter schema** for single-host prototyping. It is not a claim of an existing deployed DB or a production migration. Multi-worker service deployments should use a vetted enterprise database or serialized writer pattern. Encrypt storage via approved at-rest controls, tightly scope filesystem/DB access, and establish backups and migrations before production.

~~~sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

CREATE TABLE source_events (
  event_id TEXT PRIMARY KEY,
  project_id INTEGER NOT NULL,
  source_kind TEXT NOT NULL,
  source_ref TEXT NOT NULL,
  source_revision TEXT,
  received_utc TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('queued','leased','done','failed','quarantined')),
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_attempt_utc TEXT,
  UNIQUE(project_id, source_kind, source_ref, source_revision)
);
CREATE INDEX source_events_due ON source_events(status, next_attempt_utc);

CREATE TABLE jobs (
  job_id TEXT PRIMARY KEY,
  type TEXT NOT NULL,
  project_id INTEGER,
  state TEXT NOT NULL,
  stage TEXT NOT NULL,
  observed_count INTEGER NOT NULL DEFAULT 0,
  total_count INTEGER,
  cursor_json TEXT,
  lease_until_utc TEXT,
  started_utc TEXT,
  ended_utc TEXT,
  error_code TEXT
);

CREATE TABLE evidence (
  evidence_id TEXT PRIMARY KEY,
  project_id INTEGER NOT NULL,
  kind TEXT NOT NULL,
  source_url TEXT,
  source_sha TEXT,
  artifact_hash TEXT NOT NULL,
  source_revision TEXT,
  storage_ref TEXT,
  observed_utc TEXT NOT NULL,
  scope_json TEXT NOT NULL
);
CREATE INDEX evidence_project_sha ON evidence(project_id, source_sha);

CREATE TABLE work_clusters (
  cluster_id TEXT PRIMARY KEY,
  project_id INTEGER NOT NULL,
  fingerprint TEXT NOT NULL,
  title TEXT NOT NULL,
  summary TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  observed_state TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  UNIQUE(project_id, fingerprint)
);

CREATE TABLE proposals (
  proposal_id TEXT PRIMARY KEY,
  cluster_id TEXT NOT NULL REFERENCES work_clusters(cluster_id),
  project_id INTEGER NOT NULL,
  proposal_revision INTEGER NOT NULL DEFAULT 1,
  source_revision TEXT NOT NULL,
  status TEXT NOT NULL,
  proposed_type TEXT,
  hierarchy_json TEXT NOT NULL,
  estimate_json TEXT,
  memory_refs_json TEXT NOT NULL,
  schema_version TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);
CREATE INDEX proposals_queue ON proposals(project_id, status, updated_utc);

CREATE TABLE proposal_fields (
  proposal_id TEXT NOT NULL REFERENCES proposals(proposal_id),
  field_name TEXT NOT NULL,
  field_revision INTEGER NOT NULL,
  proposed_json TEXT NOT NULL,
  reviewed_json TEXT,
  decision TEXT NOT NULL CHECK(decision IN
      ('pending','approved','needs_changes','rejected')),
  evidence_json TEXT NOT NULL,
  reviewer_id TEXT,
  reviewed_utc TEXT,
  rationale TEXT,
  PRIMARY KEY(proposal_id, field_name)
);

CREATE TABLE review_memories (
  memory_id TEXT PRIMARY KEY,
  project_id INTEGER NOT NULL,
  team_scope TEXT NOT NULL,
  memory_kind TEXT NOT NULL,
  approved_rule_json TEXT NOT NULL,
  rationale TEXT,
  evidence_json TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('approved','revoked','superseded')),
  approved_by TEXT NOT NULL,
  approved_utc TEXT NOT NULL,
  supersedes_id TEXT,
  policy_version TEXT NOT NULL
);
CREATE INDEX memory_scope ON review_memories(project_id, team_scope, status);

CREATE TABLE changesets (
  changeset_id TEXT PRIMARY KEY,
  proposal_id TEXT NOT NULL REFERENCES proposals(proposal_id),
  project_id INTEGER NOT NULL,
  issue_iid INTEGER,
  before_json TEXT,
  after_json TEXT NOT NULL,
  source_revision TEXT NOT NULL,
  state TEXT NOT NULL,
  authorized_by TEXT,
  authorized_utc TEXT,
  expires_utc TEXT,
  idempotency_key TEXT NOT NULL UNIQUE
);

CREATE TABLE publication_receipts (
  receipt_id TEXT PRIMARY KEY,
  changeset_id TEXT NOT NULL REFERENCES changesets(changeset_id),
  attempt_no INTEGER NOT NULL,
  status TEXT NOT NULL,
  gitlab_project_id INTEGER NOT NULL,
  gitlab_issue_iid INTEGER,
  result_json TEXT,
  error_code TEXT,
  occurred_utc TEXT NOT NULL,
  UNIQUE(changeset_id, attempt_no)
);

CREATE TABLE sync_cursors (
  project_id INTEGER NOT NULL,
  source_kind TEXT NOT NULL,
  high_watermark TEXT NOT NULL,
  last_success_utc TEXT NOT NULL,
  PRIMARY KEY(project_id, source_kind)
);
~~~

Do not store API tokens, raw CAC material, unbounded repository contents or unfiltered protected text in this schema. Detailed evidence artifacts may be references to an approved, separate encrypted store with retention and deletion policies.

## 10. API contract (target, versioned)

All paths here are **proposed**; they must not be advertised as working in the existing GUI. Response shape is JSON except optional job-status streaming. Authorize every route by identity, project, scope, method and intent.

| Method and route | Purpose | Minimum permission |
|---|---|---|
| POST /api/v1/events/gitlab | Trusted internal webhook relay; queues normalized event | Verified service sender |
| POST /api/v1/jobs/sync | Launch asynchronous scoped ingestion | Project Reader |
| GET /api/v1/jobs/{jobId} | Real stage/count/status, no invented percent | Project Reader |
| POST /api/v1/jobs/{jobId}/cancel | Request cancellation at safe checkpoint | Operator |
| GET /api/v1/proposals?state= | Browse scoped autonomous inbox | Project Reviewer |
| GET /api/v1/proposals/{id} | Evidence, fields, memory influence, dependencies | Project Reviewer |
| PATCH /api/v1/proposals/{id}/fields/{field} | Edit / approve / reject using revision precondition | Assigned Reviewer |
| POST /api/v1/proposals/{id}/reconcile | Create preview changeset against fresh Git.mil snapshot | Project Reviewer |
| GET /api/v1/changesets/{id} | Before/after and policy checks | Project Reviewer |
| POST /api/v1/changesets/{id}/authorize | Explicit authorization; actor/scope/expiry bound | Authorized Publisher |
| POST /api/v1/changesets/{id}/publish | Apply approved changeset with idempotency key | Publisher service |
| GET /api/v1/changesets/{id}/receipts | Audit, conflicts and partial-failure details | Project Reviewer |
| GET /api/v1/memories | Approved memories filtered by permitted scope | Project Reviewer |
| POST /api/v1/memories/{id}/revoke | Revoke learning with reason and audit trail | Memory Curator |
| GET /api/v1/ceremonies/{id}/agenda | Snapshot agenda and freshness notice | Team Reviewer |

### 10.1 Start sync

~~~http
POST /api/v1/jobs/sync
Content-Type: application/json
X-Cascade-Intent: sync-authorized-projects

{
  "project_ids": [16629],
  "lookback_days": 30,
  "dry_run": true
}
~~~

~~~http
HTTP/1.1 202 Accepted
Content-Type: application/json

{
  "job_id": "job-synthetic-123",
  "status": "queued",
  "status_url": "/api/v1/jobs/job-synthetic-123"
}
~~~

### 10.2 Poll job

~~~json
{
  "job_id": "job-synthetic-123",
  "state": "running",
  "stage": "fetching_merge_requests",
  "observed_count": 24,
  "total_count": null,
  "stage_description": "Reading merge requests from authorized projects",
  "last_error": null,
  "can_cancel": true
}
~~~

**Progress contract:** when a denominator is unknown, show an indeterminate spinner and real item counts; do not display arbitrary percentages. A successful HTTP response is not proof that every project was synchronized; expose per-project errors and freshness.

### 10.3 Field-level review

~~~http
PATCH /api/v1/proposals/proposal-456/fields/parent_id
Content-Type: application/json
If-Match: "proposal-rev-4"
X-Cascade-Intent: review-field

{
  "decision": "approved",
  "reviewed_value": "FEATURE-batch-ingestion",
  "rationale": "Reviewer confirmed parent in project catalog"
}
~~~

~~~json
{
  "proposal_id": "proposal-456",
  "proposal_revision": 5,
  "field": "parent_id",
  "decision": "approved",
  "staged_only": true,
  "gitlab_modified": false
}
~~~

A mismatch in the Cascade proposal revision returns 409 with the current revision and no mutation. Review authorization is checked separately from publication authorization.

### 10.4 Publish semantics

The authorized changeset has immutable proposed before/after values, source revision, existing issue key if any, exact target project, approving identity, finite authorization expiry and an idempotency key. The publisher re-reads GitLab immediately before action; if the source changed, it records CONFLICT and asks for reconciliation.

**GitLab issue APIs may not guarantee an atomic compare-and-swap for every field.** The application-level re-read narrows races but does not eliminate them. Treat partial success as a first-class state, reconcile receipts, avoid duplicate creation with a stable Cascade evidence key and apply safely retriable operations.

## 11. Source-to-proposal analysis contract

### 11.1 Input evidence envelope

~~~json
{
  "schema_version": "cascade.evidence.v1",
  "project_id": 16629,
  "repository": "synthetic/batch-ingestion",
  "event_type": "merge_request_updated",
  "source_revision": "synthetic-sha",
  "commit_refs": ["synthetic-sha"],
  "merge_request_iid": 12,
  "changed_paths": ["src/retry.ps1", "tests/retry.tests.ps1"],
  "ci_status": "unknown",
  "existing_issue_refs": [],
  "scope_label": "team::Data-Crucible",
  "source_provenance": [
    {"kind": "commit", "ref": "synthetic-sha", "verified": false}
  ]
}
~~~

### 11.2 Required model proposal output

LLM suggestions must be validated against a strict schema and evidence references. Example fields:

~~~json
{
  "schema_version": "cascade.proposal.v1",
  "source_revision": "synthetic-sha",
  "proposed_type": "STORY",
  "title": "Introduce retry-safe batch ingestion",
  "summary": "Add restart-safe handling for retryable batches.",
  "acceptance_criteria": [
    "Retrying an interrupted batch does not create duplicate records."
  ],
  "parent_candidates": [
    {
      "stable_id": "FEATURE-batch-ingestion",
      "relation": "parent",
      "confidence_band": "moderate",
      "evidence_refs": ["synthetic-sha"]
    }
  ],
  "story_points": {
    "value": 5,
    "confidence_band": "low",
    "rationale": "Test and recovery semantics add uncertainty."
  },
  "effort_scenarios": {
    "unassisted": {
      "minimum_workdays": 3,
      "maximum_workdays": 5
    },
    "ai_assisted": {
      "minimum_workdays": 1,
      "maximum_workdays": 3
    },
    "label": "hypothetical estimates, not actual effort"
  },
  "delivery_evidence": {
    "implemented": "observed",
    "tests_passed": "unknown",
    "merged": "unknown",
    "accepted": "unknown"
  },
  "unresolved_questions": [
    "Do integration tests verify recovery after a partial database commit?"
  ]
}
~~~

Never accept a model-supplied parent if that ID is absent, non-unique, the wrong type, beyond permissions, or would cause a cycle. Preserve evidence missingness rather than hallucinating completion, points or approvals.

## 12. Intelligence memory and retrieval architecture

~~~mermaid
flowchart LR
    USER["Reviewer correction"] --> CHECK["Approved review decision"]
    CHECK --> MEM[("Versioned project-scoped memories")]
    TASK["New evidence package"] --> FILTER["Authorization + project/team scope"]
    MEM --> FILTER
    FILTER --> SCORE["Exact rules / lexical matches / optional approved vector lookup"]
    SCORE --> BUDGET["Bounded memory context"]
    TASK --> BUDGET
    BUDGET --> LLM["Authorized AI endpoint"]
    LLM --> VERIFY["Policy validator + review"]
    VERIFY --> USER
~~~

Memory classes, in precedence order:

1. **Hard taxonomy and policy** — approved, version-controlled rules (not learned from arbitrary examples).
2. **Scoped exact mappings** — approved project ownership, known repo/path/component -> hierarchy mapping.
3. **Reviewed demonstrations** — previous approved before/after field edits with short rationale and current provenance.
4. **Negative examples** — explicitly rejected parents/types or phrasing, scoped to the supported context.
5. **Episodic context** — relevant prior decisions, time-bounded with supersession and source revision.

Memory promotion requirements: explicit reviewer acceptance, valid scope, source evidence, no restricted content leakage, unique memory fingerprint, version/approval provenance and revocation control. An AI suggestion that went unedited is **not automatically a new general rule**.

Memory retrieval algorithm:

- Apply authorized project/team filters **before** ranking candidates.
- Retrieve hard rules and verified exact ID matches first.
- Rank approved examples by component/path match, contextual terms, recency and repeat confirmation.
- Include a limited, configurable top-K with memory IDs in the proposal audit.
- Enforce a token budget and truncate low-ranked examples, not mandatory policy.
- If the model's context is insufficient, analyze evidence in smaller chunks and merge validated summaries with source refs.
- Exclude revoked, superseded or stale memories; allow an operator to invalidate an outdated component mapping.
- Track correction/acceptance rates on a held-out synthetic or authorized corpus; do not turn per-user activity into individual performance scores.

**Recommended starter memory storage:** indexed tables in approved local SQLite for a single service; enterprise managed relational storage as concurrency/availability needs grow. Optional embeddings are not a prerequisite; exact ID, title and component/path matching should be first-class.

## 13. LLM orchestration and context budgets

The existing GenAI client sends chat-completion requests with a **max_tokens output parameter**. That does not establish the model's supported **total context window**. Probe approved endpoints for model IDs, limits, latency, max request size, streaming, timeouts and structured-output behavior.

Suggested initial per-request **configurable** budget (not a guarantee of supported model limits):

| Content | Target token allowance |
|---|---:|
| Stable policy/system/taxonomy | 2,000–4,000 |
| Project task + canonical hierarchy catalog | 2,000–5,000 |
| Retrieved reviewer-approved memory | 4,000–8,000 |
| Chunked, redacted source/diff evidence | 8,000–24,000 |
| Reserved structured output | 2,000–4,000 |

Separate **input context allowance** from **generation limit**. Never set a blanket giant output limit to compensate for missing retrieval logic.

Recommended calls:
1. Deterministic diff normalization and secret filtering.
2. Existing work lookup, labels, canonical IDs and memory retrieval.
3. Optional targeted code comprehension per component; cache by SHA + policy/memory version.
4. Evidence-linked structured Agile proposal generation.
5. Independent deterministic and schema validation.
6. Reviewer feedback capture; approved corrections become retrievable memory only after explicit confirmation.

Include bounded retries, provider failover only where explicitly allowed, rate limits, per-project request budgets, timeout/circuit breaker, token consumption metrics and zero credential content in prompts/logs. Treat prompts embedded in code and issue bodies as untrusted data.

## 14. Estimation: points, two scenarios, and historical truth

**Story points:** team-relative complexity and uncertainty, calibrated using approved reference stories. They are not hours and should not be converted to hours by a universal constant.

**Hypothetical effort scenarios:**
- **Human-only:** plausible range assuming developer does not use generative implementation assistance, with architecture, integration, testing and code review included.
- **AI-assisted:** plausible range assuming allowed tools and a stated assistance pattern, including generated-code review, troubleshooting and testing.
- Both are scenario-planning estimates—not measured time spent, an assertion about the engineer's actual tool use or individual productivity ranking.
- Provide assumption set, confidence, exclusions and uncertainty drivers for both.
- If evidence is too sparse, return **unresolved** rather than a precise-looking number.
- Never derive work hours from commit timestamps, number of changed lines, author or number of merges.

If the team later chooses to use voluntarily recorded authorized planning data, calibrate ranges with aggregated reference stories and avoid named employee performance comparisons.

## 15. User interface information architecture

~~~mermaid
flowchart TD
    A["Autonomous Inbox: newly observed work"] --> B["Intelligence Workbench: field-level review"]
    B --> C["Work Intelligence: duplicate and source reconciliation"]
    C --> D{"All checks pass?"}
    D -->|No| E["Return to reviewer with specific conflicts"]
    E --> B
    D -->|Yes| F["Staged before/after changeset"]
    F --> G["Explicit authorized publication"]
    G --> H["Git.mil receipt and audit"]
    B --> M["Approved learning memories"]
    A --> Q["Ceremony exception queue"]
    B --> Q
    H --> Q
~~~

**Autonomous Inbox:** cards for new clusters, missing evidence, unreviewed retrospective work, failed runners and stale imports. Show the exact source commit/MR IDs. Filter by source, team, review state and ceremony.

**Intelligence Workbench:** selecting a row opens the current accessible modal, with pending/approved/needs-changes, edit, validation, memory influence and relationship candidates. Persist field approvals server-side using proposal revision; editing a dependent field invalidates affected approvals.

**Work Intelligence:** receives approved proposals as a *staged transaction*, not as exported JSON the user must import. Shows before/after issue fields, match confidence, new vs existing work, planned links, duplicates and concrete validation reasons.

**Publish:** separate screen or privileged action with project scope, exact changeset, current Git.mil revision, actor, expiry and confirmation. A published receipt exposes successes, conflicts, failures and potential rollback instructions.

**Ceremony Queue:** groups by team/project/sprint and meeting date. Flag retroactively reconstructed items, missing parent, estimate disagreement, acceptance unknown, stale or policy conflicts. Do not label unverified work "completed."

### 15.1 Sync progress

**Already implemented in the branch:** accessible indeterminate spinner around current synchronous Git.mil call.

**Target asynchronous contract:** start job -> receive ID -> poll status or authorized SSE -> render true stages/counts and per-project errors. Suggested statuses: QUEUED, AUTHORIZING, FETCHING_PROJECTS, FETCHING_ISSUES, FETCHING_MRS, FETCHING_PIPELINES, PERSISTING, ANALYZING, COMPLETED, PARTIAL, FAILED, CANCELLED.

Progress count is only displayed if measured. Cancel/retry is safe at worker checkpoints and must not leave a partially applied changeset marked complete.

## 16. Workflow state machine

~~~mermaid
stateDiagram-v2
    [*] --> OBSERVED
    OBSERVED --> EVIDENCE_READY
    OBSERVED --> NEEDS_EVIDENCE
    EVIDENCE_READY --> PROPOSED
    PROPOSED --> FIELD_REVIEW
    FIELD_REVIEW --> RECONCILED
    FIELD_REVIEW --> REJECTED
    FIELD_REVIEW --> NEEDS_EVIDENCE
    RECONCILED --> APPROVED_FOR_PUBLISH
    RECONCILED --> CONFLICT
    APPROVED_FOR_PUBLISH --> PUBLISHED
    APPROVED_FOR_PUBLISH --> STALE
    APPROVED_FOR_PUBLISH --> FAILED_RETRYABLE
    FAILED_RETRYABLE --> APPROVED_FOR_PUBLISH
    CONFLICT --> FIELD_REVIEW
    STALE --> FIELD_REVIEW
    PUBLISHED --> [*]
~~~

Rules:
- Every state change is an audited event with a reason and correlation ID.
- Only reviewer-owned or policy-eligible fields may transition to APPROVED_FOR_PUBLISH.
- Source revision mismatch invalidates dependent reviews and moves the changeset to STALE/CONFLICT.
- PUBLISHED requires successful Git.mil receipt; a human review click is insufficient.
- A job crash/retry cannot silently reopen a rejected item or apply an expired authorization.
- Closure/acceptance needs separate acceptance evidence and approved policy; not inferred from git merge alone.

## 17. Threat model and deployment safeguards

Threats to address:
- **Webhook spoofing/replay:** verify secret/header/relay identity and store replay nonce/delivery key.
- **Prompt injection:** comments, README, logs and issue text can contain instructions; strip control framing and treat as quoted evidence.
- **Secret exfiltration:** static filtering, path exclusions, size bounds, prompt inspection and authorized endpoint allowlist.
- **Over-broad repo reads:** explicit project/label allowlist and service ACL evaluation before fetching detailed content.
- **Unsafe write:** separate write principal, explicit changeset permission, idempotency, source reread and dry-run.
- **Partial apply:** record each GitLab operation and receipt; reconcile duplicates and link inconsistencies after retry.
- **Stale memory:** revocable/supersedable memories, source provenance and retention.
- **Reviewer spoofing:** enterprise auth, role mapping, audit records and signed service-to-service requests where required.
- **Denial of service:** payload size limits, maximum diff/code sample size, queues with backpressure, bounded model concurrency, circuit breakers and rate limits.
- **Data loss:** transactional event inbox, backups, WAL recovery, schema migration plans and tested restore procedure.

Before any real Git.mil operations, obtain system owner approval for runner registration, approved hosting, token storage, GenAI endpoints, data classification/handling, network egress, security monitoring and retention rules.

## 18. Operations, observability and recovery

Measure:
- Event intake latency and duplicate suppression rate.
- Cursor freshness by project, event backlog size, processing latency and retry/DLQ count.
- Evidence coverage (MR/commit/test refs), proposal confidence distribution and unresolved parent count.
- Memory hit rate, human correction rate and duplicate issue avoidance measured at project level.
- Publication conflict, stale source, partial success and retry counts.
- Token/request volume, inference latency, failure rates and budget caps.
- GUI sync stages, perceived waiting, responsiveness and fallback behavior.

All logs are redacted and scoped. Each event/job/proposal/changeset gets a correlation ID propagated from webhook to publication receipt.

Recovery runbook:
1. Turn off publication feature flag; keep read-only ingest if authorized.
2. Identify stuck events and expired leases; requeue idempotently.
3. Check per-project cursor and replay overlap window without skipping events.
4. Diagnose model endpoint failures; switch to deterministic read-only proposals, not unauthorized providers.
5. Rebuild a proposal from immutable evidence IDs and recorded rule/memory versions.
6. For partial publication, inspect receipts and current Git.mil state; do not blind replay.
7. Restore metadata DB from encrypted backup to an isolated instance; verify consistency and cursor replay before reconnecting.
8. Resume publication only after a reviewer/authorized operator approves recovery.

## 19. Testing and acceptance criteria

| Layer | Tests | Acceptance criteria |
|---|---|---|
| Ingestion | duplicate events, out-of-order commits, forced pushes, missing scopes, delayed hooks | no lost acknowledged event; deterministic cursor recovery |
| Evidence | fixture MRs/commits, secret filtering, oversized files, no tests, cross-repo ref | evidence provenance stable; unauthorized content excluded |
| Hierarchy | valid chain, wrong type, duplicate ID, cycle, out-of-scope parent, root Theme | no invalid approved/published relationship |
| AI outputs | invalid JSON, hallucinated IDs, injected code instructions, huge context, missing refs | schema fail closed; no AI-controlled privilege elevation |
| Work reconciliation | existing issue matches, conflicting fuzzy hits, repeated runs | no duplicate issue creation after retried publication |
| Review | per-field approval, edit invalidation, stale revision, keyboard and accessibility | approvals persist, revision precondition enforced |
| Memory | correct scoped retrieval, revocation, outdated mapping, negative examples | no cross-project leak; revoked memories excluded |
| Estimates | no evidence, massive diff, documentation-only change, retrospective capture | no real hours inferred or fabricated acceptance |
| Publishing | sandbox create/update/link, expired auth, transient failure, partial success | audit receipt and safe re-read/reconcile |
| UI | spinner during pending sync, error cleanup, reduced-motion, truthful counts | no frozen progress or invented percentages |
| Recovery | kill worker, corrupt queue item, restart, DB backup restore | no unbounded duplicate execution, controlled recovery |

**Release sequence and gates:**
1. Fixture-only event intake and persistence; CI verified.
2. Deterministic evidence graph and merge-request matching; no live writes.
3. Model proposal adapter with strict schema, known-limit checks and approved endpoint.
4. Scoped reviewer memory, APIs, persistent UI decisions and provenance.
5. Unified Work Intelligence staging, concurrency handling and diff verification.
6. Sandbox-only publication with receipts, conflict/retry tests and approved credentials.
7. Operator-approved internal worker pilot, monitoring and ceremony agenda.
8. Only after approvals, bounded production publication; progressive rollout by explicitly selected project/team.

## 20. Open architecture decisions (must be resolved, not guessed)

| Decision | Options / information needed | Recommended default |
|---|---|---|
| Worker host | Approved internal Windows service / GitLab runner / approved server | Dedicated approved internal worker |
| Event source | Webhook relay permissions versus scheduled Git.mil polling | Both, with polling as reconciliation |
| Persistence | SQLite locally versus enterprise relational DB | SQLite in single-worker pilot if approved |
| Authentication | Service identity / approved secret broker / ARC integration | Approved least-privilege service credential |
| GenAI provider | Authorization and model input/output limits for code data | Use existing approved GenAi adapter only after verification |
| Hierarchy ownership | Cross-project parents allowed? Team/project taxonomy? | Single scope by default, exceptions by policy |
| AI estimates | Team-specific reference stories; assisted tool assumptions | Ranges with high uncertainty until calibrated |
| Publication | Review-only, partial field autopublish, or controlled full publish | Explicit human-approved sandbox writes first |
| Review memory | Which corrections may be promoted and by whom | Reviewer-approved, scoped, auditable only |
| Ceremony | GitLab iterations/milestones or linked calendar source | Configure an explicit team ceremony source |
| Retention | Repo evidence, model payloads, reviewer decisions, receipts | Approved policy and minimum necessary storage |

## 21. Definition of "fully autonomous" for Cascade

Cascade may **autonomously** discover evidence, create analysis jobs, propose new/retroactive work items, maintain draft documentation, rank hierarchy suggestions, generate bounded hypothetical effort estimates, request missing acceptance data and prepare ceremony agendas.

Cascade must **not** autonomously:
- report hypothetical effort as actual hours,
- determine individual developer productivity from git activity,
- change production Git.mil projects or user access outside an explicit authorized scope,
- silently invent parent/child links or accepted status,
- treat AI-generated memories as reviewer-approved organizational policy,
- export internal Navy code/metadata to public runners or unapproved endpoints, or
- skip institutional authorization for worker deployment.

The goal is a Product Owner assistant that handles **administrative preparation and traceable draft decisions**, while accountable people retain authority over requirements, acceptance and controlled publication.

---

**Repository implementation note:** This technical blueprint is intentionally separate from the existing architecture overview. A diagram, schema, or API route here is not evidence that the corresponding implementation exists. Implementation milestones must link to source code, tests and verification receipts before being labeled delivered.
