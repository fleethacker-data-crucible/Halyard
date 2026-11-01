<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/emblem-dark-transparent.png">
    <img src="assets/brand/emblem-light-transparent.png" width="280" alt="Halyard emblem: a mast with a pulley block and a line of signal flags, inside a brass compass ring">
  </picture>
</p>

# Halyard

A product owner's assistant that works a backlog upward to leadership and downward to stories and tasks.

Halyard reads one or more repositories on GitHub or GitLab, checks every issue against the Theme → Epic →
Feature → Story → Task model, and proposes the work a product owner would do: raw tickets triaged into the
hierarchy (intake), missing sections drafted, metadata repaired, parents inferred, sizes estimated, skills
tagged, sprints planned against team capacity and skills, a portfolio vision drafted for leadership, and a
one-page leadership dashboard that can go out by email every week. It writes only what you approve, and
only to a sandbox repository ([Halyard Ballast](https://github.com/fleethacker-data-crucible/Halyard-Ballast) for the
owner's own runs), never anywhere else.

It runs locally: a PowerShell launcher, one .NET library, and a browser UI on `127.0.0.1`. Nothing is
installed on the machine and nothing in the browser talks to the internet except through the engine.

## The name

A halyard is the line that raises and lowers a ship's sails; Halyard works upward to leadership and
downward to stories and tasks. Its sandbox, Halyard Ballast, is the sand carried in the hold to keep the
ship steady.

| Night watch (dark) | Chart room (light) |
| --- | --- |
| ![Halyard overview in the Night watch theme](docs/screenshots/overview-night-watch.png) | ![Halyard overview in the Chart room theme](docs/screenshots/overview-chart-room.png) |

The overview of a synthetic 100-item backlog seeded into an offline fake GitHub
(`tests/e2e/Capture-Screenshots.ps1` regenerates both pictures with mock data only).

## Quick start (GitHub + an OpenAI-compatible model)

> **Intelligence router:** the model endpoint now defaults to the Fleet intelligence router, [Fairlead](https://github.com/fleethacker-data-crucible/Fairlead), at `http://127.0.0.1:8776/v1/chat/completions`; no external model endpoint is built in. Set `LLM_URL` to use another gateway and `LLM_TOKEN` to Halyard's client key from Fairlead. The router recipe is tested against a fake only, not run against a live Fairlead. The GitHub API host (default `https://api.github.com`) is unchanged. See [TechStack](TechStack), "Intelligence router".

On the workstation (no .NET SDK or Node.js needed):

1. Download `Halyard-<version>.zip` from [Releases](https://github.com/fleethacker-data-crucible/Halyard/releases) and
   unzip it.
2. Copy `config\halyard.env.example` to `config\halyard.env` and set `GITHUB_TOKEN` in it (on a personal
   machine with the GitHub CLI, `gh auth login` also works; the launcher reads `gh auth token` only if `gh`
   is installed).
3. Double-click `Launch-Halyard.bat`.

The engine ships as C# source, not as a DLL: the workstation blocks loading custom DLL files, so
`Launch-Halyard.bat` compiles `src\Halyard.Core` in memory with Windows PowerShell 5.1 (`Add-Type`, C# 5,
framework assemblies only) on every launch. That takes a few seconds. A git checkout still needs the browser
UI built. To build from source (.NET 8 SDK, Node.js 20+): `build\Build-Halyard.ps1` tests and builds, leaving
the bundle in `dist\Halyard`; later, double-click `Update-Halyard.bat` (pull, build, launch; development PC
only, it needs git, the .NET SDK and Node). Running the *Release* workflow on main (or pushing a tag
`v<version>`) publishes a new bundle to Releases. To update the workstation, download the new
`Halyard-<version>.zip` and extract it over the old folder (keep `config\halyard.env` and `output\`); nothing
is built there.

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
configured, it asks for a smart card PIN + TOTP in the console once; the console then hides. The source project is
read-only; only the sandbox project can be written.

## What it does

| Page | Purpose |
|---|---|
| Overview | The levels of the hierarchy, items needing review, sizing coverage, points by theme and sprint, next actions |
| Leadership | Feature % complete, velocity, ready backlog, capacity used, open blockers; downloadable HTML report |
| Vision | Portfolio vision drafted from the backlog (deterministic or model): themes, priorities, risks, next quarter; theme proposals |
| Intake | Raw tickets from the source repositories copied into the sandbox as categorized work items with a source link |
| Hierarchy | Keyboard-navigable tree and graph of the declared chain; each item's definition, meta and findings; in-place edits queued as proposals |
| Review | Every item against its definition of ready and the topology; bulk actions on selected rows; Markdown or CSV review pack |
| Proposals | Drafted sections, metadata repairs, parent links, sizes, skills, sprint plans and theme links with a diff per row; approve, reject, apply to the sandbox |
| Plan | The next sprints filled from ready stories within committed capacity, owners by skill match, skill gaps; becomes proposals |
| Sandbox | Seed a deterministic synthetic hierarchy with a chosen share of gaps; delete it by run |
| Connection | Test the token; take a snapshot of the source or sandbox |
| Diagnostics | Engine log, every write attempt, background jobs |
| Settings | Model API key and model name; profile; a model endpoint set from a pasted curl command plus token (for testing gateways); label filter; the configured repositories (read-only) |

PowerShell equivalents: `Invoke-SandboxSeed.ps1`, `Invoke-SandboxTeardown.ps1`, `Send-HalyardReport.ps1`
(leadership dashboard by Outlook or SMTP), `Register-HalyardReportSchedule.ps1` (weekly task), and the
`Halyard` module (`Get-HalyardWorkItem`, `Test-HalyardConnection`, `New-HalyardSyntheticCatalog`).

## Team, skills and Shellback

`config/team.json` holds teams, people, points per sprint and the sprint calendar. `config/skills.json`
is the shared skill taxonomy, using the same ids as [Shellback](https://github.com/fleethacker-data-crucible/Shellback),
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
model key may be entered in the browser but only ever goes to the endpoint named in `config\halyard.env`;
an endpoint set from a pasted curl command gets only the token typed in for it (DPAPI-encrypted if remembered).
