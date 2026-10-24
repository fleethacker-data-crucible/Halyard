# Cascade work-item structure — deviation analysis & target standard

**Purpose:** Explain why sandbox bulk-push copies lacked effort, points, sprint, milestone, and type-correct bodies — and define what each work item **should** contain when we redo or promote.

**Related code:** `scripts/Invoke-BulkPushSandbox.ps1`, `src/Cascade/Taxonomy.ps1` (`New-CascadeMetaBlockFromTaxonomy`), `docs/agile-work-item-model.md`

---

## 1. What went wrong (root cause)

`[J]` bulk push was built as a **triage mirror**, not a **structured Cascade work item**.

| Expected (playbook / taxonomy) | What `[J]` actually did |
|--------------------------------|-------------------------|
| Type-correct **meta** including effort / points / sprint / milestone | Called `New-CascadeMetaBlockFromTaxonomy` with **empty** `-Effort '' -Points '' -Sprint '' -Milestone ''` and **empty** `-Parent ''` |
| Type-correct **body** (outcome / DoD / AC / tasks) | Custom body: ORIGIN table + original CBR title/labels/description only |
| Visible Cascade summary (`New-CascadeVisibleMirror`) | Not used |
| Sizing from refine AI or heuristics | Bulk table has **no** effort/points fields; push did not invent placeholders |
| Parent chain Theme → Epic → Feature → Story | Parent left blank on every row |

### Exact code (current)

```powershell
$meta = New-CascadeMetaBlockFromTaxonomy -Type $typeGuess -Id $cascadeId `
    -Theme $theme -Parent '' -State open -Area 'data-crucible' `
    -Effort '' -Points '' -Sprint '' -Milestone '' -Extra $extra

$body = @"
$meta
## ORIGIN ...
## Original CBR title / labels / description
"@
```

So the schema **supports** effort/points; the **push path never filled them** and never applied level-specific body templates.

### Secondary causes

1. **`[L]` bulk sample** only returns triage columns (`type_guess`, `theme_guess`, `action`, `why`, `needs_split`). It is not a full refine.
2. **CBR sources** are mostly unstructured tickets (no Cascade meta, often empty descriptions).
3. **Speed goal** for Shawn’s queue optimized for origin link + type guess, not full DoR-shaped issues.

That was intentional for v1 of `[J]`, but it **deviates** from the locked playbook and is insufficient for structure review.

---

## 2. Target standard (what it should be)

### 2.1 Hierarchy (locked)

```text
THEME → EPIC → FEATURE → STORY
                           └── TASKS (checklist inside Story body)

MILESTONE  associated on Epic and/or Feature and/or Story (not a parent)
```

### 2.2 Title

```text
{TYPE} :: {Short name} :: HUMAN APPROVAL REQUIRED
```

Examples:

- `THEME :: Platform Engineering :: HUMAN APPROVAL REQUIRED`
- `EPIC :: Production schema parity :: HUMAN APPROVAL REQUIRED`
- `FEATURE :: RDS IAM authentication :: HUMAN APPROVAL REQUIRED`
- `STORY :: Map IAM roles to Postgres roles :: HUMAN APPROVAL REQUIRED`

### 2.3 Metadata block (`~~~cascade-meta`)

**Always present fields:** `type`, `id`, `theme`, `parent`, `state`, `area`

**Plus by type:**

| Type | Required / expected meta |
|------|---------------------------|
| **THEME** | `status: active\|sunset` |
| **EPIC** | `theme:` (required); `milestone:` optional |
| **FEATURE** | `parent:` (Epic id); `theme:`; **`effort: Small\|Medium\|Large`**; `milestone:` optional |
| **STORY** | `parent:` (Feature id); `theme:`; **`points: 1\|2\|3\|5\|8\|13`**; **`sprint:`**; `milestone:` optional |

**Sandbox / bulk extras (keep):**

```text
source: bulk-push-sandbox
source_project: 16629
source_iid: 271
bulk_action: ACCEPT_CANDIDATE|MEETING|SKIP
confidence: high|medium|low
needs_split: true|false
review_status: pending
```

#### Example — FEATURE

```text
~~~cascade-meta
type: FEATURE
id: FEATURE-rds-iam-auth
theme: THEME-platform-engineering
parent: EPIC-prod-test-schema-parity
state: open
area: data-crucible
effort: Medium
milestone:
source_project: 16629
source_iid: 275
bulk_action: ACCEPT_CANDIDATE
confidence: medium
needs_split: false
review_status: pending
~~~
```

#### Example — STORY

```text
~~~cascade-meta
type: STORY
id: STORY-associate-iam-postgres-roles
theme: THEME-platform-engineering
parent: FEATURE-rds-iam-auth
state: open
area: data-crucible
points: 5
sprint: TBD
milestone:
source_project: 16629
source_iid: 276
bulk_action: ACCEPT_CANDIDATE
confidence: medium
needs_split: false
review_status: pending
~~~
```

When sizing is **unknown**, still emit the keys with explicit placeholders (do not omit):

- Feature: `effort: TBD`
- Story: `points: TBD`, `sprint: TBD`

Reviewers then fill or reject; absence of keys was the defect.

### 2.4 Human-visible mirror (after meta)

Use the same pattern as other Cascade writers:

```markdown
### Cascade
- **Type:** STORY
- **Id:** STORY-associate-iam-postgres-roles
- **Parent:** FEATURE-rds-iam-auth
- **Theme:** THEME-platform-engineering
- **Effort / Points / Sprint:** (as applicable)
- **Milestone:** (if any)
- **CBR origin:** project 16629 !276
```

### 2.5 Body by type

#### THEME

| Section | Required |
|---------|----------|
| Purpose | Yes |
| In scope / Out of scope | Yes |
| Owner | Yes |
| Child Epics | As known |
| Related milestones | Optional |

**Not in body:** points, sprint, task checklists, funding tables.

#### EPIC

| Section | Required |
|---------|----------|
| Outcome | Yes |
| Success picture (bullets) | Yes |
| Owner | Yes |
| Child Features | As known |
| Risks / dependencies | Recommended |
| Milestone | If targeting a release gate |

**Not in body:** story-point totals as plan of record; sprint lists.

#### FEATURE

| Section | Required |
|---------|----------|
| Description | Yes |
| Parent Epic | Yes |
| Definition of done | Yes |
| Effort (S/M/L) + short rationale | Yes (or TBD) |
| Child Stories | As known |
| Milestone | Optional |

#### STORY

| Section | Required |
|---------|----------|
| User need (who/what/why) | Yes |
| Acceptance criteria | Yes |
| **Tasks** (checklist) | Yes |
| Parent Feature | Yes |
| Points + sprint | In meta (TBD allowed) |
| Constraints / notes | As needed |

#### TASK

Default: **checkbox lines under the Story**, not separate GitLab issues.

---

## 3. Full STORY example (sandbox review copy)

**Title**

```text
STORY :: Associate IAM Roles to Postgres Roles :: HUMAN APPROVAL REQUIRED
```

**Description**

```markdown
~~~cascade-meta
type: STORY
id: STORY-associate-iam-postgres-roles
theme: THEME-platform-engineering
parent: FEATURE-rds-iam-auth
state: open
area: data-crucible
points: TBD
sprint: TBD
milestone:
source: bulk-push-sandbox
source_project: 16629
source_iid: 276
bulk_action: ACCEPT_CANDIDATE
confidence: medium
needs_split: false
review_status: pending
~~~

### Cascade
- **Type:** STORY
- **Id:** STORY-associate-iam-postgres-roles
- **Parent:** FEATURE-rds-iam-auth *(confirm or set)*
- **Theme:** THEME-platform-engineering
- **Points:** TBD
- **Sprint:** TBD
- **CBR origin:** 16629 !276

## Review decision
- [ ] Approve structure
- [ ] Edit type/parent/sizing
- [ ] Split
- [ ] Reject (leave CBR as-is)
- [ ] Needs meeting

## User need
As a Data Crucible engineer, I need SageMaker-linked IAM principals mapped to
Postgres roles in TEST so governed access does not rely on shared passwords.

## Acceptance criteria
- [ ] At least one IAM role mapped to one Postgres role in TEST
- [ ] Connection test recorded
- [ ] Break-glass path documented
- [ ] No production change in this Story

## Tasks
- [ ] Confirm role ARNs
- [ ] Apply grants in TEST
- [ ] Connection test
- [ ] Update runbook

## ORIGIN
| Field | Value |
|-------|-------|
| CBR project | 16629 |
| CBR issue IID | !276 |
| Bulk why | (from triage) |

## Original CBR content
(labels + description snapshot)
```

---

## 4. What bulk push should do on redo (spec)

1. Keep type guess from bulk table (THEME|EPIC|FEATURE|STORY).
2. Always emit **full meta keys for that type**, using **TBD** when unknown — never omit `effort` / `points` / `sprint` for Feature/Story.
3. Parent: use theme_guess; parent id `TBD` or empty with explicit `(none — set in review)` in visible mirror.
4. Body template **by type** (sections above), then ORIGIN + original CBR snapshot.
5. Call shared helpers (`New-CascadeMetaBlockFromTaxonomy` + visible mirror), not a one-off ORIGIN-only body.
6. Optional later: `[F]`-quality AI fill for AC/outcome on ACCEPT rows only; still TBD sizing until human sets it.

---

## 5. Labels (sandbox)

| Label | Meaning |
|-------|---------|
| `area::data-crucible` | Area |
| `cascade` / `cascade-test` | Cascade-managed sandbox |
| `refine::sandbox-review` | Shawn queue |
| `type::story` (etc.) | Type filter |
| `refine::ai-accepted` / `human-needed` / `bulk-skip` | Triage bucket |

---



---

## 7. Artifacts: PfW and DWI (Feature work definition)

These were part of the earlier Cascade / SMPG standard and are **not** produced by current `[L]`/`[J]` paths. That is a second deviation, not only missing effort/points.

| Term | Meaning |
|------|---------|
| **PfW** | **Proposal for Work** — default Feature-level work definition (lighter) |
| **DWI** | **Defined Work Instruction** — fuller researched package for self/peers (same family as PfW; not a contract SOW) |
| **Lightweight** | Standing/small Feature; PfW/DWI may be skipped **with rationale** |

### When AI should draft them

If context is enough (CBR title + description + labels + triage `why` + related items), Cascade should **draft** a PfW or DWI instead of leaving the Feature body as a raw CBR paste.

| Signal | Prefer |
|--------|--------|
| Short, clear Feature; limited uncertainty | **PfW** (default) |
| Multi-step, research-heavy, security/IAM/data boundaries, or long CBR body | **DWI** |
| Trivial ops/INC under an existing Feature | **Lightweight** + one-line rationale (no separate PfW issue required) |

### Feature meta (artifact keys)

```text
artifact_level: Proposal for Work | Defined Work Instruction | Lightweight
pfw_id: PFW-...          # when PfW exists or is drafted
pfw_iid: <sandbox iid>   # when stored as its own issue
dwi_id: DWI-...
dwi_iid: <sandbox iid>
```

### PfW body (minimum)

| Section | Content |
|---------|---------|
| Problem / opportunity | Why this Feature |
| Proposed approach | How we intend to deliver |
| Scope in / out | Boundaries |
| Done-when | Observable Feature completion |
| Dependencies | Teams, systems, tickets |
| Risks | Short |
| Suggested child Stories | 2–5 titles (for later Story creation) |

### DWI body (minimum)

Everything in PfW, plus:

| Section | Content |
|---------|---------|
| Background / research notes | What is already known |
| Interface / data contracts | If integration-heavy |
| Security / compliance notes | When relevant |
| Test / verification approach | How we will know |
| Rollout / backout | When operational |
| Open questions | Explicit list |

### How they attach to the hierarchy

```text
EPIC
  └── FEATURE  (artifact_level + link to PfW or DWI)
        ├── PFW or DWI   (linked work-definition artifact; may be same description or child issue)
        └── STORY × N    (from Done-when / PfW / DWI story list)
              └── Tasks checklist
```

**Preferred storage for sandbox review:**  
- Embed a `## Proposal for Work` or `## Defined Work Instruction` section **in the Feature body**, and set `artifact_level` in meta.  
- Or create a linked `PFW ::` / `DWI ::` issue and set `pfw_iid` / `dwi_iid` on the Feature.

### Gap in current tooling

| Path | Produces PfW/DWI? |
|------|-------------------|
| `[L]` bulk triage | No |
| `[J]` bulk push | No — ORIGIN + CBR paste only |
| `[F]` 1:1 refine | Partial (refine fields; not full PfW/DWI draft by default) |
| Offline `fixtures/proposals/` | Corpus for mapping; not auto-attached on push |

### Target behavior on redo / promote

1. Classify Feature vs Story vs Epic (existing).
2. For each **FEATURE** (and large STORY that should be promoted to Feature):
   - If CBR description/snippet has enough signal → **draft PfW or DWI section** in the sandbox body.
   - Set `artifact_level` accordingly (default **Proposal for Work**).
   - List **suggested child Stories** from Done-when.
3. For **Lightweight** only when clearly small; require one-line rationale in body.
4. Never block sandbox push solely because PfW is imperfect — mark `artifact_level` + `review_status: pending` so Shawn accepts or edits the draft.

### Example Feature meta + PfW stub

```text
~~~cascade-meta
type: FEATURE
id: FEATURE-rds-iam-auth
theme: THEME-platform-engineering
parent: EPIC-prod-test-schema-parity
state: open
area: data-crucible
effort: Medium
artifact_level: Proposal for Work
pfw_id: PFW-rds-iam-auth
source_project: 16629
source_iid: 275
review_status: pending
~~~
```

```markdown
## Proposal for Work
### Problem
Static DB passwords block governed SageMaker access patterns.

### Approach
Map SageMaker IAM roles to Postgres roles in TEST; document break-glass.

### Done-when
- IAM auth proven in TEST
- Mapping documented
- Security informed

### Suggested Stories
- STORY :: Research SageMaker RDS Auth
- STORY :: Associate IAM Roles to Postgres Roles
- STORY :: Configure IAM Postgres Roles
```


## 6. Decision for redo

Current sandbox copies are **valid as origin-linked triage clones** but **not** playbook-compliant work items.

**Options:**

A. **Wipe + re-push** with template bodies + TBD sizing meta (recommended before Shawn deep-dives on structure).  
B. Leave as-is; Shawn only validates type/priority; structure pass happens in `[F]` or promote.  
C. Hybrid: re-push ACCEPT/MEETING with full templates; leave SKIP as light mirrors.

Recommended: **A** once `[J]` is fixed to match this document.

---

*Generated from code inspection of Invoke-BulkPushSandbox.ps1 and taxonomy meta builder. Aligns with Cascade Agile Playbook (Theme → Epic → Feature → Story).*
