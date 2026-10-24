# Phase 1 correction checkpoint — exact Git.mil label filter

## Why this correction exists

The original Phase 1 incorrectly inferred Data-Crucible membership from labels, cascade-meta descriptions and project namespaces. That was not the requested simple label filter and could hide existing tickets. This correction removes those assumptions.

Now Cascade shows all imported Git.mil issues by default. When you select a label, issues are included only if the actual GitLab labels array contains that label, using case-insensitive exact matching. These three labels are distinct: team::Data-Crucible, Data-Crucible, and area::data-crucible. No naming convention is assumed.

## Workstation test checkpoint

1. Pull branch fix/explicit-gitmil-label-filter from GitHub. It has not been merged to main.
2. Start Cascade-GUI.bat --console, authenticate through the existing ARC flow and verify Test Git.mil API.
3. Discover and select the relevant source projects, including the shared CBR if it contains your work.
4. Click Synchronize selected projects ONCE. The top bar should show All labels and the Agile hierarchy should contain all imported issues. The raw source remains intact in source-snapshot.json.
5. In the top bar, choose an actual label from the automatically populated suggestions. Alternatively, click Discover issue labels to query Git.mil before first synchronization.
6. Click Apply label. The hierarchy, Nexus work grid and reports update from the local source snapshot, without another Git.mil sync.
7. Confirm that only tickets whose GitLab labels contain exactly the selected label appear. A similarly named label, matching project name or cascade-meta area is NOT a match.
8. Click Clear. Every imported issue must return immediately. The selection is persistent between launches; source data remains unchanged.
9. Check Git evidence and the Leadership report. While a label filter is active, unattributed project-level commits, merge requests and pipelines are suppressed rather than misrepresented as that team's verified activity. Clearing the filter restores the ordinary unfiltered activity.
10. If anything fails, use the existing Diagnostics session recorder, review the export for internal identifiers and upload the report.

## Exact label matching rules

- Match a string from the GitLab issue labels array by case-insensitive equality.
- Blank label means show all imported issues.
- Project names, repository namespaces, issue titles, cascade-meta fields, inferred teams and AI guesses are never used for matching.
- Only issues from operator-selected Git.mil source projects are considered.
- No API token, CAC PIN or Git.mil issue is modified to apply a label filter.
- Raw local source-snapshot.json is preserved; labels can be changed locally and instantly after at least one Git.mil sync.
- Source pagination limits still apply; if an issue was never imported, the filter cannot display it.

## Phase gate

Do not start Phase 2 hierarchy/schema normalization until the label filter has been tested against the real work items and approved by the operator. This correction stays on a review branch until the user confirms.

## Tests

- Windows PowerShell 5.1 parser and offline regressions.
- Exact label matching, similar-label separation and ignored metadata/path inference.
- Cached source preservation and all-labels restoration.
- Browser apply/clear/discover controls and guarded backend endpoints.
- Existing ARC authentication, Git.mil integration and local server regressions.
