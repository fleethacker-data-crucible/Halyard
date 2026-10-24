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
   Hand edits from the **Hierarchy** item panel (points, effort, parent) become pre-approved proposals.
4. On **Proposals**, open a row to read the before/after diff and the definition-of-ready state after
   applying. Approve or reject; *Apply approved to sandbox*. Refused for batches made from a
   non-sandbox repository.
5. Re-synchronize to see the result; the gaps close in **Review** and **Overview**.

The synthetic sandbox seeds this workflow: with *gaps* on, about a fifth of the items carry one of 21
kinds of problem (missing sections, missing or invalid sizes, missing or wrong parents, no metadata, ...),
each marked with the review label, and the seed manifest records which codes the audit must raise.

## Where things are

| Path | Contents |
|---|---|
| `output\snapshots\` | Last 10 snapshots (JSON) |
| `output\proposals\` | Proposal batches and their decisions |
| `output\seeds\` | Seed run manifests |
| `output\llm_transcripts\` | Redacted model requests and responses (`CASCADE_LLM_TRANSCRIPTS=0` turns off) |
| `logs\cascade-*.jsonl` | Engine log, one file per run |

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Cascade.Core.dll was not found" | Run from a built bundle, or build with `build\Build-Cascade.ps1`. |
| "Windows blocked Cascade.Core.dll" | Application control rejects the unsigned DLL. Ask IT to sign or allow it. |
| Script will not run at all | Execution policy enforced by Group Policy. `-ExecutionPolicy Bypass` cannot override that; use the IT-approved signed-script route. |
| Console stays open after launch | ARC did not unlock; read the message in the console. |
| `PROVIDER_NOT_READY` in the UI | No token for the configured provider. See Configurations above. |
| `AUTH_REJECTED` / `FORBIDDEN` | Token expired or lacks access to the repository. |
| `WRITE_BLOCKED` | The write policy refused the target. Check `CASCADE_SANDBOX_REPO`. |
| Page says "Open Cascade from the launcher" | The tab has no session token. Relaunch. |
