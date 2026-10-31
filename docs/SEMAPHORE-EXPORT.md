# Backlog health for Semaphore

Halyard writes its backlog health as four CSV files that [Semaphore](https://github.com/fleethacker-data-crucible/Semaphore) loads as
data sources (Data, add CSV files). It is a plain file handoff: read-only towards the backlog, local to the machine, nothing is sent
anywhere. Schema version 1.

```
POST /api/export/semaphore            writes output/exports/semaphore/{health,items,features,velocity}.csv from the current snapshot
GET  /api/export/semaphore/{table}    the same table as text/csv
```

Both take an optional `label` (the same filter as the other pages). Synchronize first; the export reflects the snapshot, not the live
backlog.

## Format

UTF-8 without a BOM, CRLF line ends, a header row, RFC 4180 quoting (a cell with a comma, quote or line break is quoted and quotes are
doubled). Dates are UTC ISO 8601 (`2026-10-30T09:15:00Z`). Booleans are `true`/`false`. Lists inside a cell are joined with `;`. Empty means
unknown. A text cell that starts with `=`, `+`, `-`, `@` or a tab is prefixed with `'` so a spreadsheet will not run it as a formula.
Column names only ever grow at the right-hand end within a schema version.

**health**: one row.
`generated_at, repository, total_items, needs_review, missing_parents, stories, stories_with_points, features, features_with_effort, features_active, features_on_track, average_velocity, ready_stories, ready_points, blockers`

**items**: one row per work item.
`key, number, type, id, title, state, parent_id, parent_key, theme, area, points, effort, sprint, labels, needs_review, deficiencies, missing_meta, updated_at, url`
(`type` is the level token such as `STORY`; `deficiencies` are the stable codes, for example `story_missing_acceptance_criteria`.)

**features**: one row per feature.
`id, title, epic, theme, owner, target, stories, stories_done, points, points_done, percent, blocked, status, reason`
(`status` is `done`, `ontrack`, `atrisk` or `parked`; `reason` states the rule that produced it.)

**velocity**: one row per shown sprint.
`sprint, number, points_planned, points_done, stories_done, is_current`

## Checks

`SemaphoreExportTests` pins the column lists above, the quoting and formula rules, and that the tables are built from a synthetic
backlog. Not checked: loading the files into a running Semaphore (a hand check: add the four files under Data and build a chart on
`items.deficiencies` or `velocity.points_done`).
