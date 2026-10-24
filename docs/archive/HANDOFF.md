# CASCADE - Agent Handoff

**Project:** Cascade (GitLab hierarchy + Monday-style reporting + LLM parent suggestions)  
**Org context:** Data Crucible / SMPG / SM-DATS (Navy shipyard data platform work items)  
**Date of handoff:** 2026-10-14  
**Runtime:** Windows PowerShell 5.1  

---

## 1. Mission

Cascade is a **local-first PowerShell toolkit** to:

1. Ingest GitLab work items (fixture JSON offline, or live `web.git.mil` API).
2. Propose **Epic - Feature - Story/Task** hierarchy and structured description metadata.
3. Produce **Monday.com-style HTML** completion views and tree reports.
4. Call **LLMs** to suggest parents (JSON packages, not raw CSV to the model).
5. Integrate with **ARC** vault for `genapi` (GenAi) and `advana-api` (Grok) without pasting every time.

**It does not yet** auto-apply hierarchy to live GitLab by default (apply is explicit / future).

Related but **separate** repo: **ARC** (`Projects\arc`) - CAC/2FA vault + STARK. Cascade only consumes ARC for secrets.

---

## 2. What works (verified on operator machine)

| Capability | Status |
|------------|--------|
| Local sync from fixture (62 issues) | OK - `output\tree_report.txt`, `monday_dashboard.html` |
| Migrate dry-run | OK - `output\migrate_plan.csv` (62 rows) |
| CSV - JSON + parent package | OK - `migrate_plan.json`, `parent_package.json` |
| LLM GenAi (`api.genai.mil`, model `gemini-3.8-flash`) | OK - chat + 51 parent suggestions |
| LLM Grok (Advana Databricks serving endpoint) | OK - chat test (`Hello!`) |
| ARC bridge (sibling `Projects\arc`) | Partial - unlock in **same process** works; **do not re-dot-source** `pm.ps1` or vault state resets |
| Single-session host | OK - `Launch-WithArc.bat` / `Start-CascadeHost.ps1` |
| Live GitLab sync/apply | Scaffolded + **Test-CascadeGitLabConnection** (menu [G]); needs `GITLAB_TOKEN` + project id |
| GitLab connection test | `Test-CascadeGitLabConnection` / `Invoke-TestGitLab.ps1` |

Successful parents run wrote `output\parent_suggestions.json` (51 rows) with sensible FEATURE assignments (e.g. AWS INC* - `FEATURE-aws-core`, KB/Gantt - `FEATURE-tooling`).

---

## 3. Files to port (entire tree)

Prefer: unzip **`cascade.zip`** or copy the whole `cascade\` folder.

### Required source

```
cascade/
  HANDOFF.md                 (this file)
  README.md
  .gitignore
  cascade.bat                # interactive - Start-CascadeHost
  Launch-WithArc.bat         # preferred: one process + ARC
  Launch-Cascade-FROM-PROJECTS-FOLDER.bat  # drop in Projects\
  cascade-live.bat           # optional live helpers
  config/
    cascade.env.example      # NEVER commit real cascade.env
  fixtures/
    work_items_fixture.json
    work_items_export_2026-10-09.csv
    mapping_sample.csv
    feature_catalog.txt
    hierarchy_catalog.json   # Epics + Features
  prompts/
    proposal_system.md
  scripts/
    Start-CascadeHost.ps1    # main interactive host
    Invoke-LocalSync.ps1
    Invoke-LocalMigrateDryRun.ps1
    Invoke-PlanToJson.ps1
    Invoke-SuggestParents.ps1
    Invoke-TestLlm.ps1
    Invoke-LiveSync.ps1
    Show-SampleMeta.ps1
    Import-CascadeModule.ps1
  src/Cascade/
    Cascade.psd1
    Cascade.psm1             # dots all modules below
    Schema.ps1
    GitLab.ps1
    Hierarchy.ps1
    Report.ps1
    Migrate.ps1
    GenAI.ps1                # Grok + GenAi clients
    Packages.ps1             # CSV-JSON, LLM packages
    ArcBridge.ps1            # ARC find/load/token
  tests/
    Test-Schema.ps1
  output/                    # generated; can start empty (.gitkeep)
```

### Do **not** port as secrets

- `config\cascade.env` (contains `GENAI_TOKEN` / `DATABRICKS_TOKEN` if created)
- Any pasted PATs, ARC vault files under `%LocalAppData%\ARC`

### Optional context from session (not required to run)

- Operator also uses **ARC** at `Projects\arc` (separate project).
- Fixture is a redacted snapshot of SMPG-ish work items (INC tickets, Postgres, AWS Bedrock, Qlik, etc.).

---

## 4. Architecture (short)

```
Fixture/Live issues
    - Schema (parse optional <!-- cascade-meta --> blocks)
    - Hierarchy / Migrate (type rules + mapping_sample.csv)
    - migrate_plan.csv
    - Packages: migrate_plan.json + parent_package.json
    - GenAI: Invoke-CascadeChat (Grok | GenAi)
    - parent_suggestions.json
    - Report: tree + Monday HTML
```

**LLM profiles**

| Profile | URL | Token |
|---------|-----|--------|
| **Grok** | `https://advana-jupiter.cloud.databricks.mil/serving-endpoints/databricks-grok-4-6/invocations` | Databricks PAT (`DATABRICKS_TOKEN` or ARC item **`advana-api`**; matchers may also accept `advana` / `databricks`) |
| **GenAi** | `https://api.genai.mil/v1/chat/completions` | ARC vault item **`genapi`** or `GENAI_TOKEN` |

Default GenAi model: **`gemini-3.8-flash`** (discovered via `/v1/models`; do not use missing model ids).

**Hierarchy catalog** (`fixtures/hierarchy_catalog.json`): Features parent under Epics (`EPIC-data-platform`, `EPIC-aws-foundation`, etc.).

---

## 5. Critical implementation lessons (do not regress)

1. **One PowerShell process** for ARC + Cascade. Each new `powershell -File` loses unlocked vault.
2. **Never re-dot-source `pm.ps1`** after unlock - resets `$script:PmState` / appears as -0 rec locked- and `Get-PmStarkApiKey not available`.
3. ARC vault item name is **`genapi`**, not `genai`.
4. Leave ARC menu with **[5] Exit Vault**, not Ctrl+C (triggers -Terminate batch job-).
5. LLM packages must be **JSON**; models often return markdown tables - strip fences and extract `[...]` array.
6. Env tokens **win** over ARC when set (`GENAI_TOKEN` / `DATABRICKS_TOKEN`).
7. Batch delayed expansion required if using cmd for profile args (`!VAR!`).
8. PowerShell 5.1: avoid broken `@"..."@` here-strings containing JSON; prefer string concat.
9. Databricks Grok: OpenAI-style `messages` body; model id `system.ai.grok-4-6`; response model may show `us-gov.xai.grok-4.6`.
10. Prefer stable path `Projects\cascade` beside `Projects\arc` (avoid `Downloads\cascade (N)\`).

---

## 6. Operator workflow

```text
Projects\
  arc\                 # ARC v3.5.x
  cascade\             # this project
  Launch-Cascade.bat   # optional pointer to cascade\Launch-WithArc.bat
```

1. Run `cascade\Launch-WithArc.bat`
2. CAC unlock when prompted; ARC **[5] Exit Vault** back to Cascade
3. `[1]` Local sync - `[2]` Migrate dry-run - `[8]` Suggest parents (GenAi)
4. Review `output\parent_suggestions.json`

`config\cascade.env` example keys: `ARC_PATH`, `CASCADE_ARC_ITEM_GitLab=gitmil`, `CASCADE_ARC_ITEM_GenAi=genapi`, `CASCADE_ARC_ITEM_Grok=advana-api`, plus local-only `GENAI_TOKEN` / `DATABRICKS_TOKEN` / `GITLAB_*`.

---

## 7. Suggested next work for the new agent

1. **Merge** `parent_suggestions.json` back into `migrate_plan.csv` / plan JSON (menu item).
2. **Create Epic/Feature issues** (or map catalog ids - real GitLab IIDs) before apply.
3. **Apply path** (dry-run - explicit apply) for description meta + parent links on `web.git.mil`.
4. Harden ARC token path without second `pm.ps1` load; optional `pm -Get genapi` only as last resort.
5. Dedupe Downloads vs Projects copies of cascade; single git repo.
6. Expand `mapping_sample.csv` from accepted LLM suggestions.
7. Tests beyond schema (package build, JSON extract).

---

## 8. Seed prompts for the next agent

- -Merge parent_suggestions.json into migrate_plan and regenerate Monday HTML.-
- -Add menu item Apply parents (dry-run) using GitLab API.-
- -Map FEATURE-* catalog ids to existing GitLab issue IIDs.-
- -Fix ARC token read without re-sourcing pm.ps1.-

---

## 9. Sensitivity

General-purpose tooling and sample fixtures. Do not commit `cascade.env` or vault material. Tokens stay in local config only. Do not send secrets to LLMs (prompts already instruct no secrets).

---

## 10. Leadership dashboard (manual Monday board) - 2026-10-15

Boss needs the **SMPG Leadership Dashboard** Monday while the git adapter app finishes. All in **PowerShell**.

### What was added

| Piece | Path |
|-------|------|
| Enriched normalize (points, blockers, age, owner, status labels) | `src/Cascade/Normalize.ps1` |
| Leadership metrics + HTML export | `src/Cascade/Dashboard.ps1` |
| One-shot script | `scripts/Invoke-LeadershipDashboard.ps1` |
| Sample sprint/capacity config | `fixtures/leadership_config.sample.json` |
| DoR optional meta keys | `story_points`, `target`, `sprint`, `blocked`, `blocker_type` |
| Pre-rendered demo board | `output/leadership_dashboard.html` |

### Monday command (on operator Windows box)

```powershell
cd Projects\cascade
.\scripts\Invoke-LeadershipDashboard.ps1 -DemoBoard
# open output\leadership_dashboard.html
```

Without `-DemoBoard`, metrics derive from normalized fixture/live issues (feature rows from Cascade tree when meta exists; velocity still needs `leadership_config` history until closed sprints carry points).

### Normalize fields now on each item

`status_label`, `priority`, `area`, `tech`, `owner`, `effort_band`, `story_points`, `target`, `sprint`, `age_business_days`, `is_blocker`, `blocker_type` plus standard Cascade meta (`type`/`id`/`parent`/`state`).

### Repeatable functions

- `Convert-CascadeGitLabIssuesToNormalized` - batch normalize + optional JSON out
- `Get-CascadeLeadershipMetrics` - KPIs (on-track, velocity, capacity, blockers)
- `Export-CascadeLeadershipHtml` - board matching the reference image layout
- `Get-CascadeDemoLeadershipBoard` - reference rows from the attached screenshot
