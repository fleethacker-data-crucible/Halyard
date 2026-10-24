# Cascade Delivery Intelligence GUI

## Launch
Double-click `Cascade-GUI.bat`. Use `Cascade-Terminal.bat` for the advanced PowerShell menu. It starts a **loopback-only** Windows PowerShell 5.1 service and opens your browser at `http://127.0.0.1:<selected-port>`.

The launchers use the same process-scoped PowerShell 5.1 startup flags as ARC, with a local TCP listener instead of HTTP.sys. If your organization enforces script restrictions through policy or endpoint controls, request approval or code signing; the launchers do not override those controls.

## First scan
For live Git.mil, open **Data & settings** first. If ARC is not discovered, enter the absolute ARC installation directory containing `pm.ps1` and click **Save integration settings**. Configure the ARC vault item names (`gitmil`, `genapi`, `advana-api`) and, if required for your account, the approved GenAI/Advana endpoint and model values. Only HTTPS `api.genai.mil` and `*.databricks.mil` endpoint hosts are accepted to prevent accidental credential disclosure. Then open **Git.mil connection**, select **Open ARC**, authenticate in its native window, click **Test Git.mil API**, and discover projects before synchronizing. GenAI and Advana are tested independently with **Test Gemini API** and **Test Grok API**; neither test happens automatically during a Git.mil check. For optional local scans, go to **Data & settings**, enter one or more absolute local repository root paths (one per line), choose lookback days, and click **Save settings**. Click **Scan repositories** at the top.

This calls the existing read-only `Invoke-CascadeAutonomousCycle` engine, writes a timestamped `output/autonomous/<run-id>/plan.json` and `leadership-brief.md`, and loads the plan into the GUI.

If a browser can't reach the local service, open `ui/index.html` directly and import an existing `plan.json` using **Import JSON**. The offline file viewer cannot launch scans.

## Pages
- **Overview:** repository, commit and candidate counts, classification chart, triage queue and evidence coverage.
- **Work intelligence:** search/filter candidates, inspect generated descriptions and evidence, set review decisions.
- **Git evidence:** inspect observed commit metadata and file-change counts.
- **Leadership report:** review, copy or export an evidence-limited management briefing.
- **Data & settings:** configure scan roots and clear the current browser session.

**Export decisions** saves a `cascade-triage-decisions.json` file. Decisions are **not** written to GitLab. A separate audited approval/reconciliation pipeline is needed before production changes.

## Security and limitations
- Localhost only. No third-party front-end dependencies or remote telemetry.
- HTTP server exposes read-only Git scan and status endpoints. It has **no GitLab write endpoints**.
- The local scan uses `git` executable already installed and the repository's PowerShell module.
- Scanning large folder trees can take time. Limit root directories to approved repositories.
- Browser UI is not an autonomous background agent. No AI semantic analysis or acceptance verification is implied.
- Use only on approved networks/devices. Do not load classified or restricted content without authorization.
- Not tested in this Linux development environment against Windows PowerShell 5.1 or Git.mil; validate before operational use.
## Diagnosing connection errors

When an API test fails, the UI shows a diagnostic code, for example `ARC_NOT_FOUND`, `ARC_IMPORT_FAILED`, `ARC_LOCKED`, `CREDENTIAL_MISSING`, `AUTH_REJECTED`, `ACCESS_DENIED`, `NETWORK_OR_TLS`, or `ENDPOINT_OR_MODEL_NOT_FOUND`. Under **Diagnostics**, start the click recorder, reproduce the failure, and export the session. Review the export for internal identifiers before sharing. Server logs are under `logs/`. See `docs/ARC_CONNECTIVITY_DIAGNOSIS_2026-10-22.md` for the original failure chronology. A CI pass is not live CAC/API proof.

## ARC authentication / background PowerShell (October 2026)

Run `Cascade-GUI.bat`. The PowerShell window stays **visible for ARC's CAC/TOTP authentication**, then the chosen loopback URL opens and the terminal is hidden on successful authentication. Use `Cascade-GUI.bat --console` if you need to see diagnostic output. The preferred port is **8773**, with an automatic fallback if unavailable. Do not launch the URL from another project's process: always use the browser opened by Cascade.

**Open modern ARC browser** starts `arc/Start.ps1` in its **own PowerShell process**. It is the modern browser vault, not the classic WPF window. Because ARC vault unlock state lives in memory and is process-local, opening a separate ARC window does **not** authenticate Cascade. If Cascade reports `ARC_LOCKED`, close Cascade via **End Cascade session**, restart `Cascade-GUI.bat`, and complete the startup CAC/TOTP prompts.

The GUI server **preloads only the configured `gitmil`, `genapi` and `advana-api` ARC items during startup**. Neither Git.mil nor AI test endpoints may invoke a password picker or CAC dialog while the HTTP server is running. If you change an ARC item name while Cascade is running, use **End Cascade session**, then restart and reauthenticate to load the new item; the browser never stores or transmits the token.

Do not create an HTML PIN field or store CAC PINs or API keys in the browser. If a remote approved website offers a native smart-card certificate selection dialog, it is handled by the browser/Windows middleware and is not equivalent to unlocking the local ARC vault.


## Git.mil exact issue-label filtering

After a first read-only Git.mil synchronization, the **Git.mil issue label** field in the top bar lists real imported labels. Pick an exact existing label and click **Apply label**; Cascade re-filters the preserved local source snapshot immediately without sending another Git.mil request. Click **Clear** to show all imported issues again. You can click **Discover issue labels** after selecting source projects to query the standard GitLab project labels API before an initial sync.

No Data-Crucible label name is assumed, and project paths or cascade-meta fields are not treated as a label. Generic project-level commits, MRs and pipelines are not attributed to a label-specific issue set; while filtering by label those are hidden. This is different from work-item schema normalization, which is deferred to Phase 2. See docs/PHASE1_DATA_CRUCIBLE_CHECKPOINT.md for the approval test.
