# Cascade connection diagnosis — 2026-10-22

## Evidence (redacted session summary)
The operator uploaded a session recording to GitHub for this investigation. It was reviewed, then removed from the active source tree to avoid retaining detailed user diagnostics in the repository. The source commit remains in Git history.

The UI loaded normally, including scripts, stylesheet, selected logo, and local status/config routes (HTTP 200).
The explicit ARC-open operation returned HTTP 503 in about 52 ms. Server diagnostics identified only `ARC_OPEN_FAILED: RuntimeException`—not the underlying stage.
The Git.mil authenticated-user test returned HTTP 503 in about 38 ms. Project discovery returned HTTP 503 in about 108 ms. A synchronization button click made no request because no projects were selected.
There were **no GenAI or Advana provider-test calls** in this session, so no conclusion about live AI API acceptance is supported.

## Root causes and corrections
- `Test-CascadeArcVaultUnlocked` was implemented but omitted from `Cascade.psd1`'s `FunctionsToExport`. The browser host called the unavailable function.
- ARC discovery originally depended on fixed folder conventions or environment variables. The GUI can now save the verified ARC installation directory (nonsecret path containing `pm.ps1`).
- ARC open failures now report a safe failure stage and error code, rather than just `RuntimeException`.
- Git.mil headers no longer invoke an interactive prompt during local API requests.
- GenAI and Advana/Grok use distinct endpoint/model environment settings, not a shared `LLM_URL` that could point both to the wrong service.
- Explicit GUI provider tests are noninteractive. Tokens stay in the PowerShell process, and the browser never handles CAC PINs or API keys.

## Operator verification
1. Pull the merged release, restart Cascade and open **Data & settings**.
2. If ARC isn't found automatically, enter the absolute folder containing ARC's `pm.ps1`, then save.
3. Use **Open ARC**; complete the native WPF authentication and close its window.
4. Use **Test Git.mil API**; only after success select and discover projects.
5. In **Data & settings**, independently run **Test Gemini API** and **Test Grok API**.
6. If a failure remains, export fresh diagnostics. Include the stage/code and request ID, but inspect the bundle before sharing.

All tests in CI use synthetic credentials. Real CAC authentication, access to `web.git.mil`, `api.genai.mil` and the approved Advana Databricks host must be verified on an authorized workstation.
