# Roadmap

Goal: an autonomous product owner that works downward (raw tickets → ready, sized, skilled, planned
work) and upward (delivery status and vision for leadership), sandbox first, human-gated,
provider-agnostic. Last updated 2026-10-31.

## Done

| Area | Capability |
|---|---|
| Architecture | C# 5 core (`Halyard.Core`) shipped as source and compiled in memory by PowerShell 5.1 at launch (no DLL, no third-party assembly); React/TypeScript UI; one launcher |
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
| Tests | xUnit, Pester and UI unit tests, an offline end-to-end loop over every flow above, CI on the Windows runner. GitLab is covered against a stateful fake project and report delivery with mocked Outlook/SMTP (draft PR); nothing has run against a real GitLab, Outlook or mail server |

## Status of the Next and Later items

Everything below is on `main`, built and tested against fakes (a fake GitHub, a stateful fake GitLab project, a scripted model, mocked Outlook/SMTP,
temp folders). "Live check" is what only a real model, real tickets, a real GitLab, Teams/Slack or Shellback can prove; those are listed for the owner
and have not been faked or run.

| Item | State | Built | Live check still needed |
|---|---|---|---|
| 1 Review with a real model key | not started | | a real key and a judge of the drafts |
| 2 Tune prompts on real tickets | not started | | real tickets |
| 3 Run on the workstation against GitLab | not started | GitLab provider is tested against a fake; native links read when the version is set | the locked-down workstation, vault unlock, paging |
| 4 Live Shellback export | built | export file/folder or endpoint reader, consent-only cache, withdrawn consent removed, Refresh button | a real Shellback export or endpoint |
| 5 Sprint review loop | built | planned vs delivered per sprint, recommended commitment, slipped stories first, Plan page panel | a few real sprints of history |
| 6 Rollback of the last apply | built | journal, preview, conflict-safe restore, forced override, Applied-changes panel | a rollback in the real sandbox |
| 7 Multi-step goals | built | ordered gated steps (repair, parents, draft, size, skills, plan, summary); model steps with `useModel`; a failing model stops the step with the reason | a real model for draft/size; intake stays a separate explicit action |
| 8 Report delivery beyond email | built | Teams/Slack summary from the browser and from `Send-HalyardReport.ps1 -Via Webhook`, dry run, https only, address never shown | a real webhook |
| 9 GitLab native links | partly | issue links read when `HALYARD_GITLAB_VERSION` is set (13.4+) | epics / work-item hierarchy (needs the newer API and a confirmed server) |
| 10 Shared proposal state | built | write lease for write jobs; batch edits are read-modify-write under a cross-process lock; an apply keeps decisions made meanwhile and honours a rejection | two machines on a network share |
| 11 Production promote | built | preview with diff basis, typed confirmation, full production gate, stale-item refusal, screen | never run against a real production repository |

Also on `main`: Fairlead as the default model endpoint, command registry with `commands --json` and a parity test, Semaphore CSV export, hand-offs from
PacketRat/Eight Bells (`fleet-handoff` v1, no exporter on their side yet), read-aloud and dictation with local speech only, accessible tab and dialog names,
`docs/CLI.md`, `docs/HANDOFF.md`, `docs/SEMAPHORE-EXPORT.md`, `docs/DEMO-WALKTHROUGH.md`.

## Next

| # | Item | Notes |
|---|---|---|
| 1 | Review with a real model key | Seed, Review → Proposals, Plan, Vision, Intake, Leadership report; judge the drafts |
| 2 | Tune prompts on real tickets | Synthetic gaps are clean; hand-written tickets and vision prose will need iteration |
| 3 | Run on the workstation against GitLab | Engine compile (Add-Type) under application control, vault unlock, paging, several source projects |
| 4 | Live Shellback export | Read cards from a Shellback export or endpoint instead of files dropped in `config/shellback/` |
| 5 | Sprint review loop | Compare planned vs delivered each sprint; feed the gap into velocity and the next plan |
| 6 | Rollback of the last apply batch | Store the previous body with each applied proposal; one-click restore |

## Later

| # | Item | Notes |
|---|---|---|
| 7 | Multi-step goals | intake → repair → draft → size → tag → plan → report in one run, still gated per batch |
| 8 | Report delivery beyond email | Teams / Slack webhook when the workstation allows it |
| 9 | GitLab native links | Read GitLab epics/work-item links once the server version is confirmed |
| 10 | Shared proposal state | Proposals are JSON files under `output/`; two operators on one sandbox need coordination |
| 11 | Production promote | Explicit, gated, with diff; never the default path |

## Not doing

- Automatic writes to the source backlog, any production project, or Shellback
- Storing anything about a person beyond what their Shellback card consented to
- Treating Feature effort and Story points as the same field
- Runtime dependencies on the workstation beyond Windows PowerShell 5.1 and .NET Framework 4.8
