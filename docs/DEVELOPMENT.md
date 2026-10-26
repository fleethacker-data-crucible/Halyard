# Development

## Prerequisites (build machine only)

- .NET 8 SDK (builds `net48` too, via reference assemblies)
- Node.js 20+
- PowerShell 7 or Windows PowerShell 5.1, Pester 5 (`Install-Module Pester -Scope CurrentUser`)

## Layout

```
Launch-Halyard.bat           the only launcher
module/Halyard/              PowerShell module (launcher, Lazarette bridge, command wrappers)
scripts/                     Start-Halyard, Invoke-SandboxSeed, Invoke-SandboxTeardown
src/Halyard.Core/            C# core (net48 + netstandard2.0)
src/Halyard.Core.Tests/      xUnit
web/                         React + TypeScript UI (build.mjs → web/dist)
tests/pester/                PowerShell tests and repository rules
tests/e2e/sandbox-loop.mjs   full seed → apply → teardown walk against a running host
tests/fakes/fake-github.mjs  in-memory GitHub + chat-completions fake for offline e2e (Node)
fixtures/                    deterministic synthetic catalogues (100 items; one with 20% gaps)
config/                      halyard.env.example, taxonomy.json
docs/                        this documentation
build/Build-Halyard.ps1      test + bundle
```

## Build and test

```powershell
build\Build-Halyard.ps1                  # everything: core, tests, UI, Pester, bundle in dist\
dotnet test                              # core only
cd web; npm install; npm run build       # UI only → web\dist
cd web; npm test; npm run typecheck
Invoke-Pester tests\pester
```

Run from source without bundling: build the core (`dotnet build -c Release`) and the UI, then
`.\Launch-Halyard.bat --console`. The module finds the DLL under `src\Halyard.Core\bin\Release\<tfm>` and
the UI under `web\dist`.

UI work: `cd web; npm run dev` rebuilds on save; reload the browser.

## Offline end-to-end loop

```bash
node tests/fakes/fake-github.mjs 8790 &
# config/halyard.env (or environment):
#   HALYARD_PROVIDER=github  GITHUB_API_URL=http://127.0.0.1:8790  GITHUB_TOKEN=fake
#   HALYARD_SANDBOX_REPO=fleethacker-data-crucible/Halyard-Ballast  HALYARD_SOURCE_REPO=sun-of-anton/raw-backlog
#   HALYARD_WRITE_DELAY_MS=0
#   HALYARD_LLM_PROFILE=openai  LLM_URL=http://127.0.0.1:8790/v1/chat/completions  LLM_TOKEN=fake
.\Launch-Halyard.bat --console --no-browser
node tests/e2e/sandbox-loop.mjs http://127.0.0.1:8773 <session token from the launch URL> 30 --fake http://127.0.0.1:8790
```

The loop seeds with gaps, checks the audit raises every expected code, repairs metadata, drafts sections
with the fake model, sizes, applies a manual edit, re-syncs, renders the leadership report, plans
sprints, tags skills, drafts a vision, plants raw tickets in the fake source repository (`--fake`) and
runs intake on them, then tears down. `tests/e2e/Run-E2E.ps1` does all of it on Windows. The same script
without `--fake` against a real sandbox repository proves the concept on GitHub. The fake GitHub keeps
state per repository and answers the refine, vision and skills prompts.

## Conventions

- **Only** `Providers/*` and `Llm/*` build mutating HTTP calls, and **only** `WriteGateway` calls
  `OpenWriter`. Pester fails the build otherwise.
- PowerShell code must run on 5.1: no `??`, `?:`, `&&`/`||` pipeline chains (checked by Pester), ASCII only.
- Core code must compile for `net48`: stick to the `netstandard2.0` API surface.
- Stable codes (`story_missing_acceptance_criteria`, `PARENT_NOT_DECLARED`, …) are part of the UI and exports;
  change explanations freely, change codes only deliberately.
- The synthetic generator is deterministic. If you change it, regenerate the fixtures:
  `New-HalyardSyntheticCatalog; New-HalyardSyntheticCatalog -Path fixtures\synthetic-work-items-with-gaps.json -IncludeDefects`.
  A test fails when they drift.
- Nothing organisation-specific goes in the repository: no real hostnames, project ids, team names or
  vault item names. They belong in `config/halyard.env`.

## CI

`.github/workflows/ci.yml` runs on the self-hosted Windows runner (labels `self-hosted, Windows, X64`):
core build and tests on `net8.0` and `net48`, UI typecheck/tests/build, Pester on both Windows
PowerShell 5.1 and PowerShell 7, the offline end-to-end loop, and uploads the bundle as an artifact.
