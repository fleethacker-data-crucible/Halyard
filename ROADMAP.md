# Cascade roadmap

Living plan for Cascade (sandbox-first, human-gated). Last updated: 2026-10-21.

## Design principles

- **Sandbox first.** Default write target is the sandbox project; CBR is never written silently.
- **Human gates.** Agent goals plan → approve → execute. Promote to CBR is explicit and blocked by default.
- **Meta is source of truth.** `~~~cascade-meta` drives machine fields; human-visible sections (`### Cascade`, `## Sizing`) must stay in sync after apply.
- **DoW GitLab = gitmil.** Commercial GitHub is a separate path for non-CUI work only.
- **Story points vs Feature effort.** STORYs use Fibonacci points (1, 2, 3, 5, 8). FEATUREs use effort S / M / L. Do not mix fields.

## Already working (stabilize, do not regress)

| Area | Capability | Host entry |
|------|------------|------------|
| Session | ARC unlock, cascade.env, write mode | `[A]` `[S]` |
| Read / triage | Assess findings pack | `[E]` |
| Sample / refine | Bulk sample table, 1-at-a-time refine | `[L]` `[F]` |
| Sandbox | Rebuild/upsert, wipe, taxonomy seed | `[J]` `[X]` `[U]` |
| Agent | Goal → plan → approve → GenAI tools | `[N]` |
| Sizing | Story points scan (incl. Gemini markdown tables) → local JSON/MD → optional sandbox apply | `[N]` points goal |
| Safety | Production promote gated; no silent CBR writes | — |

## Near term

| # | Function | Intent | Status |
|---|----------|--------|--------|
| 1 | **Body sync on apply** | When setting `points:` or `effort:`, also update `### Cascade` mirror and `## Sizing` so the body does not show meta=5 and UI=TBD | In progress |
| 2 | **Apply feature effort** | Same path as points: write non-TBD `effort: S\|M\|L` to sandbox FEATURE descriptions | Planned |
| 3 | **Re-sync / re-apply pass** | Fix issues already updated in meta only (stale mirror/sizing) without a full re-estimate | Planned |
| 4 | **Parent link apply** | Consume `parent_suggestions.json` → write `parent:` (and mirror) in sandbox only | Planned |
| 5 | **Parse edge cases** | Prose-only estimates (e.g. “3 Story Points”), XS→S, 13→8 policy caps | Partial |

## Mid term

| # | Function | Intent |
|---|----------|--------|
| 6 | **Richer refine `[F]`** | Stronger acceptance criteria, tasks, PfW/DWI when context exists; less empty boilerplate |
| 7 | **Definition gate for estimates** | If definition is weak, force `points: TBD` / human review instead of inventing size |
| 8 | **Parent hierarchy health** | Close STORY→FEATURE→EPIC→THEME gaps surfaced by Assess |
| 9 | **Agent goal v2** | One goal can chain: assess → points → effort → parents → local report |
| 10 | **Diff preview before apply** | Show before/after description snippet; approve per batch |

## Later (promote and ops)

| # | Function | Intent |
|---|----------|--------|
| 11 | **CBR promote (human-gated)** | Explicit write mode + flags + diff; never the default path |
| 12 | **Rollback last sandbox apply** | Undo last bulk points/parents apply |
| 13 | **Review pack export** | Table: iid, type, points, effort, parent, confidence (for stakeholder review) |
| 14 | **Playbook alignment** | Menu labels and docs match real agent tools |
| 15 | **Optional GitHub path** | Commercial / non-CUI only; gitmil remains DoW default |

## Suggested build order

1. Body sync + re-apply existing points  
2. Apply feature effort  
3. Parent suggest → apply (sandbox)  
4. Definition gate + refine quality  
5. Agent multi-step goals  
6. CBR promote (last, gated)

## Explicit non-goals (for now)

- Adding menu letters without a new capability  
- Auto-write to CBR  
- Treating Feature effort and Story points as the same field  
- Expanding agent goals until parent + effort apply are as solid as points  

## Agent tools reference (current)

| Tool | Effect |
|------|--------|
| `WriteReport` | Local `agent_report.md` |
| `CreateRefinementProposal` | Local proposal JSON (policy notes) |
| `ScanStoryPoints` | GenAI + parse → `points_estimates.json` / `.md` (and feature effort when present) |
| `ApplySandboxPoints` | Optional sandbox description update (points; body sync required) |
| `SuggestParentLinks` | Local `parent_suggestions.json` |
| `PromoteCbr` | Blocked unless production flags; no auto CBR write |

## Related files

- Host: `scripts/Start-CascadeHost.ps1`  
- Agent: `src/Cascade/AgentRuntime.ps1`, `src/Cascade/AgentTools.ps1`  
- Schema / meta: `src/Cascade/Schema.ps1`  
- Bulk sandbox body template: `scripts/Invoke-BulkPushSandbox.ps1`  
