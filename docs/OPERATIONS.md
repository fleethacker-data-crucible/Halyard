# Operations

## Running Halyard

1. Download `Halyard-<version>.zip` from [Releases](https://github.com/fleethacker-data-crucible/Halyard/releases) and unzip it
   on the machine (or copy `dist\Halyard`, produced by `build\Build-Halyard.ps1`).
2. Copy `config\halyard.env.example` to `config\halyard.env` and set the provider and repositories.
3. Double-click `Launch-Halyard.bat`.

The launcher loads settings, unlocks the vault bridge (Lazarette) in its console when the provider is GitLab
and it is configured, starts the engine
on `127.0.0.1` (port 8773, or a free one if taken), opens the browser with a one-time session link and
hides the console. **End session** in the sidebar stops everything.

| Option | Effect |
|---|---|
| `Launch-Halyard.bat --console` | Keep the console visible (diagnosing startup or Lazarette). |
| `--no-lazarette` | Never load Lazarette; use tokens from `halyard.env` (local development only). |
| `--lazarette` | Load Lazarette even for GitHub (reads `HALYARD_LAZARETTE_ITEM_GitHub`). |
| `--no-browser` | Start the engine without opening a browser. |

If you close the browser tab, relaunch Halyard. The session link is opened once and not shown again, so
nothing else on the machine can pick it up. With `--no-browser` the console prints it instead.

## Configurations

**Personal machine, GitHub sandbox**

```
HALYARD_PROVIDER=github
HALYARD_SOURCE_REPO=fleethacker-data-crucible/Halyard-Ballast
HALYARD_SANDBOX_REPO=fleethacker-data-crucible/Halyard-Ballast
```

Token: `GITHUB_TOKEN=` in `halyard.env`. On a personal machine with the GitHub CLI installed, `gh auth login`
once also works (the launcher reads `gh auth token` when `gh` is present and no token is set); the workstation
has no `gh`, so use `GITHUB_TOKEN` (or Lazarette) there.
The token needs `repo` scope; deleting issues needs admin rights on the repository.

**Several source repositories** (the usual case at work: issues spread over many projects)

```
HALYARD_SOURCE_REPO=org/platform,org/web,org/ops      # or GitLab ids: 1234,5678,group/project
HALYARD_SANDBOX_REPO=org/halyard-ballast
```

Every source is read-only. Connection synchronizes one source, the sandbox, or all sources at once;
items keep their repository, and the Hierarchy and Review pages show it. Proposals are only made for
sandbox items; Intake is how source tickets become sandbox copies.

**Team, skills and Shellback** (files, not variables)

| File | Contents |
|---|---|
| `config\team.json` | Sprint calendar (`sprintStart`, length, numbering), teams with `pointsPerSprint` and the committed / allocated / experiment split, people with team, skills and an optional Shellback card name |
| `config\skills.json` | Skill taxonomy; the ids are Shellback's so cards need no translation |
| `config\shellback\*.json` | Exported Shellback peer cards. Only cards with consent are read; skills and confidence (0–4) and availability are merged into the matching person by name |

Leadership, Plan and the skill tagger read these on every request, so editing the files needs no restart.

**Model**

```
HALYARD_LLM_PROFILE=auto           # auto | openai | databricks | custom
# LLM_URL=https://api.openai.com/v1/chat/completions   (or a base such as https://gateway.example.com/v1)
# LLM_MODEL=gpt-4o-mini
# LLM_TOKEN=                                            (or OPENAI_API_KEY)
```

The key and model can also be entered on the **Settings** page. The key stays in memory for the session;
with *remember* ticked it is written to `config\halyard.env` (git-ignored) so the next launch has it. The
endpoint URL of these profiles can only be set in the file, so a key entered there never goes anywhere else.

**Testing a gateway endpoint: Settings > Set from curl**

To try whatever endpoint a gateway documents, paste its curl command into *Set from curl* and the token into the
token field next to it, then press *Set from curl* and *Test*. Halyard reads the URL (the first argument or
`--url`), the body's `model` and the body format: `input` is the OpenAI Responses API (sent as
`{model, input, max_output_tokens}`, no streaming; the answer is read from the `output_text` items), `messages`
is chat completions, and a `/serving-endpoints/<name>/invocations` URL is Databricks. Other non-secret `-H`
headers are kept and sent; `Authorization`, `Content-Type` and anything that looks like a credential are
dropped. The profile switches to `custom`; the OpenAI and Databricks profiles and their keys are untouched.

- The token comes only from the token field. A token written into the curl command is not kept (keep
  placeholders such as `$AI_ENDPOINT_TOKEN` in the command). It is sent only to the endpoint it was entered
  with: changing to another host needs the token again.
- The URL must be `https://` (plain `http://` only for `localhost`/`127.0.0.1`), with no user name, password or
  credential in the query string. A placeholder left in the URL (`$VAR`, `${VAR:-x}`, `<...>`) is refused.
- With *remember* ticked the endpoint goes to `config\halyard.env` and the token is stored encrypted with
  Windows DPAPI for your account (`dpapi:...`), readable only by you on this machine:

```
HALYARD_LLM_PROFILE=custom
HALYARD_LLM_CUSTOM_URL=https://gateway.example.invalid/serving-endpoints/responses
HALYARD_LLM_CUSTOM_MODEL=example-model
HALYARD_LLM_CUSTOM_FORMAT=responses          # responses | chat | databricks
# HALYARD_LLM_CUSTOM_HEADERS={"X-Team":"delivery"}
# HALYARD_LLM_CUSTOM_TOKEN=dpapi:...          (written by Settings; a plain token also works here)
```

**Workstation, self-hosted GitLab**

```
HALYARD_PROVIDER=gitlab
GITLAB_URL=https://gitlab.example.com
HALYARD_SOURCE_REPO=1234            # read-only source backlog
HALYARD_SANDBOX_REPO=5678           # the only writable project
HALYARD_PROTECTED_REPOS=            # optional extra never-write list
LAZARETTE_PATH=C:\path\to\Lazarette  # optional vault bridge (ARC_PATH also works)
HALYARD_LAZARETTE_ITEM_GitLab=<vault item>  # -> GITLAB_TOKEN
HALYARD_LAZARETTE_ITEM_Llm=<vault item>     # -> LLM_TOKEN
```

With this configuration the source project is read-only and protected; only the sandbox can be written.
Unset `HALYARD_LAZARETTE_ITEM_*` names are skipped, so the bridge only reads what you name.

## Sandbox lifecycle

From the **Sandbox** page, or from PowerShell:

```powershell
.\scripts\Invoke-SandboxSeed.ps1 -Total 100 -RunId demo-1          # create the synthetic hierarchy
.\scripts\Invoke-SandboxSeed.ps1 -Total 100 -IncludeDefects         # 80% complete, 20% with gaps (-DefectRate 0.3 for 30%)
.\scripts\Invoke-SandboxTeardown.ps1 -RunId demo-1 -DryRun          # list what would go
.\scripts\Invoke-SandboxTeardown.ps1 -RunId demo-1                  # delete that run
.\scripts\Invoke-SandboxTeardown.ps1 -AllSynthetic                  # delete every synthetic item
.\scripts\Invoke-SandboxTeardown.ps1 -Everything -ConfirmText 'DELETE owner/sandbox-repo'
```

GitHub's issue listing can lag a few seconds behind creation, so synchronize again if a fresh run looks short. Deleting issues on GitHub uses its GraphQL API and needs admin rights on the repository.

Seeding creates parents before children, writes `parent:` ids into meta, adds native sub-issue links on
GitHub, and saves a manifest to `output\seeds\`. On GitHub it paces creates (~0.9 s each) to stay under
secondary rate limits, so 100 items take about two minutes. Deletion is permanent on both hosts.

## Weekly leadership report

**Leadership** in the browser shows feature % complete with a status (on track / at risk / parked /
done and why), then four charts: feature status by sprint (past sprints reconstructed from story sprints,
closures, blockers and targets; the current one is the live table), velocity with this sprint's plan
outlined and the three-sprint average, capacity per team as a meter with the committed/allocated
boundaries, and points by theme. Then the ready backlog against velocity and open blockers with their
business-day age (escalated past five days). The emailed report carries the same charts drawn as HTML
tables, which Outlook renders, and the Halyard mark in the header. *Download report* saves the same page as one self-contained HTML file, built from the latest
sandbox snapshot. `GET /api/leadership/report.html` returns it too.

To send it by email every week without a browser:

```powershell
scripts\Send-HalyardReport.ps1 -To lead@example.com -Via Outlook -Sync        # Outlook desktop (COM)
scripts\Send-HalyardReport.ps1 -To lead@example.com -Via Smtp -SmtpServer mail.example.com -From halyard@example.com -Sync
scripts\Send-HalyardReport.ps1 -Via File -OutFile output\reports\weekly.html   # just write the file
scripts\Register-HalyardReportSchedule.ps1 -To lead@example.com -Via Outlook -Day MON -Time 07:30
```

With `-Sync` the script takes a fresh snapshot first (`-SyncRepository source|sandbox`, source by
default), then renders and sends the report; the schedule script registers a weekly Windows scheduled
task for the current user with `schtasks /Create` (`-Remove` deletes it). Both read `config\halyard.env` and
unlock the vault the same way the launcher does. The schedule is opt-in: nothing registers it unless you run
the script, and workstation policy may block creating scheduled tasks; then send the report by hand instead.

## Intake: source tickets into the hierarchy

**Intake** lists raw issues from the source repositories that have no halyard-meta and are not yet
copied (a copy records `source_project`, `source_iid` and `source_url`, and the page de-duplicates on
them). *Preview* classifies each ticket as theme / epic / feature / story / task, deterministically or
with the model, and shows the structured draft (title, body sections, labels `halyard`,
`halyard-intake`, `type::…`, `refine::sandbox-review`). *Run* creates the chosen copies in the sandbox
through the write gateway; the source is never touched. Manifests are kept in `output\intakes\`.

## Vision and planning

**Vision** drafts a portfolio vision from the backlog: themes with their epics, features and points,
priorities, risks (blockers, parked features, unsized work), and a next-quarter outlook. The
deterministic summary needs no model; *Draft with model* writes the narrative and is validated against
the backlog (every item it names must exist). Drafts are saved under `output\vision\` and each one can
produce *theme link* proposals for epics that do not yet name a theme.

**Plan** fills the next sprints: ready stories (definition of ready met, sized, not blocked) are ordered
by the target month of their feature and whether the feature has started, then placed first-fit into
each team's committed share of `pointsPerSprint`. An owner is suggested where someone on the team has the
story's skills with room left; stories nobody on the team can cover are listed as skill gaps. *Propose*
turns the plan into sprint-plan proposals, reviewed and applied like any other.

## Updating Halyard

On the workstation: download the new `Halyard-<version>.zip` from Releases, stop Halyard (*End session*, or
`scripts\Stop-Halyard.ps1`), and extract the zip over the old folder, keeping `config\halyard.env` and
`output\`. Nothing is built on the workstation; the engine is compiled from the bundled source at launch.

On a development PC (git, .NET 8 SDK and Node.js installed): double-click `Update-Halyard.bat`. It pulls the
latest code, builds (stopping any running engine first, and saying which), and launches. If the build cannot
stop an engine because it was opened as administrator, it names the PID and asks you to close that window.
`Update-Halyard.bat` is not in the release bundle.

### Upgrading from the previous name

Halyard was renamed from a previous name. What an installed copy needs on its first launch after the rename still works:

- Legacy launch and update aliases forward to `Launch-Halyard.bat` and
  `Update-Halyard.bat`; existing shortcuts keep working. The release bundle includes the launch alias.
- On the first launch, `config\cascade.env` is copied once to `config\halyard.env` with the variable names updated
  (`CASCADE_*` to `HALYARD_*`, `CASCADE_ARC_ITEM_*` to `HALYARD_LAZARETTE_ITEM_*`, `CASCADE_USE_ARC` to
  `HALYARD_USE_LAZARETTE`, and `sun-of-anton/cascade-sandbox` to `fleethacker-data-crucible/Halyard-Ballast`). The old file is
  left untouched and is not read again.
- `CASCADE_*` environment variables set outside the env file still work for this release when their
  `HALYARD_*` name is not set. The fallback will be removed in the next release.
- Settings the browser remembered (tree state, last view) are copied from the old `cascade.` keys to `halyard.`
  once; the old keys are left in place.
- Stop any engine started before the update first (End session in its tab, or close its console): the stop
  script and the build only look for Halyard engines.
- Issue labels, the meta block and the mirror heading use the Halyard names (`halyard`, `halyard-synthetic`,
  `halyard-run::*`, `halyard-intake`, `~~~halyard-meta`, `### Halyard`). Items created under the previous name are not
  recognised; empty the sandbox with the previous build before upgrading, or delete those items by hand.
- Peer cards go in `config\shellback\`, and `team.json` names them with `shellbackCard`.
- A weekly report task registered before the rename runs `scripts\Send-CascadeReport.ps1`, which no longer exists.
  Re-register it with `Register-HalyardReportSchedule.ps1` and remove the old task with
  `Register-HalyardReportSchedule.ps1 -Remove -TaskName 'Cascade leadership report'`.

## Stopping Halyard

The engine stops itself when its browser tab closes (heartbeat; about 20 seconds, never during a running
job), or at once from **End session** at the bottom of the navigation. `scripts\Stop-Halyard.ps1` stops
every engine on the machine. Launching while one is running asks whether to replace it; `--stay` keeps an
engine alive after the tab closes. The pattern is written up as a standard in
[LOCAL_APP_LIFECYCLE.md](LOCAL_APP_LIFECYCLE.md).

## Review workflow

1. **Connection** → Test connection → Synchronize (source or sandbox, optionally one label).
2. **Overview** shows the next actions; **Review** lists every item with its gaps and the path that fixes
   them (*Repair meta*, *Find parent*, *Draft sections*, *Size*). Nothing is written.
3. Make proposals, either for selected rows from the bulk bar on **Review**, or for everything from
   **Proposals** → *New analysis*:
   - **Repair metadata** (deterministic): builds or completes the halyard-meta block from labels, the
     title and TBD placeholders; invalid points become TBD; wrong-level sizing keys are removed.
   - **Propose parents** (deterministic): evidence-ranked candidates.
   - **Draft missing sections** (model): acceptance criteria, user need, tasks, done-when, outcome,
     success picture, scope. Existing text is kept; every draft is re-checked against the definition of
     ready and flagged low-confidence if text was lost.
   - **Propose sizes** (model): points or effort, with the definability gate and policy clamps.
   - **Tag skills** (deterministic, or model): `skills` meta from the taxonomy, inherited from the parent
     and matched on names.
   Hand edits from the **Hierarchy** item panel (points, effort, parent) become pre-approved proposals.
4. On **Proposals**, open a row to read the before/after diff and the definition-of-ready state after
   applying. Approve or reject; *Apply approved to sandbox*, or *Apply all approved* across batches.
   A batch made by mistake can be deleted. Refused for batches made from a non-sandbox repository.
5. Every write (apply, intake, seed, delete) re-synchronizes the snapshot by itself as the last step of its
   job, so the gaps close in **Review** and **Overview** without a trip to **Connection**.

The synthetic sandbox seeds this workflow: with *gaps* on, about a fifth of the items carry one of 21
kinds of problem (missing sections, missing or invalid sizes, missing or wrong parents, no metadata, ...),
each marked with the review label, and the seed manifest records which codes the audit must raise.

## Where things are

| Path | Contents |
|---|---|
| `output\snapshots\` | Last 10 snapshots (JSON) |
| `output\proposals\` | Proposal batches and their decisions |
| `output\seeds\` | Seed run manifests |
| `output\intakes\` | Intake run manifests (source → sandbox copy) |
| `output\vision\` | Saved vision drafts |
| `output\reports\` | Leadership reports written by `Send-HalyardReport.ps1 -Via File` |
| `output\llm_transcripts\` | Redacted model requests and responses (`HALYARD_LLM_TRANSCRIPTS=0` turns off) |
| `logs\halyard-*.jsonl` | Engine log, one file per run |

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "The Halyard engine source folder ... was not found" | The bundle was not fully extracted (`src\Halyard.Core` is missing). Extract the whole `Halyard-<version>.zip` again. |
| "The Halyard engine source ... did not compile" | A source file is damaged or was edited with C# 6+ syntax or a non-framework reference. Extract the bundle again. |
| "Windows blocked compiling the Halyard engine" or "Constrained Language mode" | The policy does not allow in-memory `Add-Type` for this user. Ask IT; do not work around the policy. Halyard ships no DLL, so there is nothing to sign. |
| Launch takes a few seconds before the browser opens | Expected: the engine is compiled from source on every launch. The console (`--console`) and the diagnostics log show `Engine compiled from N source files in X ms`. |
| Script will not run at all | Execution policy enforced by Group Policy. `-ExecutionPolicy Bypass` cannot override that; use the IT-approved signed-script route. |
| Console stays open after launch | Lazarette did not unlock; read the message in the console. |
| "Halyard is already running" at launch | An engine from an earlier session is still up. Answer Y to replace it, or `--stay` was used earlier; `scripts\Stop-Halyard.ps1` stops everything. |
| Build says Halyard is still running | An engine is running. The build script stops it first; if it could not (started from an elevated window), run `scripts\Stop-Halyard.ps1` from such a window. |
| Engine stopped while I was away | By design: it stops itself about 20 s after its browser tab closes (never during a job). Launch with `--stay` or set `HALYARD_AUTO_STOP=0` to keep it running. |
| `PROVIDER_NOT_READY` in the UI | No token for the configured provider. See Configurations above. |
| `AUTH_REJECTED` / `FORBIDDEN` | Token expired or lacks access to the repository. |
| `WRITE_BLOCKED` | The write policy refused the target. Check `HALYARD_SANDBOX_REPO`. |
| Page says "Open Halyard from the launcher" | The tab has no session token. Relaunch. |
