# Operations

## Running Cascade

1. Copy the release bundle (`dist\Cascade`, produced by `build\Build-Cascade.ps1`) to the machine.
2. Copy `config\cascade.env.example` to `config\cascade.env` and set the provider and repositories.
3. Double-click `Launch-Cascade.bat`.

The launcher loads settings, unlocks ARC in its console when the provider is GitLab, starts the engine
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

**Workstation, web.git.mil**

```
CASCADE_PROVIDER=gitlab
GITLAB_URL=https://web.git.mil
CASCADE_SOURCE_REPO=16629
CASCADE_SANDBOX_REPO=52999
ARC_PATH=C:\Users\<you>\Projects\arc
```

Tokens come from the ARC items named by `CASCADE_ARC_ITEM_GitLab` (default `gitmil`), `CASCADE_ARC_ITEM_GenAi`
(`genapi`) and `CASCADE_ARC_ITEM_Grok` (`advana-api`). With this configuration the CBR (16629) is
read-only and protected; only 52999 can be written.

## Sandbox lifecycle

From the **Sandbox** page, or from PowerShell:

```powershell
.\scripts\Invoke-SandboxSeed.ps1 -Total 100 -RunId demo-1          # create the synthetic hierarchy
.\scripts\Invoke-SandboxSeed.ps1 -Total 100 -IncludeDefects         # with a few deliberate gaps for the audit
.\scripts\Invoke-SandboxTeardown.ps1 -RunId demo-1 -DryRun          # list what would go
.\scripts\Invoke-SandboxTeardown.ps1 -RunId demo-1                  # delete that run
.\scripts\Invoke-SandboxTeardown.ps1 -AllSynthetic                  # delete every synthetic item
.\scripts\Invoke-SandboxTeardown.ps1 -Everything -ConfirmText 'DELETE sun-of-anton/cascade-sandbox'
```

Seeding creates parents before children, writes `parent:` ids into meta, adds native sub-issue links on
GitHub, and saves a manifest to `output\seeds\`. On GitHub it paces creates (~0.9 s each) to stay under
secondary rate limits, so 100 items take about two minutes. Deletion is permanent on both hosts.

## Review workflow

1. **Connection** → Test connection → Synchronize (source or sandbox, optionally one label).
2. **Overview / Review / Hierarchy** read that snapshot. Nothing is written.
3. **Proposals** → Propose parents (deterministic) or Propose sizes (model). Approve or reject each.
4. **Apply approved to sandbox**. Refused for batches made from a non-sandbox repository.
5. Re-synchronize to see the result.

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
