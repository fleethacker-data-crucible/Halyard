# Cascade browser session recorder

Launch **Cascade-GUI.bat** (or `Cascade-GUI.bat`) and open **Diagnostics**.

1. Click **Start recording**.
2. Reproduce the issue in any GUI page.
3. Return to Diagnostics, click **Stop** and **Export full session**.
4. Upload the resulting `cascade-session-*.json` after reviewing its contents.

The recorder captures button presses, navigation, selection/change events (not their values), API method/path/status/duration, JavaScript errors and network failures. It is opt-in and memory-only. It does not record screenshots, keystrokes, tokens, typed field contents or browser page contents. An export also attempts to fetch the latest 200 server events, which can include project IDs, internal paths and error messages: **review the JSON before sharing**.

For server-side debugging, `logs/cascade-YYYYMMDD.jsonl` is retained locally and can be reviewed separately. Do not share logs containing sensitive information without authorization.

Only two root launchers remain: **Cascade-GUI.bat** (normal GUI entry point) and **cascade.bat** (GUI by default, legacy CLI commands when arguments are supplied). Older launchers have been removed.
### Hanging request evidence

A `API_REQUEST` entry without a corresponding `API_RESPONSE` can indicate a pending or blocked server request, not necessarily a credential error. The October 22 report showed ARC's modal WPF GUI blocking Cascade's one-request-at-a-time TCP loop. Current Cascade authenticates at startup, opens modern ARC in a separate process, and records a connection attempt's error codes without capturing CAC PINs.
