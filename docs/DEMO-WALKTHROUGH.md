# Demo walkthrough (offline, fake mode)

A script for recording a demo video of Halyard that touches **nothing real**: a fake GitHub and a fake model run on this computer, the data is the synthetic
catalogue, and the only repository written to is an in-memory sandbox. No account, token, mailbox or network is needed beyond `127.0.0.1`.
About 8 minutes at a relaxed pace. Steps marked **(PR #n)** need that pull request merged; if it is not, skip the step.

## Set up (off camera)

Needs Node.js on PATH and Windows PowerShell 5.1. From the repository root, in two terminals:

```powershell
node tests\fakes\fake-github.mjs 8790
```

```powershell
$env:HALYARD_PROVIDER = 'github'
$env:GITHUB_API_URL = 'http://127.0.0.1:8790'
$env:GITHUB_TOKEN = 'fake'
$env:HALYARD_SANDBOX_REPO = 'fleethacker-data-crucible/Halyard-Ballast'   # the name only; the fake never leaves this machine
$env:HALYARD_SOURCE_REPO = 'sun-of-anton/raw-backlog'
$env:HALYARD_WRITE_DELAY_MS = '0'
$env:HALYARD_LLM_PROFILE = 'openai'
$env:LLM_URL = 'http://127.0.0.1:8790/v1/chat/completions'
$env:LLM_TOKEN = 'fake-token-0123456789'
.\Launch-Halyard.bat --console
```

Check before recording: the URL printed by the launcher opens the Overview, the status strip says **sandbox** mode, and the Connection page shows the provider
as reachable. Use a clean browser window at 1280 x 800 in the Night watch theme (Fleet UI 0.2). The same fakes drive `tests\e2e\Run-E2E.ps1`, which is the
non-interactive version of the first half of this script.

## The story, scene by scene

Page names are the real ones; exact button wording was written from the source and not checked against a recording, so adjust it to what is on screen.

**1. Safe by default (30 s).** Overview and Connection. Say: "Halyard is a product owner for a backlog. It reads, proposes, and a person approves. It can only write to
the sandbox." Point at the sandbox/production indicator and the write policy on **Diagnostics > Write audit** (empty).

**2. Seed a backlog (45 s).** **Sandbox** > Seed. Choose 30 items with gaps, **Seed**. Watch the job progress, then the counts. Say the items are labelled
`halyard-synthetic` and only those can be deleted.

**3. See the problems (60 s).** **Review**: the Definition-of-ready gaps tab (the tabs are keyboard-reachable; show arrow keys, **PR #53**), then a missing-parent
finding. **Hierarchy** shows the tree with rollups.

**4. Fix them with proposals (90 s).** **Proposals**: **Get the backlog ready** **(PR #49, #50)** runs the ordered steps and stops at the first batch.
Open the batch, read a diff, **Approve all (high confidence)**, **Apply**. Show that the batch and the sandbox changed and the review gaps closed.

**5. Undo (45 s).** **Proposals > Applied changes** **(rollback, on main; panel in #50)**: **Undo** shows a preview with what will be restored and anything a person
edited since. Confirm, and show the earlier text come back and the proposals return to Pending.

**6. Draft and size with the fake model (60 s).** **Proposals > New analysis**: Estimate story points, then Draft sections. Say the model is a local fake here;
in real use Halyard sends to the Fairlead router at `127.0.0.1:8776` **(PR #43)**. Every model output is validated against the backlog.

**7. Plan the sprint (60 s).** **Plan**: Preview plan. Tick **Commit what the team delivered (sprint review)** **(PR #35)** and show the commitment change and the
slipped stories placed first. Show **Refresh** on the Team panel **(PR #36)** and say skills come from consented Shellback cards only.

**8. Leadership (60 s).** **Leadership**: feature progress, velocity, capacity, blockers. **Read aloud** **(PR #51)** with a local voice. **Share to Teams or Slack**
**(PR #38, #50)**: click **Preview message** only; there is no webhook configured, and the panel says so.

**9. Bring work in (45 s).** **Intake**: copy a raw ticket from the fake source repository into the sandbox. Then **Dictate or type a backlog note** **(PR #47, #51)**:
type a line, **Preview drafts**. Say hand-offs from PacketRat and Eight Bells use the same path **(docs/HANDOFF.md)**.

**10. The production door stays shut (30 s).** **Sandbox > Promote to production** **(route on main; panel in #50)**: it says **off** and why. Say it needs
production mode, an environment flag, a typed phrase and a diff preview, and that it was never run against a real repository.

**11. Close (15 s).** **Diagnostics > Write audit**: every write listed, all against the sandbox. Stop the engine from the status strip.

## Safe to say, and not to say

- Say: everything here is synthetic; the model is a fake; Halyard proposes and people decide; writes go through one gateway to a sandbox.
- Do not say: that it has been run against a real model, a real GitLab, a real Teams or Slack, or a real Shellback; none of that has happened. Those are
  listed in `ROADMAP.md` as needing a live check.

## After recording

Stop the engine, stop the fake (Ctrl+C), and delete the `output\` folder if you want a clean next take. Nothing else is left behind.
