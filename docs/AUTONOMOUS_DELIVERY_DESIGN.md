# Cascade: Invisible Agile — engineering design

## Product contract
Developers work in Git. Cascade discovers engineering evidence, proposes a coherent Agile hierarchy, maintains validated work items, and creates source-linked leadership briefs. Developer activity is **not** a time clock or individual performance score.

## Delivered in this package
- Read-only local Git discovery (multiple roots, commit and changed-file evidence).
- Offline JSON proposal plan and management activity brief.
- Conservative classification: no invented story, hours, or completion.
- Fail-closed sandbox and explicit production-write permission.

## Planned production architecture
1. **Ingestion**: GitLab webhooks plus scheduled API reconciliation, cursor checkpointing, backfill, deduplication and rate-limit handling. A central approved runner is needed for unattended operation.
2. **Evidence graph**: immutable provenance for commits, merge requests, CI, deployments, documents and work items.
3. **Work reconstruction**: cluster commits across branches/repositories; identify existing work items before creating new ones; ask for feedback when confidence is low.
4. **Canonical work-item schema**: type-specific templates, deterministic render/parse round trips, taxonomy rules, migration and validation.
5. **Agent planner**: goal queue, tool registry, scoped permissions, bounded execution, retries, checkpoints, verification and human escalation.
6. **GitLab reconciler**: dry-run changesets, idempotency keys, optimistic concurrency, conflict handling and auditable writes.
7. **Management intelligence**: deterministic metrics, coverage and evidence thresholds, risk narratives with citations, freshness warnings and scheduled reports.

## Safety and acceptance
- Sandbox autonomy first; production changes require explicit opt-in and bounded permissions.
- Never infer actual hours worked from commit timestamps.
- Do not auto-close based on commit message alone: require acceptance evidence, CI results and policy-specific definition of done.
- No secret tokens in logs or reports; use ARC or approved credential source.
- Don't treat activity volume as a measure of individual productivity.
- PS 5.1 is the minimum runtime; no mandatory Python/Node installation.
- **Unattended execution is not included in this increment**: it requires an approved runner and GitLab integration testing.

## Run
```powershell
.\scripts\Invoke-EngineeringDiscovery.ps1 -Roots @('C:\Projects') -SinceDays 30
```
Review `output/discovery_plan.json` and `output/engineering_activity_brief.md`. This command never writes to GitLab.

## Validation
Run existing `powershell.exe -NoProfile -File tests\Invoke-CascadeTests.ps1 -Suite Menu` on Windows PowerShell 5.1. Additional offline checks are in `tests/Test-Discovery.ps1`.