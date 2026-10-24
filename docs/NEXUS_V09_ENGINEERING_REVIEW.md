# Cascade Nexus v0.9 — engineering review and corrective integration

## Scope and intent

Cascade is meant to be a PowerShell 5.1-backed, locally served browser application that turns verified Git.mil engineering events and work-item links into a reviewable Agile hierarchy. It is not simply a visualization gallery or a work-item generator. ARC remains the approved source for credential management. No production Git.mil modification is in the web GUI.

## Reproducible defects corrected

1. **Nexus asset missing from HTTP allowlist** — `Start-CascadeWeb.ps1` only served app.js, app.css, and index.html. `/nexus.js` returned 404. The routing allowlist and content type now include `nexus.js`.
2. **Nexus JS scope crash** — `nexus.js` attempted to use `state`, `render`, `page`, `names`, and `download` private to the separate app.js IIFE. Replaced with a minimal read-only `CascadeNexusBridge` plus `cascade:plan-changed` and `cascade:page-changed` events. No global state object is writable from Nexus.
3. **Project ceiling mismatch** — `Get-CascadeGitMilSnapshot` defaulted to 10 while the GUI and API permitted 20. The snapshot and export now permit 20 explicitly. Project IDs are de-duplicated in the API handler.
4. **ARC prompt in health check** — `Test-CascadeArcVaultUnlocked` used `Ensure-VaultUnlocked`, which may prompt for CAC/PIN while serving an HTTP GET. It now reads `CascadeArc` module state without triggering unlock. `/api/integrations` does not fetch secrets (availability is marked not checked), and Git.mil session initialization refuses to trigger a secret lookup while the vault is locked and no environment token exists.
5. **Missing useful demo hierarchy** — the demo had only actions and commits. Added clearly labeled six-item Theme → Epic → Feature → Story → Task hierarchy for visual testing.
6. **Request response hardening** — static responses include `nosniff`, `no-store`, `DENY` framing, and `no-referrer`; local requests must have an explicitly local Host as well as a loopback source.

## Verification actually performed

- `node --check ui/app.js` and `node --check ui/nexus.js`: pass.
- `node tests/test-browser-smoke.js`: pass. Checks startup, bridge initialization, demo loading, six-item hierarchy, populated SVG graph, and activity timeline with a minimal DOM test harness.
- `python tests/test-nexus-integration.py`: pass. Static source invariants for PowerShell route mappings, project limit, and no-prompt ARC status.

**Not verified:** Windows PowerShell 5.1 execution, target Nautilus policies, CAC authentication behavior, live Git.mil permissions/network access, 20-project live synchronization, real integration end-to-end. Node/Python are used **only to test in development**, not required at runtime.

## Remaining engineering work

- ARC browser-native CAC workflow still requires a formally approved integration, not capturing PINs through arbitrary HTML. The UI accurately displays unlocked/loaded state without automatically prompting.
- A truly asynchronous job queue and progress API are needed to prevent long Git.mil synchronization requests monopolizing the single-threaded PowerShell HttpListener loop.
- Git.mil paging/time-window coverage requires precise truncation detection and explicit per-project reconciliation summaries.
- Add an ADO-quality editable backlog only behind human approval and the existing fail-closed production write guard.
- A full dependency-aware Gantt and durable agent orchestration have **not** been implemented; the current graph and activity timeline are honest evidence-oriented views.

## Install/run

Extract the release so its `Cascade/` directory remains intact and run `Cascade-GUI.bat` on a workstation approved for PowerShell 5.1 and browser-based local tools. For offline viewing, open `ui/index.html` and import a `plan.json` or click the explicitly labeled DEMO mode.
