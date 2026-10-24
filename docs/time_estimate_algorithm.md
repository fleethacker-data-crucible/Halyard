# Effort fields on GitLab (web.git.mil)

## What works reliably

**Weight** on the issue/work item = Story points (Fibonacci).
- REST: `PUT /issues/:iid` with `{ "weight": 3 }`
- glab work-item: `--weight`

Many work-item UIs show **Weight**, not classic Time tracking Estimate.

## Time estimate

Classic Issues API: `POST /issues/:iid/time_estimate` with form `duration=8h`.
On some GitLab work-item configurations this endpoint is missing or the sidebar only shows Weight.
Cascade sets **weight** from points always; time_estimate is best-effort.

## Planning hours (body only if API missing)

| Points | Hours |
|--------|------|
| 1 | 2 |
| 2 | 4 |
| 3 | 8 |
| 5 | 16 |
| 8 | 24 |
| 13 | 40 |

Feature Effort: Small | Medium | Large | Extra Large (not Weight).
