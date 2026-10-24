# Cascade / SMPG Agile Playbook (AI + adapter)

Authority: SMPG Agile Delivery Playbook, Work Definition Standard, Epic/Feature/Story templates.

## Terminology

| Term | Meaning |
|------|---------|
| **DoR** | **Definition of Ready** - is this item ready to pull / start? |
| **PfW** | **Proposal for Work** - default Feature-level work definition (lighter) |
| **DWI** | **Defined Work Instruction** - researched package of work for self/peers (same family as PfW; use when fuller than a short PfW). Not a classic contract SOW. |
| **AC** | Acceptance Criteria on a Story (required before Ready) |

## Hierarchy

```
EPIC > FEATURE > STORY
```

- **TASK** is not a preferred Git work item. Use an **in-Story task checklist** (AI-suggested) instead.
- Operational/INC tickets default to **STORY** under a Feature.

## Cascade metadata (system of record)

HTML comment + visible `### Cascade` mirror.

| Key | Required | Meaning |
|-----|----------|---------|
| type | yes | EPIC \| FEATURE \| STORY \| PFW \| DWI |
| id | yes | Stable Cascade id |
| state | yes | OPEN \| READY \| IN_PROGRESS \| DONE \| BLOCKED |
| parent | type not EPIC | Parent Cascade id |
| parent_iid | when known | GitLab iid of parent issue |
| artifact_level | Feature | Proposal for Work \| Defined Work Instruction \| Lightweight |
| pfw_id / pfw_iid / dwi_id / dwi_iid | Feature DoR | Link to PfW or DWI |
| effort_band | Story optional | XS S M L XL |

### State vs GitLab

| Cascade state | GitLab issue |
|---------------|--------------|
| OPEN, READY, IN_PROGRESS, BLOCKED | opened |
| DONE | closed |

Adapter apply must set GitLab open/closed to match when meta state changes.

## DoR by type

### Feature (Ready)
- Short description
- **Feature Done-When** (observable)
- Parent EPIC (`parent` + `parent_iid` when exists)
- Artifact level + **linked PfW or DWI** (or AI-drafted, human-accepted)
- Child Stories exist (or AI proposes them from Done-When / PfW / DWI)

### Story (Ready)
- Story text
- **Acceptance Criteria**
- Parent FEATURE
- Optional AI **task checklist** inside the description (not separate Git issues)

### Epic
- Outcome statement, success picture; children are Features only.

## Parent / child operations (AI + adapter)

1. **set_parent** - assign `parent` / `parent_iid` on an existing item
2. **create_story** (child) - when Feature has no fully defined Stories, AI drafts 2-5 Stories from Done-When / PfW / DWI
3. **link_pfw** / **link_dwi** - attach existing proposal corpus file or Git iid
4. **draft_acceptance_criteria** / **draft_done_when** / **draft_task_checklist**
5. **normalize_meta** - type, id, parent, parent_iid, state + visible mirror

Prefer catalog Feature/Epic ids. Prefer STORY over TASK. Never invent secrets.

## Proposal corpus

Offline PfW/DWI markdown under `fixtures/proposals/`. AI maps Features to these when titles/topics align; otherwise draft new DWI/PfW for human acceptance.

## AI response contract

Return ONLY JSON:

```
{
  "analysis": [ { "iid": 123, "deficiencies": ["..."], "notes": "..." } ],
  "proposals": [
    {
      "action": "set_parent|create_story|normalize_meta|link_pfw|link_dwi|draft_acceptance_criteria|draft_done_when|draft_task_checklist|create_feature",
      "for_iid": 123,
      "type": "EPIC|FEATURE|STORY|PFW|DWI",
      "id": "STORY-x",
      "parent": "FEATURE-y",
      "parent_iid": "",
      "title": "...",
      "state": "OPEN",
      "acceptance_criteria": ["..."],
      "task_checklist": ["Research / discover", "Implement", "Test in TEST", "Peer review"],
      "note": "..."
    }
  ]
}
```

## Hierarchy source of truth (data-crucible)

Prefer parents from `fixtures/hierarchy_catalog.json`, seeded from the
**SM-DATS Medallion Gap Analysis & Roadmap (09 Oct 2026)**:

- **EPIC-bronze** (active) - F1.1-F1.6 baseline through run
- **EPIC-silver** (parked) - evaluate later only
- **EPIC-gold** (active, light) - catalog + Daily Snapshots ETL
- **EPIC-gitops** (parallel) - CDK core, TEST/PROD, drift

Legacy FEATURE-data-lake / FEATURE-postgres-* may appear in older suggestions;
map new work to FEATURE-B-* / FEATURE-G-* / FEATURE-GO-* ids.

Story points: Fibonacci 1/2/3/5/8/13 on Stories only. Feature = sum of children.

## SMPG Agile Delivery Playbook (authoritative process)

Source of process truth: **SMPG Agile Delivery Playbook (09 Oct 2026)**.

- Feature = leadership unit (% complete). Story = engineer pull unit (Ready before sprint).
- Artifact level on Features: Full SOW | Proposal (default) | Lightweight (standing).
- Story Ready: parent Feature + testable AC + effort band + deps known.
- GenAI drafts only; human accept with name + date before Ready.
- Capacity split: 70-80% committed / 10-15% a-la-carte / 10-15% experimentation.
- Prefer Azure DevOps; Git optional with same hierarchy and Ready-gate.
- **Gold** is delivery Epic 3 (consumption metrics) but stays light until metric catalog rows exist.
- Standing Epics 5-9 absorb infra, R&D, process, hackathon, DB tooling work that is not Bronze/Silver/Gold/GitOps.

## Epic lifecycle and health

See `docs/epic_lifecycle_and_health.md` (next iteration: wire into Epic shells and meta).
