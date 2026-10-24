# Ownership roles (Epic / Feature / Story)

| Role | Default | Purpose |
|------|---------|---------|
| **Product Owner** | Cascade AI | Backlog structure, prioritization suggestions, package hygiene |
| **Technical Focal** | unassigned | Architecture, stack, scripts, implementation path |
| **SME** | unassigned | Domain correctness (metric definition, ops process, data meaning) |

## Additional fields to consider

| Field | Level | Purpose |
|-------|-------|---------|
| **Ops contact** | Feature/Story | When IAM/LF/network is in the path |
| **Environment** | Story | TEST / PROD scope |
| **Secrets** | Feature/Story | Never put credentials or tokens in tickets |
| **Sponsor** | Epic | Program leadership consumer |
| **Lifecycle** | Epic | ALPHA BETA LIVE SUSTAINING SUNSETTING |
| **Health** | Epic | ON_TRACK WATCH AT_RISK OFF_TRACK BLOCKED PARKED IDLE |

## DoR / crawler model
Structural DoR (parent + AC + points) does not require a human accept ritual.
If a human is confused by DWI, they set a status/label the weekly crawler treats as "needs SME" and notifies the SME.
