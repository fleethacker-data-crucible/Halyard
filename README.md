<p align="center"><img src="assets/brand/emblem-light.png" width="280" alt="Halyard"></p>

# Halyard

A product owner's assistant that works a backlog upward to leadership and downward to stories and tasks.

Halyard reads one or more repositories on GitHub or GitLab, checks every issue against the Theme → Epic →
Feature → Story → Task model, and proposes the work a product owner would do: raw tickets triaged into the
hierarchy (intake), missing sections drafted, metadata repaired, parents inferred, sizes estimated, skills
tagged, sprints planned against team capacity and skills, a portfolio vision drafted for leadership, and a
one-page leadership dashboard that can go out by email every week. It writes only what you approve, and
only to a sandbox repository ([Halyard Ballast](https://github.com/sun-of-anton/Halyard-Ballast) for the
owner's own runs), never anywhere else.

It runs locally: a PowerShell launcher, one .NET library, and a browser UI on `127.0.0.1`. Nothing is
installed on the machine and nothing in the browser talks to the internet except through the engine.

## The name

A halyard is the line that raises and lowers a ship's sails; Halyard works upward to leadership and
downward to stories and tasks. Its sandbox, Halyard Ballast, is the sand carried in the hold to keep the
ship steady. Formerly Cascade. Upgrading an existing Cascade install needs no edits first: see
[Upgrading from Cascade](docs/OPERATIONS.md#upgrading-from-cascade).

| Night watch (dark) | Chart room (light) |
| --- | --- |
| ![Halyard overview in the Night watch theme](docs/screenshots/overview-night-watch.png) | ![Halyard overview in the Chart room theme](docs/screenshots/overview-chart-room.png) |

The overview of a synthetic 100-item backlog seeded into an offline fake GitHub
(`tests/e2e/Capture-Screenshots.ps1` regenerates both pictures with mock data only).

## Quick start (GitHub + an OpenAI-compatible model)

```powershell
# one-time, on the build machine: .NET 8 SDK, Node.js 20+
build\Build-Halyard.ps1                       # tests and builds; bundle in dist\Halyard
copy config\halyard.env.example config\halyard.env
gh auth login                                 # or set GITHUB_TOKEN in config\halyard.env
.\Launch-Halyard.bat
```

Later updates: double-click `Update-Halyard.bat` (pull, build, launch).

In the browser:

1. **Settings** → paste your OpenAI API key (held in memory; tick *remember* to keep it in the
   git-ignored `config\halyard.env`) and test the model.
2. **Sandbox** → seed 100 items: about 80 are complete, 20 carry one of 21 kinds of gap.
3. **Connection** → synchronize the sandbox.
4. **Overview** shows what needs doing; **Review** lists every gap with the path that fixes it; select
   rows and use the bulk bar, or go to **Proposals** and run *Repair metadata*, *Draft missing
   sections*, *Propose parents*, *Propose sizes*.
5. **Proposals** → open a row to read the before/after diff, approve, then *Apply to sandbox*.
6. **Sandbox** → delete the run when done.

## On a locked-down workstation (GitLab)

Copy `dist\Halyard` across, set `HALYARD_PROVIDER=gitlab`, `GITLAB_URL` and the project ids in
`config\halyard.env` (see the example file), and run `Launch-Halyard.bat`. If the vault bridge is
configured, it asks for CAC + TOTP in the console once; the console then hides. The source project is
read-only; only the sandbox project can be written.

## What it does

| Page | Purpose |
|---|---|
| Overview | The levels of the hierarchy, items needing review, sizing coverage, points by theme and sprint, next actions |
| Leadership | Feature % complete, velocity, ready backlog, capacity used, open blockers; downloadable HTML report |
| Vision | Portfolio vision drafted from the backlog (deterministic or model): themes, priorities, risks, next quarter; theme proposals |
| Intake | Raw tickets from the source repositories copied into the sandbox as classified work items with a source link |
| Hierarchy | Keyboard-navigable tree and graph of the declared chain; each item's definition, meta and findings; in-place edits queued as proposals |
| Review | Every item against its definition of ready and the topology; bulk actions on selected rows; Markdown or CSV review pack |
| Proposals | Drafted sections, metadata repairs, parent links, sizes, skills, sprint plans and theme links with a diff per row; approve, reject, apply to the sandbox |
| Plan | The next sprints filled from ready stories within committed capacity, owners by skill match, skill gaps; becomes proposals |
| Sandbox | Seed a deterministic synthetic hierarchy with a chosen share of gaps; delete it by run |
| Connection | Test the token; take a snapshot of the source or sandbox |
| Diagnostics | Engine log, every write attempt, background jobs |
| Settings | Model API key and model name; profile; label filter; the configured repositories (read-only) |

PowerShell equivalents: `Invoke-SandboxSeed.ps1`, `Invoke-SandboxTeardown.ps1`, `Send-HalyardReport.ps1`
(leadership dashboard by Outlook or SMTP), `Register-HalyardReportSchedule.ps1` (weekly task), and the
`Halyard` module (`Get-HalyardWorkItem`, `Test-HalyardConnection`, `New-HalyardSyntheticCatalog`).

## Team, skills and Shellback

`config/team.json` holds teams, people, points per sprint and the sprint calendar. `config/skills.json`
is the shared skill taxonomy, using the same ids as [Shellback](https://github.com/sun-of-anton/Shellback),
so exported Shellback peer cards dropped into `config/shellback/` give each person their consented skills and
confidence without any translation. Halyard only reads those cards; Shellback stays the owner of people,
skills and consent.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): stack, components, write safety, local security
- [docs/OPERATIONS.md](docs/OPERATIONS.md): running, configuration, sandbox lifecycle, troubleshooting
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): building, testing, the offline end-to-end loop, CI
- [docs/WORK_ITEM_MODEL.md](docs/WORK_ITEM_MODEL.md): the hierarchy, halyard-meta, definition of ready, PfW/DWI
- [docs/LOCAL_APP_LIFECYCLE.md](docs/LOCAL_APP_LIFECYCLE.md): the engine stops with its browser tab; the standard every local app in this family follows
- [ROADMAP.md](ROADMAP.md)

## Safety in one paragraph

Writes default to the sandbox repository only. Production needs `HALYARD_WRITE_MODE=production`,
`HALYARD_ALLOW_PRODUCTION=1` and an explicit opt-in in code, and the browser can change none of them.
Every proposal, including a hand edit, is previewed as a diff and goes through the same gateway.
Deletes only touch items labelled `halyard-synthetic`, unless you type the wipe phrase. Every write is
audited. Provider tokens stay in process memory and never reach the browser, logs or transcripts; the
model key may be entered in the browser but only ever goes to the endpoint named in `config\halyard.env`.
