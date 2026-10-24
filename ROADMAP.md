# Roadmap

Sandbox first, human-gated, provider-agnostic. Last updated 2026-10-24.

## Done

| Area | Capability |
|---|---|
| Architecture | C# core (`Cascade.Core`) loaded by PowerShell 5.1; React/TypeScript UI; one launcher |
| Providers | GitHub and GitLab behind one work-item model; cascade-meta is the hierarchy source of truth |
| Safety | Single write gateway, sandbox allow-list, protected source and extra protected repos, synthetic-only deletes, write audit |
| Sandbox | Deterministic synthetic hierarchy; 80% complete / 20% with one of 21 kinds of gap (rate adjustable); seed, dry-run and teardown by run |
| Analysis | Topology audit, definition-of-ready checks, hierarchy tree with point rollups, Markdown/CSV review pack |
| Refinement | Model drafts missing sections (acceptance criteria, user need, tasks, done-when, outcome, success picture, scope), keeps existing text, re-checked against the DoR rules |
| Metadata | Deterministic repair of cascade-meta from labels, titles and TBD placeholders |
| Proposals | Parents from evidence; model sizing with definability gate and policy clamps; hand edits as approved proposals; one projection for preview and apply; diff per row |
| UI | Bulk actions on Review; next actions and rollups on Overview; keyboard tree with remembered expansion; in-place edit; API key from Settings |
| Tests | xUnit (118), Pester (16, incl. repository rules), UI unit tests, offline end-to-end loop proving every injected gap is found and fixed, CI on the Windows runner |

## Next

| # | Item | Notes |
|---|---|---|
| 1 | Review the product end to end with a real model key | Seed with gaps, walk Review → Proposals with the diff, apply, re-sync |
| 2 | Tune the refinement prompt on real tickets | The synthetic gaps are clean; hand-written tickets are not |
| 3 | Run on the workstation against a GitLab sandbox | Confirms DLL load under application control, vault unlock, GitLab paging |
| 4 | Rollback of the last apply batch | Store the previous body with each applied proposal; one-click restore |
| 5 | Intake from the source backlog | Pick source tickets, propose type/theme, create structured copies in the sandbox with a link back |

## Later

| # | Item | Notes |
|---|---|---|
| 6 | Multi-step goals | assess → repair → draft → size → report in one run, still gated per batch |
| 7 | GitLab native links | Read GitLab epics/work-item links as context once the server version is confirmed |
| 8 | Signed bundle | Code-sign `Cascade.Core.dll` if application control requires it |
| 9 | Shared proposal state | Proposals are JSON files under `output/`; two operators on one sandbox need coordination |
| 10 | Production promote | Explicit, gated, with diff; never the default path |

## Not doing

- Automatic writes to the source backlog or any production project
- Treating Feature effort and Story points as the same field
- Runtime dependencies on the workstation beyond Windows PowerShell 5.1 and .NET Framework 4.8
