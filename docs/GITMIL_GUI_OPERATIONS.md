# Cascade v0.5 — Git.mil integration (read-only release candidate)

## What changed
The browser interface now connects to **web.git.mil** through the existing PowerShell GitLab and ARC bridge. The Git.mil page checks connectivity, discovers membership projects, and synchronizes selected projects. The backend retrieves project details, issues, commits, merge requests, and pipelines; creates evidence-linked triage proposals and a management activity brief. No local Git directories are required for Git.mil sync.

## Start
1. Use an **approved** Windows PowerShell 5.1 machine with network access to `https://web.git.mil` and an accessible ARC vault.
2. Configure `ARC_PATH` (or keep ARC in the sibling `arc` directory). ARC's service `gitmil` must hold an appropriately scoped GitLab token. If ARC requires an interactive unlock, do it in the same console session before starting the GUI. `GITLAB_URL` must be `https://web.git.mil`.
3. Run `Cascade-GUI.bat`, then open `http://localhost:8765`.
4. Select **Git.mil connection** → **Check ARC + Git.mil** → **Discover accessible projects** → select projects → **Synchronize selected projects**.
5. Review the dashboard, source evidence, triage decisions, and exported report. Review `output/autonomous/<run-id>/plan.json` for project-specific errors.

## Known limitations (do not deploy as autonomous production manager)
- GitLab connectivity, ARC unlock, proxy/TLS trust, permissions, API behavior and PS 5.1 must be tested on the actual approved machine.
- This is a **read-only Git.mil integration**. There are no production-write GUI endpoints, and triage decisions export to JSON only.
- Commit grouping is heuristic; it does not prove an Epic/Feature/Story hierarchy, actual hours, or acceptance.
- The GUI has no live AI classification/GenAi orchestration yet, though existing ARC-backed Grok/GenAi modules are retained.
- The API scanner has bounded pagination (default five pages per project/endpoint). The resulting report may not cover every work item or commit.
- The UI uses a loopback-only HTTP server; it does not offer enterprise identity controls, persistent service management, or multi-user access.
- Don't send sensitive data to an unapproved AI endpoint. Only use approved ARC services and approved hosting.

## Validation gate before operational use
- Verify ARC `gitmil` token resolution and noninteractive behavior, including expiration and reauthorization.
- Verify GET project list, issues (open and closed), commits, merge requests, and pipelines against actual Git.mil projects, including pagination, permissions and empty repos.
- Run `powershell.exe -NoProfile -File tests\Invoke-CascadeTests.ps1 -Suite Menu` and `tests/Test-Discovery.ps1` in Windows PowerShell 5.1.
- Validate management counts against GitLab and confirm error/truncation flags are surfaced.
- Perform a threat review of the loopback server, token lifecycle, local saved JSON, and log handling.
- Add durable agent scheduling, event processing, canonical metadata/body reconciliation, safe GitLab writes, and automated acceptance verification before declaring production readiness.