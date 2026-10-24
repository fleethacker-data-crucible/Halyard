# Phase 1–3 repository consolidation

## What changed

1. **Clutter:** dropped archived binary release ZIPs and the tracked session-recording artifact from the active Git tree (their previous commits remain in Git history); removed duplicate `docs/ROADMAP.md` and moved dated handoff/release notes to `docs/archive/`.
2. **Source of truth:** tracked the complete Cascade Nexus v0.9 PowerShell/browser source tree directly, not solely the old v0.7 ZIP or the standalone HTML preview. Default launcher is now the integrated local browser; classic console remains available via `Cascade-Terminal.bat`.
3. **Entrypoints/testing:** fixed `cascade.bat live`, added explicit documented dispatch for `meta`, `shell`, `gui`, `test-offline`, and `sandbox-rebuild` (dry-run), consolidated offline tests into a single PowerShell 5.1 test runner, and removed duplicate wrappers for module import, GitLab test, integrity audit, and sandbox rebuild. The underlying nontrivial, write-protected workflows are retained.

## Removed compatibility wrappers and equivalents

| Retired path | Replacement |
|---|---|
| `scripts/Import-CascadeModule.ps1` | `Import-Module .\src\Cascade\Cascade.psd1` |
| `scripts/Invoke-TestGitLab.ps1` | `tests/Test-GitLab.ps1` / `cascade.bat test-gitlab` |
| `scripts/Invoke-IntegrityAudit.ps1` | `tests/Test-Integrity.ps1` / `powershell.exe -NoProfile -File tests\Invoke-CascadeTests.ps1 -Suite Offline` |
| `scripts/Invoke-SandboxRebuildFromCbr.ps1` | `scripts/Invoke-BulkPushSandbox.ps1 -FromCbrDirect` / `cascade.bat sandbox-rebuild` (dry-run) |

## Deliberately preserved

Every PowerShell module imported by `Cascade.psm1`, the explicit `WritePolicy.ps1` authorization checks, the standalone Nexus HTML preview, the source fixtures, user-agreement-gated sandbox scripts, and the original single-process ARC operator launcher remain. Avoid replacing mature scripts with stubs.

## Known gaps

- ARC and Git.mil were not accessible for live runtime verification. The backend's synchronous sync could block the single-threaded localhost listener for long operations.
- Browser host and ARC integration should be independently tested in an approved Windows PowerShell 5.1 environment before any operational rollout.
- Live Git.mil read flows currently have overlapping config/env logic; centralize only after targeted integration tests and an API contract review to avoid changing credential precedence.
- Git history still includes old archives and any previous session recordings. If the recording contains sensitive information, coordinate a separate incident/repository-history remediation; a regular Git delete does not erase history.
- The source contains organization-specific fixture descriptions; do not publish this private repository or copy it to public services without approved review.

## Reproducible checks

PowerShell on Windows: `powershell.exe -NoProfile -File tests\Invoke-CascadeTests.ps1 -Suite Offline` and `Cascade-GUI.bat`.
Development-only tools: `node tests/test-browser-smoke.js`, `python tests/test-nexus-integration.py`.