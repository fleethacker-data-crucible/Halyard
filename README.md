# Cascade

Cascade reads a backlog from GitHub or GitLab, checks every issue against the
Theme → Epic → Feature → Story → Task model, proposes the fixes (missing sections drafted by a model,
metadata repaired, parents inferred, sizes estimated), and writes only what you approve to a sandbox
repository, never anywhere else.

It runs locally: a PowerShell launcher, one .NET library, and a browser UI on `127.0.0.1`. Nothing is
installed on the machine and nothing in the browser talks to the internet except through the engine.

## Quick start (GitHub + an OpenAI-compatible model)

```powershell
# one-time, on the build machine: .NET 8 SDK, Node.js 20+
build\Build-Cascade.ps1                       # tests and builds; bundle in dist\Cascade
copy config\cascade.env.example config\cascade.env
gh auth login                                 # or set GITHUB_TOKEN in config\cascade.env
.\Launch-Cascade.bat
```

In the browser:

1. **Settings** → paste your OpenAI API key (held in memory; tick *remember* to keep it in the
   git-ignored `config\cascade.env`) and test the model.
2. **Sandbox** → seed 100 items: about 80 are complete, 20 carry one of 21 kinds of gap.
3. **Connection** → synchronize the sandbox.
4. **Overview** shows what needs doing; **Review** lists every gap with the path that fixes it; select
   rows and use the bulk bar, or go to **Proposals** and run *Repair metadata*, *Draft missing
   sections*, *Propose parents*, *Propose sizes*.
5. **Proposals** → open a row to read the before/after diff, approve, then *Apply to sandbox*.
6. **Sandbox** → delete the run when done.

## On a locked-down workstation (GitLab)

Copy `dist\Cascade` across, set `CASCADE_PROVIDER=gitlab`, `GITLAB_URL` and the project ids in
`config\cascade.env` (see the example file), and run `Launch-Cascade.bat`. If the vault bridge is
configured, it asks for CAC + TOTP in the console once; the console then hides. The source project is
read-only; only the sandbox project can be written.

## What it does

| Page | Purpose |
|---|---|
| Overview | The cascade of levels, items needing review, sizing coverage, points by theme and sprint, next actions |
| Hierarchy | Keyboard-navigable tree and graph of the declared chain; each item's definition, meta and findings; in-place edits queued as proposals |
| Review | Every item against its definition of ready and the topology; bulk actions on selected rows; Markdown or CSV review pack |
| Proposals | Drafted sections, metadata repairs, parent links and sizes with a diff per row; approve, reject, apply to the sandbox |
| Sandbox | Seed a deterministic synthetic hierarchy with a chosen share of gaps; delete it by run |
| Connection | Test the token; take a snapshot of the source or sandbox |
| Diagnostics | Engine log, every write attempt, background jobs |
| Settings | Model API key and model name; profile; label filter; the configured repositories (read-only) |

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
Every proposal, including a hand edit, is previewed as a diff and goes through the same gateway.
Deletes only touch items labelled `cascade-synthetic`, unless you type the wipe phrase. Every write is
audited. Provider tokens stay in process memory and never reach the browser, logs or transcripts; the
model key may be entered in the browser but only ever goes to the endpoint named in `config\cascade.env`.
