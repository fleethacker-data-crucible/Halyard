# The crew: an agentic product owner and scrum master

Halyard already did the product owner's paperwork well: it audited the hierarchy, drafted sections, sized, tagged, planned
and reported, each behind a button and each human-gated. It was not agentic. Nothing decided what to do next, nothing ran
the sprint cadence, nothing ran unattended across repositories, and voice was limited to the browser's own speech. This page
records that review, what this change builds, and what is left.

## Review: where Halyard fell short

| # | Gap | Effect | Status |
|---|---|---|---|
| 1 | No planner. Every flow is a button; the goal runner stops at the first step with proposals and waits. | A person has to know which of 60 commands to run, in which order. | Built: the crew |
| 2 | No scrum master. Sprint review existed, but no burn against the calendar, no standup list, no ceremonies, no retro prompts. | Halyard reported on the backlog but did not run the sprint. | Built: scrum brief |
| 3 | Voice was the browser's own speech: Windows voices out, and dictation only where the browser recognises on-device (rarely). | Read-aloud sounded robotic; talking to Halyard mostly did not work. | Built: TalkForge and Binnacle |
| 4 | One repository per launch, and only a person could start it. The weekly report schedule only emailed one dashboard. | No weekly sweep across an organisation or a GitLab group. | Built: weekly watch and Dogwatch |
| 5 | Proposals only target sandbox items; a source repository needs Intake first. | A crawled repository is read-only: analysis, but no proposals. | Open: intake on watch |
| 6 | Every proposal needs a click to approve, even deterministic, reversible ones. | The PO loop cannot close on its own, even in the sandbox. | Open: autonomy ladder |
| 7 | `Halyard.ps1` runs only `commands`; there is no `halyard <noun> <verb>` CLI or MCP server. | Scripts and other agents call the API by hand. | Open |
| 8 | No memory: working agreements, definition of done and decisions live in people's heads. | The crew cannot hold the team to its own rules. | Open |

## What this change builds

### The crew (`halyard agent ask`)

`POST /api/agent/ask` takes `{ message, history }` and returns `{ answer, calls, batches, source }`.

- **Tools are the command registry.** The crew may call every read command and the commands that only make proposals
  (`goal ready`, `metadata propose`, `parents propose`, `plan preview`, `plan propose`, `intake preview`, `handoff preview`,
  `promote preview`). `CrewAgent.Tools` filters `CommandRegistry.All`, so a new read command becomes a tool with no extra work.
- **It can never write.** Writing, async, approving, applying, rolling back, promoting, seeding and settings commands are
  refused before they reach the router, and the model is told to name the batch a person should review. `CrewTests` pins this.
- **Same pipeline as the page.** A call runs in process through `HalyardHost.DispatchAsync`, which builds a request with this
  session's token and goes through the same router, host check and error mapping as the browser.
- **Protocol.** The model replies with one JSON object, `{"call": {...}}` or `{"answer": "..."}`. JSON in text rather than
  native tool calls, so any Fairlead route works, local models included. At most six calls per turn; results are cut to 6000
  characters.
- **No model, still useful.** Without a Fairlead key (or when Fairlead is down) the crew answers from the scrum brief, picking
  the part the question is about (blockers, risks, retro, refinement, ceremonies, next actions) and says why.

### The scrum brief (`halyard scrum brief`)

`ScrumMaster.Build` is deterministic: burn against a straight line with 15% slack, blockers (escalated first), stale sprint
stories (no change in 3 days), sprint stories nobody owns, the refinement agenda (open stories and features that fail their
definition of ready), risks (behind, over capacity, thin ready backlog, escalations, features at risk), retro prompts from
the last finished sprint, the ceremony calendar with an agenda and a command each, and next best actions. `spoken` is the
brief as plain sentences for the voice.

### The weekly watch (`halyard agent watch`)

`POST /api/agent/watch` runs the backlog-ready goal (proposals only), builds the brief and, with `useModel`, asks the model
for a six-sentence note to the team and leadership. It writes `output/watch/watch-<id>.json` and `.md`. On a source
repository, where proposals cannot be made, the goal step is skipped with the reason and the brief is still produced.
Nothing reaches a repository: applying stays a person's job. `GET /api/agent/watches` lists recent watches.

[Dogwatch](../../Dogwatch) (local repository next to Halyard) calls this weekly for every active repository it finds on
GitHub or on a GitLab server such as web.git.mil.

### Voice: TalkForge and the Binnacle orb, as Lazarette does it

- **The orb.** Binnacle (`web/public/vendor/binnacle`, copied from edge-ops `ui-kit/binnacle`) floats over every page. Click
  it to talk; click again to stop and send. It shows `listening`, `thinking` while the crew works, `speaking` with the real
  audio level, and `approval` while a batch the crew made is waiting on the Proposals page. It is told a state, a level and a
  short caption, never the conversation.
- **Spoken answers.** `POST /api/voice/speak` sends one sentence at a time to Fairlead's `/v1/audio/speech` with Halyard's
  own Fairlead key and `X-Fairlead-Privacy: local`; Fairlead routes it to TalkForge on this PC and Halyard returns the WAV.
  Unlike Lazarette, Halyard needs no TalkForge token: every model call already goes through Fairlead, so speech does too.
  When TalkForge cannot be reached, the browser's installed local voice stands in (never a cloud voice).
- **Voice input.** Whisper tiny.en runs in a worker in the page (`web/src/workers/stt.worker.ts`), with the model and runtime
  served read-only from TalkForge's assets at fixed `/stt/` paths (the same list Lazarette serves). Audio never leaves the tab.
  Without those files, the browser's recogniser is used only when it runs on this computer; otherwise typing.

| Setting | Default | Meaning |
|---|---|---|
| `HALYARD_VOICE_ROUTE` | `speech` | The Fairlead route whose target is TalkForge (`http://127.0.0.1:25009`, TalkForge token as the key) |
| `HALYARD_VOICE` | TalkForge's default | A Piper voice id, for example `en_US-libritts_r-medium` |
| `HALYARD_VOICE_SPEED` | `1.0` | 0.5 to 2.0 |
| `HALYARD_TALKFORGE_PATH` | `..\TalkForge`, then `%USERPROFILE%\Projects\TalkForge` | Where the Whisper files are (`populate-assets.ps1 -Only 'stt/*'`) |

The page CSP gains `script-src 'self' 'wasm-unsafe-eval'` (Whisper's WASM), `worker-src 'self'` and `media-src 'self' blob:`.

## What is left, in order

1. **Live check.** A real Fairlead route to TalkForge, Whisper files in place, and the crew against a real model on a seeded
   Ballast. Prompts will need tuning on real tickets (roadmap items 1 and 2).
2. **Intake on watch.** Option for the watch to run Intake preview on a source repository and save the drafts as a proposal
   batch for Ballast, so crawled repositories get proposals too, still gated.
3. **Autonomy ladder.** `HALYARD_AUTO_APPLY=metadata,parents` (sandbox only, off by default): the watch applies deterministic,
   reversible kinds itself, journals them and offers rollback. Model-made kinds stay manual.
4. **Team memory.** `config/agreements.md` (definition of done, working agreements, escalation rules) read into the crew's
   prompt and the brief's checks.
5. **CLI and MCP.** `halyard <noun> <verb>` from the registry (Dogwatch could then drop its HTTP glue), and an MCP server
   generated from the same list.
6. **Native tool calls** when the Fairlead route supports them, keeping the JSON protocol as the fallback.
