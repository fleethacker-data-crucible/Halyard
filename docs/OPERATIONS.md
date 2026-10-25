# Operations

## Running Cascade

1. Copy the release bundle (`dist\Cascade`, produced by `build\Build-Cascade.ps1`) to the machine.
2. Copy `config\cascade.env.example` to `config\cascade.env` and set the provider and repositories.
3. Double-click `Launch-Cascade.bat`.

The launcher loads settings, unlocks the vault bridge (ARC) in its console when the provider is GitLab
and it is configured, starts the engine
on `127.0.0.1` (port 8773, or a free one if taken), opens the browser with a one-time session link and
hides the console. **End session** in the sidebar stops everything.

| Option | Effect |
|---|---|
| `Launch-Cascade.bat --console` | Keep the console visible (diagnosing startup or ARC). |
| `--no-arc` | Never load ARC; use tokens from `cascade.env` (local development only). |
| `--arc` | Load ARC even for GitHub (reads `CASCADE_ARC_ITEM_GitHub`). |
| `--no-browser` | Start the engine without opening a browser. |

If you close the browser tab, relaunch Cascade. The session link is opened once and not shown again, so
nothing else on the machine can pick it up. With `--no-browser` the console prints it instead.

## Configurations

**Personal machine, GitHub sandbox**

```
CASCADE_PROVIDER=github
CASCADE_SOURCE_REPO=sun-of-anton/cascade-sandbox
CASCADE_SANDBOX_REPO=sun-of-anton/cascade-sandbox
```

Token: `gh auth login` once (the launcher reads `gh auth token`), or `GITHUB_TOKEN=` in `cascade.env`.
The token needs `repo` scope; deleting issues needs admin rights on the repository.

**Several source repositories** (the usual case at work: issues spread over many projects)

```
CASCADE_SOURCE_REPO=org/platform,org/web,org/ops      # or GitLab ids: 1234,5678,group/project
CASCADE_SANDBOX_REPO=org/cascade-sandbox
```

Every source is read-only. Connection synchronizes one source, the sandbox, or all sources at once;
items keep their repository, and the Hierarchy and Review pages show it. Proposals are only made for
sandbox items; Intake is how source tickets become sandbox copies.

**Team, skills and Synapse** (files, not variables)

| File | Contents |
|---|---|
| `config\team.json` | Sprint calendar (`sprintStart`, length, numbering), teams with `pointsPerSprint` and the committed / allocated / experiment split, people with team, skills and an optional Synapse card name |
| `config\skills.json` | Skill taxonomy; the ids are Synapse's so cards need no translation |
| `config\synapse\*.json` | Exported Synapse peer cards. Only cards with consent are read; skills and confidence (0–4) and availability are merged into the matching person by name |

Leadership, Plan and the skill tagger read these on every request, so editing the files needs no restart.

**Model**

```
CASCADE_LLM_PROFILE=auto           # auto | openai | databricks
# LLM_URL=https://api.openai.com/v1/chat/completions   (or a base such as https://gateway.example.com/v1)
# LLM_MODEL=gpt-4o-mini
# LLM_TOKEN=                                            (or OPENAI_API_KEY)
```

The key and model can also be entered on the **Settings** page. The key stays in memory for the session;
with *remember* ticked it is written to `config\cascade.env` (git-ignored) so the next launch has it. The
endpoint URL can only be set in the file, so a key entered in the browser never goes anywhere else.

**Workstation, self-hosted GitLab**

```
CASCADE_PROVIDER=gitlab
GITLAB_URL=https://gitlab.example.com
CASCADE_SOURCE_REPO=1234            # read-only source backlog
CASCADE_SANDBOX_REPO=5678           # the only writable project
CASCADE_PROTECTED_REPOS=            # optional extra never-write list
ARC_PATH=C:\path\to\arc              # optional vault bridge
CASCADE_ARC_ITEM_GitLab=<vault item>  # -> GITLAB_TOKEN
CASCADE_ARC_ITEM_Llm=<vault item>     # -> LLM_TOKEN
```

With this configuration the source project is read-only and protected; only the sandbox can be written.
Unset `CASCADE_ARC_ITEM_*` names are skipped, so the bridge only reads what you name.

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
done and why), velocity over the last six sprints, the ready backlog against velocity, capacity used in
the current sprint, open blockers with their business-day age (escalated past five days), and points by
theme. *Download report* saves the same page as one self-contained HTML file, built from the latest
sandbox snapshot. `GET /api/leadership/report.html` returns it too.

To send it by email every week without a browser:

```powershell
scripts\Send-CascadeReport.ps1 -To lead@example.com -Via Outlook -Sync        # Outlook desktop (COM)
scripts\Send-CascadeReport.ps1 -To lead@example.com -Via Smtp -SmtpServer mail.example.com -From cascade@example.com -Sync
scripts\Send-CascadeReport.ps1 -Via File -OutFile output\reports\weekly.html   # just write the file
scripts\Register-CascadeReportSchedule.ps1 -To lead@example.com -Via Outlook -Day MON -Time 07:30
```

With `-Sync` the script takes a fresh snapshot first (`-SyncRepository source|sandbox`, source by
default), then renders and sends the report; the schedule script registers a weekly Windows scheduled
task (`-Remove` deletes it). Both read `config\cascade.env` and unlock the vault the same way the
launcher does.

## Intake: source tickets into the hierarchy

**Intake** lists raw issues from the source repositories that have no cascade-meta and are not yet
copied (a copy records `source_project`, `source_iid` and `source_url`, and the page de-duplicates on
them). *Preview* classifies each ticket as theme / epic / feature / story / task, deterministically or
with the model, and shows the structured draft (title, body sections, labels `cascade`,
`cascade-intake`, `type::…`, `refine::sandbox-review`). *Run* creates the chosen copies in the sandbox
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

## Updating Cascade

Double-click `Update-Cascade.bat`: it pulls the latest code, builds (stopping any engine or PowerShell
window that still holds `Cascade.Core.dll`, and saying which), and launches. If the build cannot stop a
holder because it was opened as administrator, it names the PID and asks you to close that window.

## Stopping Cascade

The engine stops itself when its browser tab closes (heartbeat; about 20 seconds, never during a running
job), or at once from **End session** at the bottom of the navigation. `scripts\Stop-Cascade.ps1` stops
every engine on the machine. Launching while one is running asks whether to replace it; `--stay` keeps an
engine alive after the tab closes. The pattern is written up as a standard in
[LOCAL_APP_LIFECYCLE.md](LOCAL_APP_LIFECYCLE.md).

## Review workflow

1. **Connection** → Test connection → Synchronize (source or sandbox, optionally one label).
2. **Overview** shows the next actions; **Review** lists every item with its gaps and the path that fixes
   them (*Repair meta*, *Find parent*, *Draft sections*, *Size*). Nothing is written.
3. Make proposals, either for selected rows from the bulk bar on **Review**, or for everything from
   **Proposals** → *New analysis*:
   - **Repair metadata** (deterministic): builds or completes the cascade-meta block from labels, the
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
| `output\reports\` | Leadership reports written by `Send-CascadeReport.ps1 -Via File` |
| `output\llm_transcripts\` | Redacted model requests and responses (`CASCADE_LLM_TRANSCRIPTS=0` turns off) |
| `logs\cascade-*.jsonl` | Engine log, one file per run |

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Cascade.Core.dll was not found" | Run from a built bundle, or build with `build\Build-Cascade.ps1`. |
| "Windows blocked Cascade.Core.dll" | Application control rejects the unsigned DLL. Ask IT to sign or allow it. |
| Script will not run at all | Execution policy enforced by Group Policy. `-ExecutionPolicy Bypass` cannot override that; use the IT-approved signed-script route. |
| Console stays open after launch | ARC did not unlock; read the message in the console. |
| "Cascade is already running" at launch | An engine from an earlier session is still up. Answer Y to replace it, or `--stay` was used earlier; `scripts\Stop-Cascade.ps1` stops everything. |
| Build fails: `Cascade.Core.dll` "being used by another process" | An engine is running. The build script now stops it first; if it could not (started from an elevated window), run `scripts\Stop-Cascade.ps1` from such a window. |
| Engine stopped while I was away | By design: it stops itself about 20 s after its browser tab closes (never during a job). Launch with `--stay` or set `CASCADE_AUTO_STOP=0` to keep it running. |
| `PROVIDER_NOT_READY` in the UI | No token for the configured provider. See Configurations above. |
| `AUTH_REJECTED` / `FORBIDDEN` | Token expired or lacks access to the repository. |
| `WRITE_BLOCKED` | The write policy refused the target. Check `CASCADE_SANDBOX_REPO`. |
| Page says "Open Cascade from the launcher" | The tab has no session token. Relaunch. |
