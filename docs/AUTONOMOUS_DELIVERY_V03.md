# Cascade v0.3 — Autonomous Delivery Foundation

This is a **development increment**, not a production-ready autonomous product.

## What works in this increment

- Discover local Git repositories and commits with file-level evidence.
- Cluster identical normalized commit subjects by repository into candidate stories.
- Heuristically categorize work (defect, capability, maintenance, infrastructure, quality, documentation, unknown).
- Compare candidates to existing GitLab issues using a stable evidence key embedded in the issue body.
- Generate an immutable per-run JSON plan and evidence-limited leadership Markdown brief.
- Explicitly approve candidate keys for sandbox GitLab creation, with a recheck for duplicates.
- Enforce sandbox target checks and explicit `GITLAB_PROJECT_ID` equality on the new apply path.
- Tests cover grouping, no fabricated hours/completion, and duplicate avoidance.

## Quick start — read-only

```powershell
.\scripts\Invoke-AutonomousCycle.ps1 -Roots @('C:\Projects') -SinceDays 30
# Inspect output\autonomous\<run-id>\plan.json and leadership-brief.md
```

Read-only with existing GitLab work items (requires configured GitLab credentials):

```powershell
.\scripts\Invoke-AutonomousCycle.ps1 -Roots @('C:\Projects') -UseGitLab
```

Read-only with a local issues fixture:

```powershell
.\scripts\Invoke-AutonomousCycle.ps1 -Roots @('C:\Projects') -IssuesFixture '.\fixtures\issues.json'
```

## Explicit sandbox write path

1. Set `CASCADE_WRITE_MODE=sandbox` and `GITLAB_PROJECT_ID=52999` (or your explicitly configured sandbox ID).
2. Review `plan.json`; identify desired `PROPOSE_CREATE` keys.
3. Preview without `-Execute`:

```powershell
.\scripts\Apply-ApprovedSandboxPlan.ps1 -PlanPath '.\output\autonomous\<run-id>\plan.json' -ApprovedKeys @('0123456789abcdef0123') -ProjectId 52999
```

4. Add `-Execute` only when you intend to create the approved issues. The underlying PowerShell command supports `-WhatIf` and GitLab requires working credentials. No auto-close functionality is implemented.

## Limits and next gates

This is not semantic work reconstruction. Grouping is deterministic and conservative, using commit subjects; it can miss relationships and over-segment work. It does not autonomously infer epics/features, create hierarchy links, infer real hours, evaluate tests, or close work. The leadership brief is observational, **not** a verified executive delivery report. Scheduled operation, resilient API synchronization, enterprise authorization, work-item metadata round-trip normalization, and end-to-end tests on Git.mil remain to be implemented.

Do not run against production until integration and policy tests have been completed. The source environment does not have PowerShell available, so PS 5.1 runtime validation must be performed on an approved Windows host.
