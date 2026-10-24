# Cascade checkpoint — work-item review dialog and organization

## What changed

The Intelligence Workbench no longer puts a normalization inspector after 200 work items. Click a work-item title in the table and a large, scrollable review dialog opens immediately.

Each dialog has FIVE editable sections: (1) identity/type/title, (2) cascade-meta metadata, (3) description/acceptance criteria, (4) organization/parent relationships, and (5) literal GitLab issue labels. Each section has three explicit choices: Pending (grey), Approved (green), Needs changes (amber). The badge is accompanied by text and never relies on color alone. Editing a section automatically resets its approval to Pending; changing a parent or type also resets metadata approval because the fields must agree.

Approve is LOCAL REVIEW ONLY. Even when all five sections are green, Cascade DOES NOT update Git.mil. Use Export this review for one issue or Export review JSON for the reviewed issue set. Exported JSON can contain internal issue titles and body text, so inspect it before sharing. Local decisions are memory-only for the current imported snapshot and are cleared on a fresh intelligence run or label/snapshot change.

## Neutral vocabulary

The UI says Work item type instead of Declared type, and Type not assigned instead of Unclassified. Existing internal API enum and finding code are preserved for compatibility with older scripts, while displayed wording uses non-security-classification terminology. Type selection still requires your decision when the source has no explicit type; a CLI heuristic is only a candidate.

## Organization / parent-child review

Cascade now includes a separate deterministic relationship reviewer with the same imported issue-label and project boundary. It respects Theme -> Epic -> Feature -> Story -> Task, unique stable canonical IDs, explicit parent metadata and cycle safeguards.

Parent candidates are ranked using distinctive title tokens, scoped domain/component/product/service labels and explicit theme lineage. Same project or generic team label is NOT enough evidence by itself. Each suggestion shows its candidate parent ID, title, type and supporting reasons; strong/moderate/weak confidence describes evidence quality, never permission to create a relationship.

If nothing matches, Cascade leaves the relationship unresolved and the reviewer can request changes. It deliberately does NOT copy the older CLI script's first available Feature or placeholder fallback. A previously declared parent outside the selected label is shown as unverifiable in the current scope, without disclosing the excluded ticket's details. Manual parent selection only offers unique, correctly typed, same-project items in the active import.

For issues without an explicit work-item type, a heuristic type may help find **potential** parent candidates, but they appear in the dialog only after the reviewer first selects a work-item type. No source work-item type or parent is automatically written or designated.

## Test on Nautilus

1. Fetch the feature branch and launch Cascade-GUI.bat --console. Confirm ARC/CAC startup and Git.mil connectivity.
2. In Git.mil connection select projects, discover the actual Data-Crucible issue label, apply it, then sync.
3. Open Intelligence Workbench and click Run intelligence review.
4. Click an item near the top of the 200-item list. Confirm the popup appears immediately and you do not need to scroll down.
5. Approve Identity. Then edit its title; the Identity decision must return to Pending. Use Needs changes for an unresolved issue.
6. Select an item with a missing parent. Inspect candidate parent IDs and their evidence. Confirm that no weak first-item fallback is auto-selected. Choose a parent explicitly if it is valid.
7. Review metadata, description, organization and labels. Confirm edits to type/parent reset the related approvals, that green requires validation, and that labels preserve your exact selected scope.
8. Close and reopen an issue: local choices remain for the current review. Export the local reviewed proposal package, then re-run intelligence: old decisions must be cleared.
9. Verify Leadership Dashboard and Topology Review now display Type not assigned, and that existing internal issue code conventions remain compatible.
10. Inspect Git.mil directly to confirm no ticket or label was changed; this delivery has no write-back route.

## Limitations and next phase

This is a deterministic evidence-first review, not an AI semantic entity matcher. Imported issues beyond the configured 200-item preview or outside the selected label may have missing context, so no claim of an exhaustive hierarchy is made. A future authorized phase can add AI-ranked candidates and a diff/approval/write-back workflow with independent policy checks, idempotence and rollback.

Do not merge until Windows CI and approved Nautilus workstation tests pass.
