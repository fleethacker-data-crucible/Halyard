# Work-item model

The standard Cascade checks every issue against, on GitHub and GitLab alike. The code that enforces it
lives in `src/Cascade.Core/Schema` (required keys, sizing policy, definition-of-ready checks) and
`src/Cascade.Core/Analysis` (topology audit); the codes it reports are listed in the Review page.

**Scope:** Theme → Epic → Feature → Story (tasks inside the Story) · milestone association · PfW/DWI work definitions.

---

## 1. Topology (locked)

```text
THEME          portfolio lane (ongoing container)
  └── EPIC       outcome-sized initiative
        └── FEATURE   shippable capability
              └── STORY    sprint-scale increment (≤ one 14-day sprint)
                    └── TASKS  checklist inside the Story body (default)

MILESTONE      time / release gate — associated, not a parent
SPRINT         iteration timebox — primarily on Stories
```

### Rules

| Rule | Detail |
|------|--------|
| Parent chain | Story → Feature → Epic → Theme |
| Theme | Ongoing; never a date-bound project |
| Time-boxes | Epics (and milestones), not Themes |
| Tasks | Embedded checklist on Story by default; not separate GitLab issues unless tooling requires it |
| Milestone | Optional link on Epic, Feature, and/or Story |
| Points | Stories only (Fibonacci: 1, 2, 3, 5, 8, 13) |
| Effort | Features only: Small \| Medium \| Large |
| Writes | Cascade sandbox first; CBR only after human accept |

### Title pattern

`{TYPE} :: {Short name}`

Examples: `THEME :: Platform Engineering`, `EPIC :: Production schema parity`, `FEATURE :: RDS IAM auth`, `STORY :: Map IAM roles to Postgres roles`

### Id pattern

`{TYPE}-{slug}` — e.g. `STORY-map-iam-postgres-roles`

---

## 2. Cascade metadata block

Every Cascade-managed issue should start with a `cascade-meta` fence (or HTML comment equivalent). Reviewers should see these fields.

```text
~~~cascade-meta
type: THEME|EPIC|FEATURE|STORY
id: TYPE-slug
theme: THEME-...
parent: EPIC-... or FEATURE-...
state: open|closed
status: active|sunset          # Theme mainly
area: data-crucible
effort: Small|Medium|Large     # Feature
points: 1|2|3|5|8|13           # Story
sprint: 2026-S12               # Story
milestone: FY26-Q1-Release     # optional; Epic/Feature/Story
source_project: 16629          # when mirrored from CBR
source_iid: 271
~~~
```

### Required meta by type

| Type | Required meta |
|------|----------------|
| **THEME** | type, id, state, status, area |
| **EPIC** | type, id, theme, state, area |
| **FEATURE** | type, id, parent (Epic id), theme, state, area, effort |
| **STORY** | type, id, parent (Feature id), theme, state, area, points, sprint |

**Milestone** is optional at Epic, Feature, or Story. Use the same milestone name when the work ships in the same release train.

---

## 3. THEME

### Purpose

Portfolio lane. Groups Epics. Does not end when a project ends.

### Metadata example

```text
~~~cascade-meta
type: THEME
id: THEME-platform-engineering
theme: THEME-platform-engineering
parent:
state: open
status: active
area: data-crucible
~~~
```

### Body should contain

| Section | Content |
|---------|---------|
| **Purpose** | Why this lane exists (1–3 sentences) |
| **In scope** | What belongs here |
| **Out of scope** | What does not |
| **Owner** | Role or named accountable |
| **Child Epics** | List or links as they appear |
| **Related milestones** | Names only (optional) |

### Should not contain

Funding tables, story points, sprint dates, task checklists, detailed delivery plans.

### Artifacts

| Artifact | Optional? |
|----------|-----------|
| Short strategy note / charter (link) | Optional |
| Portfolio dashboard filter | Derived from labels |
| Child Epic list | Living |

### Example body (abridged)

```markdown
## Purpose
Platform engineering for Data Crucible: shared data platform services,
environments, and operational automation used by multiple product Epics.

## In scope
- Shared RDS / Postgres patterns
- Qlik operational platform concerns
- Cross-cutting IAM patterns for data services

## Out of scope
- One-off report builds (those are product Features/Stories under other Themes)
- Enterprise network tickets owned by other orgs

## Owner
Data Crucible tech lead (or named PO)

## Child Epics
- (linked as created)
```

---

## 4. EPIC

### Purpose

Outcome-sized initiative under one Theme. Can span multiple sprints/Features.

### Metadata example

```text
~~~cascade-meta
type: EPIC
id: EPIC-prod-test-schema-parity
theme: THEME-platform-engineering
parent:
state: open
area: data-crucible
milestone: FY26-Data-Platform-Stabilization
~~~
```

### Body should contain

| Section | Content |
|---------|---------|
| **Outcome** | Observable result when the Epic is done |
| **Success picture** | 3–7 concrete bullets (what “good” looks like) |
| **Theme** | Which lane (also in meta) |
| **Owner / accountable** | Role or person |
| **Child Features** | List or links |
| **Milestone** | If the Epic targets a release gate |
| **Risks / dependencies** | Optional but useful |

### Should not contain

Story-point totals as a plan of record, day-by-day task lists, sprint assignments (those live on Stories).

### Artifacts

| Artifact | Notes |
|----------|-------|
| Outcome statement | Required in body |
| Architecture / ADR link | When design is non-obvious |
| Dependency list (systems, teams) | Useful |
| Milestone or release note link | When associated |

### Example body (abridged)

```markdown
## Outcome
AWS Test PostgreSQL RDS schema matches Production for the schemas
Data Crucible teams use for ETL and app testing.

## Success picture
- [ ] Schema-only sync path documented and repeatable
- [ ] Service accounts exist on Test and Prod as required
- [ ] Sample data strategy agreed (no prod PII leakage)
- [ ] Tech lead sign-off on parity checklist

## Owner
Platform engineering lead

## Child Features
- FEATURE :: Schema sync tooling
- FEATURE :: Service account baseline

## Milestone
FY26-Data-Platform-Stabilization

## Dependencies
- IAM / DIO for role provisioning
- Access to Prod metadata (not prod row data)
```

---

## 5. FEATURE

### Purpose

Shippable capability under an Epic. Sized with **effort** (not Fibonacci).

### Metadata example

```text
~~~cascade-meta
type: FEATURE
id: FEATURE-rds-iam-auth
theme: THEME-platform-engineering
parent: EPIC-prod-test-schema-parity
state: open
area: data-crucible
effort: Medium
milestone: FY26-Data-Platform-Stabilization
~~~
```

### Body should contain

| Section | Content |
|---------|---------|
| **Description** | What capability is delivered |
| **Parent Epic** | Name/id |
| **Definition of done** | Checklist for the Feature as a whole |
| **Effort** | Small / Medium / Large (+ rationale if useful) |
| **Child Stories** | Sprint slices |
| **Milestone** | Optional |

### Effort guide

| Effort | Meaning |
|--------|---------|
| **Small** | About one sprint of focused work or less |
| **Medium** | About two–three sprints / multiple Stories |
| **Large** | Multi-sprint; expect split pressure; watch for hidden Epic |

### Artifacts

| Artifact | Notes |
|----------|-------|
| DoD checklist | Required |
| Design sketch / interface contract | When integration-heavy |
| Test plan pointer | Useful for Medium/Large |
| Child Story list | Living |

### Example body (abridged)

```markdown
## Description
Enable SageMaker-associated IAM principals to authenticate to PostgreSQL
RDS using IAM, reducing static password use for Data Crucible tooling.

## Parent
EPIC-prod-test-schema-parity

## Definition of done
- [ ] Auth path proven in TEST
- [ ] Role mapping documented
- [ ] Fallback password path documented for break-glass
- [ ] Security stakeholder informed

## Effort
Medium — research + mapping + verification Stories

## Child Stories
- STORY :: Research SageMaker RDS Auth
- STORY :: Associate IAM Roles to Postgres Roles
- STORY :: Configure IAM Postgres Roles
```

---

## 6. STORY

### Purpose

Sprint-scale, testable increment under a Feature. **Points + sprint** required for velocity metrics.

### Metadata example

```text
~~~cascade-meta
type: STORY
id: STORY-associate-iam-postgres-roles
theme: THEME-platform-engineering
parent: FEATURE-rds-iam-auth
state: open
area: data-crucible
points: 5
sprint: 2026-S12
milestone: FY26-Data-Platform-Stabilization
~~~
```

### Body should contain

| Section | Content |
|---------|---------|
| **User need** | Who / what / why (user story or equivalent) |
| **Acceptance criteria** | Testable bullets (Given/When/Then or checklist) |
| **Tasks** | Checklist of execution steps (this is where Tasks live) |
| **Parent Feature** | Name/id |
| **Notes / constraints** | Env, security, outages |

### Points guide (14-day sprint)

| Points | Rough meaning |
|--------|----------------|
| 1–2 | Small, clear, low risk |
| 3–5 | Typical Story |
| 8 | Large for one sprint; scrutinize |
| 13 | Usually split |

### Tasks (inside Story)

```markdown
## Tasks
- [ ] Confirm IAM role names with DIO
- [ ] Map roles in TEST Postgres
- [ ] Verify connect from SageMaker test notebook
- [ ] Document mapping in team wiki
```

Do **not** put story points on Tasks.

### Artifacts

| Artifact | Notes |
|----------|-------|
| AC list | Required |
| Task checklist | Required (even if short) |
| Link to MR / pipeline / ticket | When work starts |
| Screenshot or test evidence | At close |
| Wiki / runbook update | When ops-facing |

### Example body (abridged)

```markdown
## User need
As a Data Crucible engineer, I need my SageMaker execution role mapped to
a Postgres role in TEST so I can run governed queries without a shared password.

## Acceptance criteria
- [ ] Documented mapping of at least one SageMaker role to one Postgres role in TEST
- [ ] Successful connection test recorded (date, role, method)
- [ ] Break-glass password path still documented
- [ ] No production change in this Story

## Tasks
- [ ] Obtain role ARNs from current SageMaker domain
- [ ] Apply grants in TEST
- [ ] Connection test from notebook
- [ ] Update runbook section "RDS IAM auth"

## Parent
FEATURE-rds-iam-auth
```

---

## 7. MILESTONE (association model)

Milestone is **not** a hierarchy level. It is a **named time/release gate**.

| Level | When to associate |
|-------|-------------------|
| **Epic** | Epic is meant to finish in a release/train |
| **Feature** | Feature ships in that gate even if Epic continues |
| **Story** | Story is required for the gate (common for near-term release) |

Use **one consistent milestone name** string in meta and body (example: `FY26-Q2-Data-Crucible-Release`).

GitLab Milestone object may also be linked in the project UI; Cascade meta keeps the name portable.

---

## 8. Labels (minimum)

| Label | Use |
|-------|-----|
| `area::data-crucible` | Product area filter |
| `type::theme` / `type::epic` / `type::feature` / `type::story` | Optional explicit type label |
| `theme::...` | Optional mirror of theme id |
| `cascade` | Created or managed by Cascade |
| `cascade-synthetic` | Synthetic item; the only kind teardown may delete |
| `cascade-run::<id>` | Which seed run created the item |
| `refine::sandbox-review` | Human review queue |
| `refine::ai-accepted` | From bulk ACCEPT path |
| `refine::human-needed` | Meeting required |

Priority labels (`priority::now|next|later|critical`) remain valid business labels; they do not replace hierarchy.

---

## 9. Sandbox review checklist

For each `REVIEW ::` / `REVIEW-MEETING ::` item:

1. **Type** — Theme / Epic / Feature / Story correct? If wrong, note the correct type.  
2. **Parent** — Is parent id present or implied? Orphan Stories need a Feature (create or name it).  
3. **Theme** — Does it belong under the right Theme lane?  
4. **Body** — Outcome/AC/Tasks present at the right level?  
5. **Split** — Is this larger than one sprint (Story) or one Feature effort? Mark split.  
6. **Milestone** — Needed? If yes, same name as siblings.  
7. **CBR link** — `source_iid` points at the real item; compare intent.  
8. **Decision** — Approve structure / Request edits / Reject (keep on CBR as-is) / Meeting.

Sandbox is a **proposal board**. CBR stays system of record until a deliberate promote.

---

## 10. Anti-patterns

| Anti-pattern | Do instead |
|--------------|------------|
| Theme with end date | Time-box an Epic under the Theme |
| Epic with story points only, no outcome | Write outcome + success picture |
| Feature with Fibonacci points | Use effort Small/Medium/Large |
| Story without AC | Add testable AC before Accept |
| Tasks as separate backlog issues by default | Checklist on Story |
| Milestone as parent of Features | Associate milestone; parent stays Epic/Feature |
| Bulk AI table applied straight to CBR | Sandbox review first |

---

## 11. Quick reference card

| Level | Title | Sizing | Parent | Body core | Artifacts |
|-------|-------|--------|--------|-----------|-----------|
| Theme | `THEME ::` | Ongoing | — | Purpose, in/out, owner | Charter link optional |
| Epic | `EPIC ::` | Outcome | Theme | Outcome, success picture | ADR/deps optional |
| Feature | `FEATURE ::` | S/M/L effort | Epic | Description, DoD | Design/test plan optional |
| Story | `STORY ::` | Points + sprint | Feature | Need, AC, **Tasks** | MR, evidence at close |
| Task | checklist line | — | Story | Done/not done | — |
| Milestone | name | Date/gate | — | Associated on E/F/S | Release notes optional |

---


---

## 12. Work definitions: PfW and DWI

| Term | Meaning |
|------|---------|
| **PfW** | Proposal for Work: the default, lighter Feature-level work definition |
| **DWI** | Defined Work Instruction: a fuller, researched package for the same Feature when the work is multi-step, research-heavy or crosses security/data boundaries |
| **Lightweight** | A small, standing Feature that skips PfW/DWI with a one-line rationale |

Set `artifact_level: Proposal for Work | Defined Work Instruction | Lightweight` on the Feature. Either embed a
`## Proposal for Work` (or `## Defined Work Instruction`) section in the Feature body, or link a separate
`PFW ::` / `DWI ::` issue through `pfw_id` / `dwi_id`. Templates are in [`templates/`](templates/).

**PfW minimum:** problem/outcome, approach, scope in/out, done-when, dependencies, risks, 2–5 suggested Stories.
**DWI adds:** background/research, interface or data contracts, security notes, verification approach, rollout/backout, open questions.

## 13. Sizing placeholders

When a size is unknown, keep the key and write `TBD`; never omit it. `points: TBD` and `effort: TBD` are valid,
and the sizing proposals in Cascade only ever fill a `TBD` after a human approves the change. Stories with no
testable acceptance criteria stay `TBD` instead of receiving an invented size.
