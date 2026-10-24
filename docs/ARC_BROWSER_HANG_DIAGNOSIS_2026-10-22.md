# Cascade GUI modal ARC deadlock — diagnostic findings (22 October 2026)

The operator uploaded `cascade-support-2026-10-22.json` to report a freeze. The file was reviewed before being removed from the active source tree. Git history retains the upload; avoid sharing unreviewed diagnostics publicly.

## Observed timeline (UTC, sanitized)
- 15:40:42: Browser called `POST /api/arc/open` and recorded no response.
- 15:40:57: Browser called `GET /api/gitmil/status` and recorded no response.
- 15:42:06: Browser called `GET /api/gitmil/projects` and recorded no response.
- The support bundle had no server event list and no confirmed Git.mil authentication response. The report does not prove that a token was rejected by Git.mil.
- `Start-CascadeWeb.ps1` called `Start-PmGui` inside an HTTP handler. It ran the older ARC WPF interface, whose modal `ShowDialog()` prevented this synchronous TCP listener from answering other requests.

## Remediation
1. Start the ARC CAC/TOTP workflow in Cascade's visible PowerShell process **before** its local HTTP listener is started.
2. Resolve the configured ARC service items (`gitmil`, `genapi`, `advana-api`) at startup. Browser requests never call vault getters that can launch authentication dialogs.
3. Launch the modern ARC browser through `ARC/Start.ps1` in a separate process from a nonblocking HTTP route; do not claim the separate vault session unlocks Cascade.
4. Prefer a distinct loopback port (8773); if occupied, use an OS-assigned free port without killing another project.
5. After successful CAC/TOTP and browser startup, hide PowerShell console; retain `--console` for troubleshooting and provide a guarded End session button.
6. Bound the Git.mil user-probe timeout and UI deadlines. Read-only synchronization still runs synchronously and may temporarily occupy the backend.
7. Add Windows CI checks that use synthetic ARC authentication and a slow external ARC browser process, verifying that further API calls still respond.

## Security
A browser popup may use Windows-native certificate selection when an approved HTTPS website supports it. Never ask a local HTML form for a CAC PIN. ARC's encrypted local vault unlock is distinct from authenticated Git.mil, GenAI and Advana API requests. Tokens remain only in the local PowerShell process and are stripped from child ARC browser processes.

## Workstation acceptance tests
- Start `Cascade-GUI.bat --console`; complete native CAC/TOTP. Verify `ARC vault unlocked`.
- Confirm the browser opens on the printed, dynamically bound port.
- Click **Open modern ARC browser** and immediately **Test Git.mil API**. The request must not wait for the separate ARC UI window to close.
- Use **End Cascade session** and confirm the process exits.
- Reopen normally without `--console` and confirm console hides after successful authentication.
- Test GenAI/Advana separately; capture new redacted diagnostics and exact error codes if services reject credentials.

Passing CI cannot verify DoD CAC, organizational network access or live service scopes.
