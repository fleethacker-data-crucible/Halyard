# Cascade Nexus operational readiness

## What is implemented in this release
- Two entry points only: `Cascade-GUI.bat` and `Cascade-Terminal.bat`.
- Native ARC WPF auth action from browser via same PowerShell server process. No CAC PIN in HTTP.
- Read-only ARC discovery/unlock status; never attempts unlock during health polling.
- Genuine Git.mil `/api/v4/user` authentication test without CLI project selection.
- Live project discovery and real project/issue/commit/merge/pipeline snapshots.
- Explicit Gemini and Grok test requests through the existing ARC token mapping.
- The exact selected #8 Nexus graphic, cropped from original design board.
- Source-backed hierarchy, evidence table, work grid, graph, timeline, report, local scan and JSON import.

## Boundaries
- This code is not validated with an actual CAC, ARC vault, or Git.mil account in CI.
- Some ARC configurations use native/terminal prompts; the browser does not accept PIN/passwords.
- A local server cannot become a public or remote service; it only serves localhost.
- Backend reads Git.mil under current user's approved permissions; no production issue modifications from GUI.
- API pagination is bounded; reports must disclose the limit. Model ID availability varies.
- Windows PowerShell and WPF support required for native ARC UI.

## Startup security compatibility
Cascade follows ARC's working Windows PowerShell 5.1 startup pattern: STA, NoProfile, and `-ExecutionPolicy Bypass` for **the launched process only**. The launchers do not call `Set-ExecutionPolicy` or change the user's or computer's policy. A Group Policy (`MachinePolicy` or `UserPolicy`), AppLocker or WDAC restriction still takes precedence; ask Navy IT to approve or sign the scripts if those controls block execution.

The browser uses a bounded, single-user `TcpListener` bound to **127.0.0.1 only** rather than `HttpListener`. This avoids requiring HTTP.sys URL ACL registration/elevation; it does not override firewall or endpoint controls and does not expose a network service.

- **"running scripts is disabled" / UnauthorizedAccess:** check the launcher's printed `Get-ExecutionPolicy -List` table and the host's security controls. Don't weaken system-wide restrictions.
- **"Access is denied" at HttpListener.Start():** indicates an old checkout or archive. Confirm `scripts/LoopbackTransport.ps1` exists and the GUI launches `Start-CascadeWeb.ps1`. Fresh builds no longer call HttpListener.
- **Local port already in use:** close the prior Cascade instance or launch from a terminal with a different unused, approved high port.
- **Nexus browser doesn't open:** navigate to `http://127.0.0.1:8765/`. If blocked, examine the console error and `logs/cascade-YYYYMMDD.jsonl`. Do not assume successful launch from a downloaded ZIP.

## Troubleshooting
- ARC not found: set `ARC_PATH` or place ARC in `Projects\arc` next to `Projects\cascade`.
- ARC locked: use **Open ARC · authenticate**. Confirm native ARC UI finishes and closes before testing Git.mil.
- Git.mil `401`: verify ARC `gitmil` has an authorized Git.mil PAT (not GitHub/GenAi/Grok token).
- Git.mil `403`: token may lack project membership or scope.
- Network failure: verify sanctioned network/proxy/cert and the actual Git.mil API route from this workstation.
- Gemini/Grok failure: verify credentials `genapi`/`advana-api`, endpoint and permitted model.
- If any operation fails, export Diagnostics and review redacted server logs; do not share sensitive issue data.
## Browser startup, ARC auth and shutdown

The Cascade browser host is **single-threaded**. ARC unlock must run at **startup**, before the local TCP listener begins serving. No HTTP route should invoke `Start-PmGui`, `Ensure-VaultUnlocked`, `Read-Host`, or any other blocking dialog. Modern ARC's browser is launched in a separate process and explicitly cannot transfer its in-memory vault session into Cascade. The GUI launcher hides its console only after successful startup authentication; use `Cascade-GUI.bat --console` to retain it.

Cascade defaults to port **8773** and falls back to a dynamically bound free **127.0.0.1** TCP port if the preferred port is occupied. Never terminate another project's PowerShell instance to free a port. The sidebar's **End Cascade session** uses an explicit same-origin POST with an intent header; it stops the hidden PowerShell listener. Source-code tests also exercise a simulated ARC browser that remains open while Cascade answers other API requests.

`api.genai.mil` and Git.mil are independent external services from ARC CAC/TOTP; credentials for the former remain mapped to ARC items and are not automatically derived from browser certificate authentication. Server `logs/cascade-YYYYMMDD.jsonl` contains no tokens, but may contain paths or project identifiers. Review before sharing.
