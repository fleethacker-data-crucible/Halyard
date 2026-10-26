# Roadmap

Goal: an autonomous product owner that works downward (raw tickets → ready, sized, skilled, planned
work) and upward (delivery status and vision for leadership), sandbox first, human-gated,
provider-agnostic. Last updated 2026-10-24.

## Done

| Area | Capability |
|---|---|
| Architecture | C# core (`Halyard.Core`) loaded by PowerShell 5.1; React/TypeScript UI; one launcher |
| Providers | GitHub and GitLab behind one work-item model; several source repositories per snapshot; halyard-meta is the hierarchy source of truth |
| Safety | Single write gateway, sandbox allow-list, protected source and extra protected repos, synthetic-only deletes, write audit; proposals refused outside the sandbox |
| Sandbox | Deterministic synthetic hierarchy with history: closed stories in past sprints, carry-over, blockers, owners, skills, feature targets; 80/20 complete/gap, rate adjustable |
| Analysis | Topology audit, definition-of-ready checks, hierarchy tree with rollups, Markdown/CSV review pack |
| Delivery | Feature % complete and status, velocity, ready backlog, capacity used, blockers with age; leadership page and self-contained HTML report; Outlook/SMTP weekly send and schedule scripts |
| Team | `team.json` capacity and sprint calendar, `skills.json` taxonomy shared with Shellback, Shellback peer cards imported read-only (consent, confidence, availability) |
| Intelligence | Model drafts for missing sections, sizing with definability gate, skill tagging, vision drafting, intake classification; every model output validated against the backlog; deterministic fallbacks throughout |
| Planning | Sprint planner: ready stories by feature target and progress, first-fit into committed capacity, owners by skill match, skill gaps; becomes proposals |
| Intake | Raw source tickets categorized and copied into the sandbox with a link back; de-duplicated; source never written |
| Proposals | Eight kinds behind one projection for preview and apply; diff per row; approve, reject, apply, apply all, delete batch |
| Tests | xUnit (145), Pester (16), UI unit tests, offline end-to-end loop over every flow above, CI on the Windows runner |

## Next

| # | Item | Notes |
|---|---|---|
| 1 | Review with a real model key | Seed, Review → Proposals, Plan, Vision, Intake, Leadership report; judge the drafts |
| 2 | Tune prompts on real tickets | Synthetic gaps are clean; hand-written tickets and vision prose will need iteration |
| 3 | Run on the workstation against GitLab | DLL load under application control, vault unlock, paging, several source projects |
| 4 | Live Shellback export | Read cards from a Shellback export or endpoint instead of files dropped in `config/shellback/` |
| 5 | Sprint review loop | Compare planned vs delivered each sprint; feed the gap into velocity and the next plan |
| 6 | Rollback of the last apply batch | Store the previous body with each applied proposal; one-click restore |

## Later

| # | Item | Notes |
|---|---|---|
| 7 | Multi-step goals | intake → repair → draft → size → tag → plan → report in one run, still gated per batch |
| 8 | Report delivery beyond email | Teams / Slack webhook when the workstation allows it |
| 9 | GitLab native links | Read GitLab epics/work-item links once the server version is confirmed |
| 10 | Signed bundle | Code-sign `Halyard.Core.dll` if application control requires it |
| 11 | Shared proposal state | Proposals are JSON files under `output/`; two operators on one sandbox need coordination |
| 12 | Production promote | Explicit, gated, with diff; never the default path |

## Not doing

- Automatic writes to the source backlog, any production project, or Shellback
- Storing anything about a person beyond what their Shellback card consented to
- Treating Feature effort and Story points as the same field
- Runtime dependencies on the workstation beyond Windows PowerShell 5.1 and .NET Framework 4.8
