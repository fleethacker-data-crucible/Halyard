# Halyard: Tech Test Procedure

This guide tests Halyard end to end on one Windows PC: the engine and browser UI, the GitHub sandbox
([Halyard-Ballast](https://github.com/fleethacker-data-crucible/Halyard-Ballast)), the model router
[Fairlead](https://github.com/fleethacker-data-crucible/Fairlead), and optionally the vault
[Lazarette](https://github.com/fleethacker-data-crucible/Lazarette) and [Semaphore](https://github.com/fleethacker-data-crucible/Semaphore).
It takes about 60 to 90 minutes, most of it the seed run.

Every step is taken from this repository (commit on `test/halyard-procedure`). A step marked **UNVERIFIED** has not been run
on a workstation yet, or its exact screen or output is not in the repo. Report those as they are; do not guess.

> **Privacy:** the Halyard and Fairlead keys live in `config\halyard.env` (git-ignored) and Windows Credential Manager. Do not paste
> them into a report, a ticket or a chat. Use the sandbox repository for every write test. Never point a write test at a real backlog.

---

## 1. Before you start

| You need | Notes |
|---|---|
| Windows 10 or 11 with Windows PowerShell 5.1 (`powershell.exe`) | The launcher and scripts target 5.1 (`Launch-Halyard.bat` calls it). |
| The Halyard clone | `C:\Users\ashab\fleethacker-data-crucible\Halyard`, on `test/halyard-procedure` or `main`. |
| The Halyard-Ballast clone | `C:\Users\ashab\fleethacker-data-crucible\Halyard-Ballast`. It is the only writable repository. |
| The Fairlead clone | `C:\Users\ashab\fleethacker-data-crucible\Fairlead`. Needed for every model step. |
| GitHub access | `gh auth status` shows a login, or `GITHUB_TOKEN` with `repo` scope is set in `config\halyard.env`. |
| A source repository for Intake | Any repository you own that holds the raw tickets in step T13. `HALYARD_SOURCE_REPO` names it. **UNVERIFIED** which repository the tester will use. |
| `config\halyard.env` | Copy `config\halyard.env.example` to `config\halyard.env`. Set `HALYARD_SOURCE_REPO` and `HALYARD_SANDBOX_REPO` (both may be Halyard-Ballast for a sandbox-only run). |
| Lazarette (optional) | Only if the vault is used. Found through `LAZARETTE_PATH`, a sibling `Lazarette` folder, or `%USERPROFILE%\Projects\Lazarette`. |
| Build tools (optional) | Only for step V2: .NET 8 SDK, Node.js 20+, Pester 5. |

> **Config check:** `HALYARD_WRITE_MODE` must be `sandbox` (the default in the example). Do not set `HALYARD_ALLOW_PRODUCTION`.

---

## 2. Start order

| Order | Dependency | Needed for | Required? |
|---|---|---|---|
| 1 | Fairlead | Every model call (Settings test, Draft, Size, Tag skills with model, Vision draft with model) | Yes for model steps |
| 2 | GitHub | Connection, sync, seed, proposals, intake, teardown | Yes |
| 3 | Lazarette | Vault token (only when `HALYARD_USE_LAZARETTE=1` or `HALYARD_LAZARETTE_ITEM_GitHub` is set) | No |
| 4 | Halyard engine | The UI and API | Yes |
| 5 | Shellback peer cards | Skills and availability for Plan | No (files in `config\shellback\`) |
| 6 | Semaphore | Loading the Halyard CSV export | No |

---

## 3. Smoke test per dependency

### D1. Fairlead

1. In `C:\Users\ashab\fleethacker-data-crucible\Fairlead`, double-click **`Launch-Fairlead.bat`**. A tray icon appears.
2. In PowerShell:
   ```powershell
   curl.exe -s http://127.0.0.1:8776/api/health
   ```
   **Expected:** `{"ok":true,"version":"..."}` (the repo's API doc lists this route as unauthenticated).
   **If connection refused:** Fairlead is not running, or its port moved. Check the tray tooltip or the console (`Launch-Fairlead.bat --console`).
3. Install a key for Halyard: open Fairlead (tray icon → **Open Fairlead**), go to **Clients**, add a client for **Halyard** and choose
   the option to install it for this app. **UNVERIFIED** exact button names.
4. Check that the credential exists, without printing it:
   ```powershell
   cmdkey /list:Fairlead/Halyard
   ```
   **Expected:** one entry named `Fairlead/Halyard`. **UNVERIFIED** that `cmdkey` shows the entry in this environment.

### D2. GitHub sandbox and source

1. Confirm the sandbox is reachable (read only):
   ```powershell
   gh api repos/fleethacker-data-crucible/Halyard-Ballast --jq .full_name
   ```
   **Expected:** `fleethacker-data-crucible/Halyard-Ballast`.
2. Confirm the source repository is reachable the same way (replace the name with `HALYARD_SOURCE_REPO`).
3. Check the token scope with `gh auth status`. Deleting issues needs admin rights on the sandbox repository.

### D3. Lazarette (only if used)

1. Confirm the library file exists at the path Halyard searches (`lib\Lazarette.ps1` under the Lazarette folder). Halyard's
   `module\Halyard\Lazarette.ps1` loads it.
2. Read item names only (no secrets):
   ```powershell
   . "C:\Users\ashab\fleethacker-data-crucible\Lazarette\lib\Lazarette.ps1"
   Unlock-LazaretteVaultInteractive
   Get-LazaretteItemNames
   ```
   **Expected:** the item named in `HALYARD_LAZARETTE_ITEM_GitHub` appears. **UNVERIFIED** the interactive unlock on this PC.
3. Skip this dependency if `GITHUB_TOKEN` is set in `config\halyard.env`.

### D4. Halyard engine

1. From `C:\Users\ashab\fleethacker-data-crucible\Halyard`:
   ```powershell
   .\Launch-Halyard.bat --console --no-browser
   ```
   **Expected:** the console compiles the engine (a few seconds, it prints the source file count and time), prints a URL with a one-time
   session token and says which port it chose (default 8773).
2. In a second PowerShell window:
   ```powershell
   curl.exe -s http://127.0.0.1:8773/api/health
   ```
   **Expected:** `{"ok":true,"version":"..."}`. `/api/health` is the only route that needs no session token.
3. Open the printed URL once in the browser. Do not share it.
4. **Connection** → **Test connection**. **Expected:** the sandbox repository is reported as reachable.
5. **Settings** → **Test model**. **Expected:** a reply from the model route. **If it says** *Open Fairlead and install a key for Halyard.*,
   return to step D1.3.

### D5. Semaphore (optional)

1. Double-click **`Launch-Semaphore.bat`** in the Semaphore clone (the README says it runs on loopback ports 8800–8802).
2. **Data** → add the four CSV files from step T14 (`health`, `items`, `features`, `velocity`).
3. **Expected:** the tables load and a chart can use `items.deficiencies` or `velocity.points_done`. **UNVERIFIED**: the repo
   checks the export format only; loading into Semaphore is a manual check (`docs/SEMAPHORE-EXPORT.md`).

---

## 4. Halyard functional tests

Run these in order. Each one uses the sandbox only.

**T1. Seed the sandbox.** From the Halyard folder:
```powershell
.\scripts\Invoke-SandboxSeed.ps1 -Total 100 -IncludeDefects -RunId tech-test-1
```
(Or **Sandbox** → **Seed a synthetic hierarchy**, with *gaps* ticked.)
**Expected:** 100 issues in Halyard-Ballast labelled `halyard`, `halyard-synthetic` and `halyard-run::tech-test-1`, about 2 minutes
(paced for GitHub). Counts match Halyard's generator: 2 themes, 6 epics, 18 features, 50 stories, 24 tasks. About 20 items carry gaps.
A manifest is saved in `output\seeds\`.

**T2. Synchronize.** **Connection** → **Synchronize** (sandbox). **Expected:** the snapshot shows 100 items; **Overview** counts
the items needing review. If a fresh run looks short, synchronize again (GitHub listings can lag a few seconds).

**T3. Review and audit codes.** Open **Review**. **Expected:** each gap is listed with its stable code. The codes must include every
code in `expected_codes` in `seed\simulated-halyard-seed.json` in Halyard-Ballast (see step T16 for that file). Compare with
the seed manifest in `output\seeds\`.

**T4. Repair metadata.** **Review** → select rows with `missing_halyard_metadata` → bulk bar → **Repair meta**, or
**Proposals** → **New analysis** → **Repair metadata**. **Expected:** a proposal batch; the diff shows the `~~~halyard-meta` block being
built. Approve, then **Apply approved to sandbox**. **Expected:** the items re-synchronize by themselves and the code disappears from **Review**.

**T5. Propose parents.** **Proposals** → **New analysis** → **Propose parents**. **Expected:** `PARENT_UNRESOLVED` and
`PARENT_TYPE_MISMATCH` items get ranked candidates with evidence; a wrong-level parent is not auto-approved. Apply the approved rows.

**T6. Draft missing sections (model).** **Proposals** → **New analysis** → **Draft missing sections**. Needs Fairlead.
**Expected:** drafts for `story_missing_user_need`, `story_missing_acceptance_criteria` and `story_missing_tasks`. Existing text is kept;
a draft that lost text is flagged low confidence.

**T7. Propose sizes (model).** **New analysis** → **Propose sizes**. Needs Fairlead.
**Expected:** story points from 1, 2, 3, 5, 8, 13 and feature effort Small, Medium or Large. `story_has_effort_band` and
`feature_has_story_points` are corrected.

**T8. Tag skills.** **New analysis** → **Tag skills** (deterministic). **Expected:** `skills` meta drawn from `config\skills.json`
ids, inherited from the parent where it has none.

**T9. Hand edit.** **Hierarchy** → select a story → change points in the item panel. **Expected:** a pre-approved proposal appears
in **Proposals**; it goes through the same diff and apply path.

**T10. Plan.** **Plan** → choose the planning window. **Expected:** ready stories (sized, definition of ready met, not blocked) are placed
within each team's committed share of `pointsPerSprint` from `config\team.json`; skill gaps are listed. **Propose this plan** creates sprint-plan proposals.
**UNVERIFIED** that the people in the seed (`person-a`..`person-d`) match `config\team.json`; adjust the team file for the test (open question).

**T11. Leadership.** **Leadership** → **Download report**. **Expected:** one self-contained HTML file; the open-blocker list includes
`STORY :: Query ledger by correlation id` (label `blocked::awaiting-sample-feed`, blocked since 2026-10-27), with its business-day age.

**T12. Vision.** **Vision** → **Draft** (deterministic) and then, with Fairlead, **Draft with model**. **Expected:** themes, priorities and risks
(blockers, parked or unsized work) appear; the model draft names only items that exist. Drafts are saved under `output\vision\`.

**T13. Intake.** Create four raw tickets in your source repository (see the `intake_source_tickets` in the seed file, step T16). Then
**Intake** → select the source → **Preview** → **Run**. **Expected:** copies appear in the sandbox with labels `halyard` and `halyard-intake` and a
`source_url`; the source tickets are not changed. Manifest in `output\intakes\`. **UNVERIFIED** the classification each ticket should receive (Intake classifies them itself).

**T14. Semaphore export.** Export the four CSV tables from the current snapshot (`POST /api/export/semaphore`, via the API with the session
header; **UNVERIFIED** which button in the UI triggers it). **Expected:** `output\exports\semaphore\{health,items,features,velocity}.csv`,
UTF-8 without BOM, CRLF line ends, header rows as in `docs\SEMAPHORE-EXPORT.md`.

**T15. Write safety.** **Diagnostics** → writes. **Expected:** every write in this run targets `Halyard-Ballast` only. Attempting a write to any
other repository is refused with `WRITE_BLOCKED`. Production writes are not offered in the UI.

**T16. Teardown.** Dry run first, then the real delete:
```powershell
.\scripts\Invoke-SandboxTeardown.ps1 -RunId tech-test-1 -DryRun
.\scripts\Invoke-SandboxTeardown.ps1 -RunId tech-test-1
```
**Expected:** the dry run lists only `halyard-synthetic` items with that run id; the real run deletes them and nothing else.
Intake copies carry `halyard-intake`, not necessarily the run id: **UNVERIFIED** whether they need deleting by hand.

**Reference file.** `seed\simulated-halyard-seed.json` in Halyard-Ballast holds 43 simulated items with the expected audit codes and a blocked
story. Halyard does not import it; use it to compare results with T3, T11 and T13. Its `expected_codes` are a subset of what T1 produces
(**UNVERIFIED**: the subset relation is by generator, not checked against a live run).

---

## 5. Expected results and failure checks

| Symptom | Check | Fix |
|---|---|---|
| `Connection refused` on 8776 | Fairlead tray icon present; port in its console | `Launch-Fairlead.bat` |
| Settings: *Open Fairlead and install a key for Halyard.* | `cmdkey /list:Fairlead/Halyard` | Step D1.3 |
| `PROVIDER_NOT_READY` | `GITHUB_TOKEN` set, or `gh auth status` logged in | Step D2 |
| `AUTH_REJECTED` or `FORBIDDEN` | Token scope and repository access | Renew token; check `repo` scope |
| `WRITE_BLOCKED` | `HALYARD_SANDBOX_REPO` value | Set it to `fleethacker-data-crucible/Halyard-Ballast` |
| Session page says *Open Halyard from the launcher* | The tab has no session token | Relaunch |
| Engine stopped while you were away | Auto-stop, about 20 s after the tab closes | `--stay` or `HALYARD_AUTO_STOP=0` |
| "Halyard is already running" | An earlier engine | Answer Y, or `scripts\Stop-Halyard.ps1` |
| Fresh run looks short in **Review** | GitHub listing lag | **Connection** → **Synchronize** again |
| `expected_codes` missing in T3 | Seed run used no `-IncludeDefects`, or a different `-Seed` | Re-run T1 with the defaults shown (`-Seed` defaults to 20261024) |

---

## 6. Checklist

- [ ] Fairlead health answers; `Fairlead/Halyard` credential present
- [ ] GitHub sandbox and source reachable with `gh`
- [ ] Lazarette unlocked, or `GITHUB_TOKEN` set (Lazarette not used)
- [ ] Engine starts, `/api/health` answers, Test connection passes, Test model passes
- [ ] T1 seed: 100 items, correct counts, manifest written
- [ ] T2 sync; T3 audit codes match `expected_codes`
- [ ] T4 to T9 proposals created, approved, applied; re-sync closes the gaps
- [ ] T10 plan, T11 leadership report (blocker listed), T12 vision
- [ ] T13 intake copies created; sources unchanged
- [ ] T14 four CSV files written in the expected format
- [ ] T15 no write outside the sandbox
- [ ] T16 teardown dry run and delete match the run id
- [ ] No token, key or session link in any saved file or report

---

## 7. Reporting results

1. Note the date, your name, the Halyard commit (`git -C Halyard rev-parse --short HEAD`), the Fairlead, Ballast and Semaphore commits,
   and whether Lazarette was used.
2. For each step: **Pass**, **Fail** (give the step, the actual result and the code shown), or **UNVERIFIED** (say why).
3. Attach the seed manifest from `output\seeds\`, the intake manifest from `output\intakes\` and the engine log `logs\halyard-*.jsonl`
   for the run. Do not attach `output\llm_transcripts\` without checking them; they are redacted but still model traffic.
4. Do not include the session link, any token, the Fairlead key or the contents of `config\halyard.env`.
5. Send the report to the Halyard maintainer through the team's normal channel (**UNVERIFIED** which channel).

---

## 8. Open questions and UNVERIFIED steps

- **UNVERIFIED:** exact button names in Fairlead's Clients page (D1.3) and the UI path that triggers the Semaphore export (T14).
- **UNVERIFIED:** `cmdkey` output (D1.4); Lazarette unlock on the workstation (D3).
- **UNVERIFIED:** the team file mapping for `person-a`..`person-d` (T10).
- **UNVERIFIED:** Intake classification of the four raw tickets (T13) and whether intake copies need manual deletion (T16).
- **Open:** `Halyard/docs` and Fairlead's `integrations/halyard/README.md` describe different variables (`LLM_URL`, `LLM_TOKEN`, `LLM_MODEL`
  there; `HALYARD_FAIRLEAD_URL`, `HALYARD_FAIRLEAD_MODEL` in Halyard's code). This procedure uses the Halyard names. Fairlead's recipe is marked **Hackathon (not verified)**.
