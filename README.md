# Cascade Nexus — PowerShell 5.1 + offline-first browser

Cascade Nexus turns engineering evidence into **reviewable Agile delivery intelligence**. The product has two deliberately different modes: a localhost browser interface (default) and the legacy operator console (explicit). It does not infer hours worked or completion from Git commits, and it does not silently write to production Git.mil projects.

## Launch — two files, nothing else

- **`Cascade-GUI.bat`** opens the localhost browser app at an automatically selected loopback port (prefers `8773`), using Windows PowerShell 5.1 as its live backend. CAC/TOTP is completed in the visible PowerShell console before the browser opens; the console is then hidden. Use `Cascade-GUI.bat --console` to keep it visible.
- **`Cascade-Terminal.bat`** opens the existing advanced operator console (read-only and explicitly approved sandbox workflows).

The four historical launchers were removed. Both scripts launch one PowerShell process each. There is no Python, Node.js, Docker or external JavaScript runtime requirement on the Navy workstation.

### First-time connection

1. Place approved ARC in a neighboring `Projects\\arc` folder, or configure `ARC_PATH` / **Data & settings** to its absolute installation path containing `pm.ps1`. The ARC vault items are named **`gitmil`**, **`genapi`**, and **`advana-api`** by default.
2. Run **`Cascade-GUI.bat`**. **Before any local HTTP server is started**, Cascade loads ARC once and uses ARC's existing CAC/TOTP authorization in the visible PowerShell window. Do not enter PINs in the web UI. After authentication, Cascade starts the browser on its selected port and hides the console if authorization succeeded.
3. Run **Test Git.mil API**. This calls authenticated `/api/v4/user`. Then **Discover accessible projects**, select projects, and **Synchronize**. No demo data is substituted for a failed connection.
4. **Open modern ARC browser** opens ARC `Start.ps1` in a **separate process**, never the old WPF GUI. That separate window can manage ARC secrets, but its unlocked vault session is **not shared with Cascade**. If Cascade is locked, restart its GUI launcher to authenticate.
5. On **Data & settings**, separately run **Test Gemini API** / **Test Grok API** after the vault is unlocked.

**Background engine:** the PowerShell process stays alive and listens on loopback while the console is hidden. Use **End Cascade session** in the sidebar to stop it. For troubleshooting, `Cascade-GUI.bat --console` keeps the terminal visible; logs remain in `logs/`. Port fallback never stops ARC, Keystone, or another localhost application.

The browser operates at `http://127.0.0.1:<selected-port>` on the local workstation only. Review the repo and follow Navy execution/approval policy before use. Live Git.mil/CAC authentication must be verified on an approved workstation; automated tests cannot use your CAC.

## Windows security and launcher troubleshooting

Both batch launchers mirror ARC's working PowerShell 5.1 entry point: `-STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File`, scoped to **one process**. They do not call `Set-ExecutionPolicy`; they cannot override enforced Group Policy, AppLocker or WDAC restrictions. If the workstation rejects the scripts under an enforced policy, use your IT-approved signed-script route instead.

The GUI binds a **loopback-only TCP socket at 127.0.0.1**, not `HttpListener` (which may require HTTP.sys URL reservations and can throw `Access is denied`). The backend still serves the same authenticated/read-only APIs. See `docs/OPERATIONAL_READINESS.md` for the distinction between PowerShell policy errors, port conflicts, and authentication failures.

## Repository layout

- `scripts/Start-CascadeWeb.ps1`: local browser HTTP host / read-only engineering collection.
- `ui/`: bundled HTML, CSS and JS including Nexus graph, work grid and activity timeline.
- `src/Cascade/`: PowerShell module, Git.mil adapter, ARC bridge, agent planner, safeguards and evidence normalization.
- `scripts/`: operator workflows and optional safe/sandbox actions. Scripts with writes have their own approval requirements.
- `tests/`: offline PowerShell suite, browser smoke test (requires Node for development only), static integration test (Python for development only).
- `fixtures/`: versioned, sanitized sample data required by tests and demo.
- `docs/`: current operational documentation; `docs/archive/`: historical handoff and release notes.

## Tests

```powershell
powershell.exe -NoProfile -File tests\\Invoke-CascadeTests.ps1 -Suite Offline
```

The offline suite runs Schema, Integrity, AgentRuntime, Discovery and Autonomy tests without any Git.mil, ARC, or LLM access. Development hosts can additionally run:

```bash
node tests/test-browser-smoke.js
python tests/test-nexus-integration.py
node --check ui/app.js
node --check ui/nexus.js
```

**Testing limits:** passing offline smoke tests does *not* prove Windows PowerShell 5.1 syntax, CAC prompts, Git.mil pagination, or ARC integration in the actual Navy environment. Live connection diagnostics require explicit operator initiation.

## Git.mil write policy

Production project `16629` must remain protected. Test/sandbox project `52999` is the intended scratch target. All potential remote writes must go through `Assert-CascadeWriteTarget`, and human approval is required before applying proposed changes. The browser intelligence workflow remains **read-only** by default. Do not paste or commit PATs, private keys, or vault data.

## Read the current docs

- `docs/GUI_QUICKSTART.md`
- `docs/GITMIL_GUI_OPERATIONS.md`
- `docs/NEXUS_INTEGRATION.md`
- `docs/CLEANUP_PHASE1_3.md`
- `ROADMAP.md` (single canonical roadmap)

Older source tree and release archives remain recoverable from Git history. Release ZIPs and session recordings are intentionally excluded from the active source root. See `docs/archive` for historical context.