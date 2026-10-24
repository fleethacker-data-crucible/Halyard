# Phase 2A checkpoint — topology review (read-only)

This release makes existing Git.mil issues easier to inspect **without normalizing or changing them**. Phase 1 exact issue-label filtering and ARC startup are unchanged.

## New in the Agile delivery hierarchy

The **Topology review** panel appears under the existing tree after a Git.mil sync. It reports, for the **currently selected label**:

- Unknown or conflicting types in cascade-meta vs. type:: labels
- Missing stable Cascade IDs; the project/IID fallback remains usable
- Duplicate declared IDs across imported issues
- Missing, ambiguous, self-referential or cyclic explicit parent references
- Parent-type mismatch for an Epic / Feature / Story / Task
- **Parent outside current label** when the parent exists in the original imported snapshot but is excluded from the current view
- **Parent unresolved in imported source**, which could also mean that the parent belongs to an unselected project or was omitted by pagination

Each finding has an explanation and a link to the original issue (Git.mil host only). Severity filtering and **Export review JSON** are available.

## Workstation acceptance checkpoint

1. Run the approved Windows PowerShell launcher from branch feature/phase2a-topology-audit. Verify **ARC/CAC authentication and Test Git.mil API** are unchanged.
2. Synchronize the CBR and any other selected source projects with **All labels**. In **Agile delivery hierarchy**, check that the original tree still appears, with the new Topology review beneath it.
3. Verify that the audit counts describe the imported issues and that missing metadata yields findings, *not* new guessed links.
4. Select the existing Data-Crucible Git.mil issue label; click **Apply label**. The review must update immediately from the saved source snapshot, without another network sync.
5. If a child's declared parent has another label, the audit should show **PARENT_OUTSIDE_LABEL** and must **not** expose that excluded parent's title.
6. Try **Errors**, **Warnings**, **Unclassified** and **Parent outside label** filters. Confirm the review table updates and that the Git.mil issue links open only the approved host.
7. Export the JSON review. Ensure it lists only findings from the currently selected issue label and includes no credential values. Check any internal project/issue identifiers before sharing.
8. Click **Clear** on the issue label. The full original work-item list and corresponding unfiltered audit should return. No issues are written to Git.mil.
9. Confirm the original local source-snapshot.json has not changed and that the ARC session, background engine, and End session button still work.

**Stop here.** Don't begin AI classification, normalization approval workflows, or migration until this review is accepted.

## Limitations

- The audit only uses explicitly declared cascade-meta and type:: labels. It never fabricates parents, issue types, IDs, or acceptance criteria.
- Native GitLab work-item hierarchy and issue links are not yet ingested; a finding does not prove GitLab lacks a parent relationship.
- The imported dataset can be truncated by API page limits. PARENT_UNRESOLVED means the reference was not found in the imported snapshot, not that the source item definitely doesn't exist.
- A label-specific review only includes its selected issues. Only existence of excluded parents is reported, not their details.
- This checkpoint is entirely read-only and does not call GenAI or Advana.

## Automated gates

- Windows PowerShell 5.1 syntax parse and existing offline suite
- Synthetic CBR hierarchy with valid and invalid metadata, conflicting labels, duplicate IDs, cycles, out-of-label parent and zero-match scope
- Existing ARC, network and local startup regressions
- Browser smoke: topology panel, severity filter, escaping and approved Git.mil links
