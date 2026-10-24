# Epic lifecycle and health (next iteration)

Epics are standing product/capability lines - they are rarely closed.
Feature % complete is the progress metric. Epics do not use lifetime % complete.

## Two axes (do not merge)

| Axis | Field | Values |
|------|--------|--------|
| **Lifecycle (maturity)** | `lifecycle` | ALPHA, BETA, LIVE, SUSTAINING, SUNSETTING |
| **Health (execution)** | `health` | ON_TRACK, WATCH, AT_RISK, OFF_TRACK, BLOCKED, PARKED, IDLE |

## Lifecycle

| Value | Meaning |
|-------|---------|
| ALPHA | Exploring; research-heavy; unstable contract |
| BETA | Real path in TEST/limited PROD; still changing |
| LIVE | Supported for intended users; enhancements |
| SUSTAINING | Run and enhance only; no major build Features |
| SUNSETTING | Exit path; no new capability Features |

Optional: RETIRED if a line is fully decommissioned.

## Health

| Value | Meaning |
|-------|---------|
| ON_TRACK | Work moving; no aged critical blockers |
| WATCH | Early smell; not At Risk yet |
| AT_RISK | Path to outcomes threatened |
| OFF_TRACK | Horizon outcomes unlikely without intervention |
| BLOCKED | Hard external stop (OPS/vendor); name dependency + age |
| PARKED | Explicit pause / decision gate (not failure) |
| IDLE | Live/Sustaining but no active Features this sprint |

## Dashboard shape

```
EPIC-gold | BETA | ON_TRACK | Active Features: 3 | Blockers: 0
  FEATURE-... | 60% | 9/15 pts
```

## Rules

1. Lifecycle changes rarely; health can change every sprint.
2. Parked = decision; Blocked = named dependency with age.
3. At Risk for two sprints -> recovery or de-scope note (playbook).
4. AI may suggest; human accepts lifecycle and health.
5. Story points and % complete stay on Features/Stories only.

## Cascade meta (implement next)

```
lifecycle: ALPHA|BETA|LIVE|SUSTAINING|SUNSETTING
health: ON_TRACK|WATCH|AT_RISK|OFF_TRACK|BLOCKED|PARKED|IDLE
child_iids: ...
children: ...
```

Do not put Fibonacci points on Epics.
