# Changelog

All notable changes to Halyard. The version is the root `VERSION` file; the engine, the project file and the web package
must match it (`EngineSourceTests.VersionMatchesTheProjectFile`).

## 1.2.0 (unreleased)

### The crew: an agentic product owner and scrum master
- **Crew** (`halyard agent ask`): plans through Fairlead and calls Halyard's own commands as tools; reads and proposes, and
  refuses every writing command, so approving and applying stay with a person. Without a model it answers from the scrum brief.
- **Scrum brief** (`halyard scrum brief`): burn against the sprint, blockers, stale and unowned work, risks, ceremonies with an
  agenda, retro prompts and next best actions.
- **Weekly watch** (`halyard agent watch`): the ready goal, the brief and a short note, saved under `output/watch/`; the local
  Dogwatch runner calls it across GitHub organisations and GitLab groups.
- **Voice**: the Binnacle orb (from edge-ops). Spoken answers through TalkForge's neural voices, started and stopped by Halyard
  as Lazarette does (port 25008, a token made for each run); off unless turned on or asked by voice. Voice input with Whisper in
  the page, from TalkForge's files. Settings, Voice picks a voice and plays a test sentence.

### Desktop, like Lazarette and Semaphore
- Starts hidden and opens in its own Edge or Chrome app window; the console shows only for the Lazarette unlock.
- Tray icon (Open, output folder, Quit). A second launch brings the running window forward. A desktop shortcut on first launch.
- Port block 8806-8808 (proposed in edge-ops PORTS.md), moved off PacketRat's 8771-8773, with no fallback outside the block.
  `/__fleethacker/id` and `/__fleethacker/shutdown` per the fleet port contract.
- Startup errors in a message box instead of a console prompt.

### Shell
- Welcome page, as Semaphore has it: the emblem, one primary way in that follows the engine's state, the other ways in, and
  "Open straight to the overview next time".
- Ctrl+K command palette, keyboard shortcuts (`?`, `.`, `g` then a letter), a `?` help dialog that lists them all.
- An error boundary per page, page titles in the window title, and work item titles shown without the `STORY ::` prefix the
  level chip already shows.

### Hierarchy, appearance and the orb
- **Hierarchy levels are easy to tell apart**: five distinct hues (brass Theme, violet Epic, blue Feature, green Story,
  slate Task) instead of a ramp of blues, level chips tinted in their hue with the full word, Themes as banded headings,
  Epics and Features in heavier type, indent guides, a legend with counts, and "Show to" a level (Themes ... Tasks).
- **The graph** puts each level in its own column with a pinned heading, draws larger tinted cards, folds branches
  (+n badges), draws the selected item's chain solid, and has zoom, fit and center buttons. It opens folded to Features.
- **Fixed: Hierarchy kept showing deleted sandbox items.** A page opened while a seed, teardown or apply was still running
  kept the old snapshot, because the job's poller lived on the page that started it. The shell now refetches every
  snapshot view when the engine reports a new snapshot.
- **Settings, Appearance**, as Lazarette and Semaphore have it: theme presets, Surprise me, export and import (theme files
  move between the three apps), mode, accent, tint, glow, shape, density, type, motion, pattern, high contrast, and
  Reduce motion. The old Night watch / Chart room choice carries over.
- **The crew orb follows Lazarette and Semaphore**: 130 px, no menu on hover, look and colours chosen in Settings, click or
  press-and-hold to talk, can be turned off (the Crew button stands in), caption on hover only. Settings, Voice adds voice
  input on/off and "Send when I stop talking".

### Fairlead
- **One clear failure when Fairlead is down.** Before a model job, Halyard checks Fairlead's `GET /v1/health`; if nothing
  answers, the job is refused with `FAIRLEAD_OFFLINE` instead of failing once per item.
- **Start Fairlead from Halyard**: a banner on any page when a model job was refused, and a status line with a Start button
  in Settings, Model calls. It runs Fairlead's own tray launcher (`HALYARD_FAIRLEAD_PATH`, else next to Halyard, else
  `%USERPROFILE%\Projects\Fairlead`) and waits for it to answer. `halyard fairlead status` and `halyard fairlead start`.

### Engineering
- Code splitting: pages load on demand; the first load fell from 608 KB to about 375 KB. `npm run size` checks a budget in CI.
- Playwright browser tests in Edge against a real engine and the fake GitHub (`npm run e2e`), with screenshots in both themes.
- PSScriptAnalyzer in CI with the fleet settings. One `VERSION` file, and this changelog.

## 1.1.0
- Fairlead as the model endpoint, the command registry, Semaphore export, sprint review, rollback, multi-step goals, report
  delivery by webhook, shared proposal state and production promote. See ROADMAP.md.

## 1.0.0
- First release: the work-item model, the sandbox, analysis, proposals, planning, intake and the leadership report.
