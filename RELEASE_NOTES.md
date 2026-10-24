# Cascade agent tools (2026-10-21d)

## Why [N] was not "AI" before
Earlier builds only wrote local stub proposals. Approve did not call GenAI.

## Now (v2026-10-21d)
After you approve a goal plan, tools can run:
- **ScanStoryPoints** - GenAI estimates for defined STORYs; TBD if weak; files under agent_runs/<id>/
- **SuggestParentLinks** - GenAI/heuristic parents
- **ApplySandboxPoints** - optional sandbox description update (explicit second prompt)
- **PromoteCbr** - always blocked unless production flags (still does not auto-write CBR)

## Priority map
1 Points scan - done (AI local)
2 Parent suggestions - done (AI local)
3 Richer refine - still use [F]/[L]
4 Sandbox apply points - optional on execute
5 Agent executes real tools - done
6 CBR promote - gated/blocked by default

# Cascade 0.4.0 — Stages 1–3

## Latest Fix (Oct 2026)
- **Fixed parse error in Invoke-BacklogAssess.ps1**: Replaced em-dash (`—`) with ASCII hyphen in markdown formatting string. This was breaking PowerShell 5.1 module import / script parsing.
- Updated `Cascade.psd1` to export `Invoke-BacklogAssess`.
- Repackaged `cascade.zip`.

## Stage 1 — Sandbox upsert
- `[J]` / `Invoke-BulkPushSandbox.ps1` **upserts by source_iid** (default)
- Finds existing `refine::sandbox-review` mirrors and **PUT** updates them
- `-NoUpsert` forces create-only; DryRun shows CREATE vs UPDATE
- Schema body/meta unchanged (TYPE :: title :: HUMAN APPROVAL REQUIRED, PfW, TBD sizing)

## Stage 2 — Assess
- Host **`[E]`** → `Invoke-BacklogAssess.ps1`
- Loads sandbox and/or CBR issues, runs hierarchy integrity + DoR/quality findings
- Writes `output/assess_findings.json` and `output/assess_report.md`
- Optional `-DraftToSandbox` for High findings (confirm)

## Stage 3 — Agent Goal
- Host **`[N]`** → `Invoke-AgentGoal.ps1` (planned)
- `Start-CascadeAgentGoal` plans actions from goal + assess pack

## Quick Start (after unzip)
```powershell
cd cascade
.\Start-CascadeHost.ps1
# Then press [E] for Assess or [L] for bulk triage
```

Use `[L]` first (read-only triage table for Shawn), then `[E]`, then `[J]` to push normalized items to sandbox.