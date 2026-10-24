# Roadmap

Sandbox first, human-gated, provider-agnostic. Last updated 2026-10-24 with the v1.0 refactor.

## Done in v1.0 (the refactor)

| Area | Capability |
|---|---|
| Architecture | C# core (`Cascade.Core`) loaded by PowerShell 5.1; React/TypeScript UI; one launcher |
| Providers | GitHub and GitLab behind one work-item model; cascade-meta is the hierarchy source of truth |
| Safety | Single write gateway, sandbox allow-list, protected source, synthetic-only deletes, write audit |
| Sandbox | Deterministic 100-item synthetic hierarchy; seed, dry-run and teardown by run |
| Analysis | Topology audit, definition-of-ready checks, hierarchy tree with point rollups, review pack export |
| Proposals | Evidence-ranked parent links; model sizing with definability gate and policy clamps; approve → apply |
| Body sync | Applying a size rewrites meta, the `### Cascade` mirror and `## Sizing` together |
| Tests | xUnit, Pester (incl. repository rules), UI unit tests, offline end-to-end loop, CI on the Windows runner |

## Next: prove it on GitHub, then port

| # | Item | Notes |
|---|---|---|
| 1 | ~~Run the loop against `sun-of-anton/cascade-sandbox`~~ | Done 2026-10-24: 100 seeded, 98 sub-issue links, synced, audited, 6 sizes applied. Teardown still to be run from a normal machine |
| 2 | Run on the workstation against 52999 | Confirms DLL load under application control, ARC unlock, GitLab paging |
| 3 | Feature effort proposals end to end on real data | Already implemented; needs real-data tuning |
| 4 | Diff preview before apply | Show the before/after body per proposal |
| 5 | Rollback of the last apply batch | Store the previous body with each applied proposal |

## Later

| # | Item | Notes |
|---|---|---|
| 6 | Refine assistant | Stronger acceptance criteria, tasks and PfW/DWI drafts as proposals |
| 7 | Multi-step goals | assess → size → parents → report in one run, still gated per batch |
| 8 | GitLab native links | Read GitLab epics/work-item links as context once the server version is confirmed |
| 9 | Signed bundle | Code-sign `Cascade.Core.dll` if application control requires it |
| 10 | CBR promote | Explicit, gated, with diff; never the default path |

## Not doing

- Automatic writes to the CBR or any production project
- Treating Feature effort and Story points as the same field
- Runtime dependencies on the workstation beyond Windows PowerShell 5.1 and .NET Framework 4.8
