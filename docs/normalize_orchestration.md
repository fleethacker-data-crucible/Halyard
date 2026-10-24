# Normalize orchestration (fleet, not one ticket)

## What works today (single item)
1. [R] gap report - work queue CSV
2. [P] GenAi propose for one source iid
3. [M]->5 safe dup Story body + optional Feature shell + optional Epic shell
4. Human review; delete cascade-test junk

## Target fleet path (do not auto-write production yet)
1. [R] produce gap CSV sorted by normalize_priority
2. Batch by catalog Feature (FEATURE-B-*, FEATURE-G-*, FEATURE-GO-*)
3. Ensure Epic shell once per epic, Feature shell once per feature (reuse iids - do not recreate)
4. For each Story in batch: [P] then [M]->5 (or future batch propose)
5. Human accept batch; only then apply to real iids
6. Points rollup = sum of Story points under Feature

## Reuse rule
Before creating a Feature/Epic shell, search open issues for:
- title contains catalog id, or
- Cascade meta id=FEATURE-... / EPIC-...
If found, set parent_iid to existing shell instead of creating another.

## PfW / DWI
- Feature Ready requires PfW (or Lightweight + rationale)
- Story Ready requires AC (+ DWI when a written instruction exists)
