# Cascade

Cascade reads a backlog from GitHub or GitLab (web.git.mil), checks every issue against the
Theme → Epic → Feature → Story → Task model, proposes fixes for missing parents and sizes, and writes
the fixes you approve to a sandbox repository, never anywhere else.

It runs locally: a PowerShell launcher, one .NET library, and a browser UI on `127.0.0.1`. Nothing is
installed on the workstation and nothing in the browser talks to the internet.

## Quick start (personal machine, GitHub)

```powershell
# one-time, on the build machine: .NET 8 SDK, Node.js 20+
build\Build-Cascade.ps1                       # tests and builds; bundle in dist\Cascade
copy config\cascade.env.example config\cascade.env
gh auth login                                 # or set GITHUB_TOKEN in config\cascade.env
.\Launch-Cascade.bat
```

In the browser: **Sandbox** → Seed 100 items, **Connection** → Synchronize the sandbox, then read
**Overview**, **Hierarchy** and **Review**, and try **Proposals**. Delete the run from **Sandbox** when done.

## On the workstation (web.git.mil)

Copy `dist\Cascade` across, set `CASCADE_PROVIDER=gitlab` and the project ids in `config\cascade.env`
(see the example file), and run `Launch-Cascade.bat`. ARC asks for CAC + TOTP in the console once; the
console then hides. The CBR is read-only; only the sandbox project can be written.

## What it does

| Page | Purpose |
|---|---|
| Overview | The cascade of levels, items needing review, sizing coverage, top gaps and findings |
| Hierarchy | Tree and graph of the declared chain, with each item's definition, meta and findings |
| Review | Every item against its definition of ready and the topology; CSV review pack |
| Proposals | Evidence-ranked parent links and model-proposed sizes; approve, reject, apply to the sandbox |
| Sandbox | Seed a deterministic synthetic hierarchy; delete it by run |
| Connection | Test the token; take a snapshot of the source or sandbox |
| Diagnostics | Engine log, every write attempt, background jobs |
| Settings | Repositories, label filter, model profile for this session |

PowerShell equivalents: `Invoke-SandboxSeed.ps1`, `Invoke-SandboxTeardown.ps1`, and the `Cascade`
module (`Get-CascadeWorkItem`, `Test-CascadeConnection`, `New-CascadeSyntheticCatalog`).

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): stack, components, write safety, local security
- [docs/OPERATIONS.md](docs/OPERATIONS.md): running, configuration, sandbox lifecycle, troubleshooting
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): building, testing, the offline end-to-end loop, CI
- [docs/WORK_ITEM_MODEL.md](docs/WORK_ITEM_MODEL.md): the hierarchy, cascade-meta, definition of ready, PfW/DWI
- [ROADMAP.md](ROADMAP.md)

## Safety in one paragraph

Writes default to the sandbox repository only. Production needs `CASCADE_WRITE_MODE=production`,
`CASCADE_ALLOW_PRODUCTION=1` and an explicit opt-in in code, and the browser can change none of them.
Deletes only touch items labelled `cascade-synthetic`, unless you type the wipe phrase. Every write is
audited. Tokens stay in process memory and never reach the browser, logs or transcripts.
