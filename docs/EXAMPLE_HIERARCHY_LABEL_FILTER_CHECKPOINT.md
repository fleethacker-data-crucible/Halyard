# Checkpoint: Synthetic schema example and GitLab-native label search

## Scope

The Example Work Items tab is a separate static browser view. It contains exactly 46 synthetic records: 1 Theme, 3 Epics, 6 Features, 12 Stories and 24 optional separate Tasks. Every item has a complete Cascade metadata block and worked Markdown description, parent ID and illustrative GitLab labels. The tab never connects to Git.mil or ARC and never replaces live work items.

The sample label is team::Data-Crucible; **this is illustrative, not confirmation that this label exists in web.git.mil**. Use Discover issue labels to learn actual organization labels.

## Required metadata vs recommended bodies

| Type | Required keys (config/taxonomy.json) | Body | Parent |
|---|---|---|---|
| THEME | type, id, state, status, area | Outcome, beneficiaries, success measures, constraints, review cadence | root |
| EPIC | type, id, theme, state, area | Outcome statement, target window, success picture, risks | parent Theme ID (Cascade convention) |
| FEATURE | type, id, parent, theme, state, area, effort | Short description, PfW/DWI, Feature Done-When, dependencies | Epic ID |
| STORY | type, id, parent, theme, state, area, points, sprint | User story, observable acceptance criteria, constraints, Definition of Done | Feature ID |
| TASK | Example convention: type, id, parent, theme, state, area | Objective, implementation/verification steps, evidence | Story ID, optional; normally Story checklist |

Metadata is embedded in a fenced cascade-meta block. Issue labels are a SEPARATE GitLab issue field. A metadata area is not itself a GitLab label. Each work item to appear in a label-filtered view must carry the selected literal label in its actual issue labels array. GitLab's native Epics/Work Items may require their own APIs rather than ordinary project Issues API calls.

## Label visibility correction

The former importer read only five 100-item issue pages from each selected project, then filtered locally. In shared repositories, older labeled issues could be missed.

Now a **synchronize with a selected label** also calls the GitLab-native project issues API with a URL-encoded labels parameter, merges matching results by project ID and issue IID, and keeps the broader source snapshot intact. GitLab project-label discovery includes ancestor group labels.

An applied label filters the local source immediately. If that source was imported with another label, the UI warns that the targeted server-side search was NOT performed and prompts a new synchronization.

Limitations: server-side label pagination still has a cap, permissions can restrict results, and GitLab native Epics may not be included in project Issues responses. The current filter accepts exactly one literal label, not a CSV list. Reserved names Any/None or names containing commas are filtered locally only, to avoid GitLab special query semantics.

## Operator checkpoint — test on Nautilus

1. Switch to branch feature/example-hierarchy-gitlab-labels. Launch Cascade-GUI.bat --console. Complete ARC CAC/TOTP startup and Test Git.mil API. Neither startup nor hiding the console was altered.
2. Open Example Work Items, verify 46 complete synthetic records and a five-level nested parent chain. Click a Feature and Story to review their metadata and descriptions. Search and filter by type, copy one complete description, and export sample JSON.
3. In Git.mil connection, discover and select the real source projects containing your work. Click Discover issue labels. Select the actual Data-Crucible label name, not a guessed synonym.
4. Apply the label and Synchronize selected projects. Inspect the label diagnostics for source count, number of matching issues, server-side label query confirmation, and additional older issue imports.
5. Open Agile hierarchy and Nexus. Verify the actual labeled work items appear. If zero appear, inspect the suggested similar existing labels, selected projects, failed queries, and possible native GitLab Epic-vs-Issue differences.
6. Clear the label and verify that every imported work item returns. If you apply another label, synchronize again when you need comprehensive targeted server-side results beyond the cached pages.
7. Confirm Topology Review still describes only the selected scope and never silently invents parents. No Git.mil issues or labels are changed.
8. Test the normal background launcher and End session button after confirming the console-mode behavior.

## Test gates

The Windows CI suite parses all PowerShell files, runs the synthetic Git.mil label-query regression, validates the 46-item metadata and parent graph, exercises the Example tab UI without real data, and runs the existing ARC/server/Nexus regressions.

**Stop at this checkpoint.** Native GitLab Epic ingestion, full AI schema normalization and production ticket migration remain separate future phases.
