# Cascade acceptance checkpoint: interactive hierarchy, dashboard, intelligence and stack

## Scope
This feature branch is deliberately not a wholesale rewrite to Svelte, .NET or Rust. Keep the existing PowerShell/ARC host and Git.mil connection until Nautilus compatibility is verified. This deliverable makes no Git.mil writes.

## 1. Example Work Items
Open Example work items. The example hierarchy is now at the TOP of the page, with an adjacent inspector. It contains 1 Theme, 3 Epics, 6 Features, 12 Stories and 24 optional standalone Tasks. Expand/collapse any branch, select an item, click its ACTUAL stable parent ID or a breadcrumb, search, filter and inspect fully populated metadata and descriptions.
GitLab labels appear as scoped label chips. The illustrative team::Data-Crucible label is not asserted to exist on the real web.git.mil instance. The comparison table is now collapsible beneath the interactive tree. Example titles no longer duplicate the type prefix. New taxonomy-generated titles also omit type prefixes; existing Git.mil issue titles are NOT renamed.

## 2. Leadership dashboard
Leadership dashboard is now the primary presentation rather than a Markdown report. It summarizes imported label-visible issues, declared types, unclassified items, GitLab opened/closed states, topology errors/warnings, scope, and attributable evidence. Metrics cards open underlying hierarchy/intelligence/evidence views. A supporting Markdown brief remains downloadable.
Closed issues are NOT proof of accepted work. Throughput, team productivity, hours, budget, verified completion and velocity remain unavailable unless backed by trustworthy data.

## 3. Run intelligence through the GUI
1. Start Cascade normally, authenticate ARC/CAC, test the Git.mil API.
2. In Git.mil connection, select source projects and synchronize. Select an actual team label from Git.mil if you want to narrow the review.
3. Open Intelligence workbench and click Run intelligence review. It reads the latest saved source snapshot and calls the existing Convert-CascadeGitLabIssueToNormalized, Get-CascadeWorkItemDeficiencies, Get-CascadeTaxonomy and Get-CascadeTopologyAudit functions.
4. Filter Needs human review, click an issue, inspect missing schema keys, existing stable ID, declared parent ID, deficiency codes and preview description.
5. Export review JSON for human discussion. NO API write, AI model invocation, parent guessing or approval occurs. Unknown types are marked heuristic. Current preview limit is 200 scoped issues; narrow using real Git.mil labels.
The earlier CLI AI and sandbox migration scripts are not automatically run by this safe first GUI stage.

## 4. Test the future stack before any refactor
Browser: open Stack compatibility and click Run browser compatibility tests. This tests real WASM execution, bundled Web Worker, IndexedDB, ephemeral OPFS, ES Modules and WebCrypto. Export JSON.
Host: from the Cascade repository root on Nautilus run:

    powershell.exe -NoProfile -File .\scripts\Test-CascadeStack.ps1

The script reports PowerShell 5.1 mode and execution policy without changing it; in-memory C# Add-Type, Windows DPAPI, TCP loopback, managed SQLite provider presence, optional dotnet availability and Rust toolchain discovery. Output is written under output/stack-check. It does NOT execute native Rust binaries, install packages, bypass system policy, or collect credentials.
A successful WebAssembly browser test is not a native Rust deployment approval. Browser OPFS support alone does not justify moving sensitive data to SQLite WASM. Continue using JSON snapshots until the approved local database driver, encryption and locking pass separate tests. Svelte/TypeScript would be prebuilt in CI.

## Stop at this checkpoint
Verify the example tree, parent navigation, dashboard metrics, workbench review and both stack probes on your workstation. Confirm the ARC/CAC flow and label filter still work and no Git.mil issue was changed. PR remains unmerged while awaiting acceptance. Native GitLab hierarchy ingestion, AI proposal validation and production migration are separate phases.

## Automated gates
Windows PowerShell 5.1 parser, existing ARC/API regressions, local read-only intelligence endpoint with operator-intent guard, synthetic normalized schema/parent tests, example navigation, browser dashboard smoke, real WASM module execution, and safe Windows host stack probe.
