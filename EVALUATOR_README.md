# Cascade package — evaluator notes

## What this is
PowerShell tooling for GitLab-backed agile hierarchy:

Theme → Epic → Feature → Story (tasks as checklist in Story)

Plus Feature work definitions: **PfW** (Proposal for Work) and **DWI** (Defined Work Instruction).

## Not included in repo
- Live tokens / `config/cascade.env` (local only; see `cascade.env.example`)
- Live network credentials

## Core scripts
| Script | Role |
|--------|------|
| `Start-CascadeHost.ps1` | Menu host |
| `Invoke-BulkRefineSample.ps1` | `[L]` CBR triage table (read-only) |
| `Invoke-BulkPushSandbox.ps1` | `[J]` Schema-compliant sandbox rebuild |
| `Invoke-SandboxRebuildFromCbr.ps1` | Alias: rebuild after wipe (`-FromCbrDirect`) |
| `Invoke-BacklogRefine.ps1` | `[F]` One-at-a-time refine |
| `WritePolicy.ps1` | Sandbox vs production write gates |

## Schema references
- `docs/agile-work-item-model.md` — topology + meta
- `docs/work_item_structure_target.md` — target body/meta/PfW-DWI
- `docs/agile_playbook.md` — PfW/DWI DoR
- `config/taxonomy.json` — theme ids + required_meta
- `fixtures/dor_rules.json` — Definition of Ready codes

## Sandbox rebuild after wipe
1. `[X]` wipe sandbox
2. `[L]` optional (triage table)
3. `[J]` or `.\scripts\Invoke-SandboxRebuildFromCbr.ps1 -FromCbrDirect`
4. Filter `refine::sandbox-review`

Each sandbox issue includes: cascade-meta (effort/points/sprint TBD as needed), type body, PfW draft on Features, ORIGIN `source_iid` to CBR.

## Write policy
Default writes target sandbox project only. CBR is read for triage/rebuild unless production mode is explicitly enabled.
