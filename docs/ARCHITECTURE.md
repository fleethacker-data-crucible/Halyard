# Architecture

Halyard reads a backlog from a Git host, checks it against the work-item model, proposes fixes, and
writes approved fixes to a sandbox. It runs as one local process on the operator's machine and is
used through a browser on `127.0.0.1`.

## Constraints that shape everything

| Constraint | Consequence |
|---|---|
| Target workstation runs Windows PowerShell 5.1 on .NET Framework 4.8; no installs, no admin | The runtime is PowerShell + the engine's C# source + static web files. No Node, Python, services or installers on the workstation. |
| Application control blocks loading custom DLL files (confirmed 2026-10-26) | The engine ships as C# source and Windows PowerShell 5.1 compiles it in memory at launch (`Add-Type`). No DLL is shipped or loaded from disk, no third-party assembly is used. |
| On some workstations, smart card + TOTP happen inside a PowerShell vault (Lazarette) in a console | PowerShell stays the launcher and owns the vault unlock. Everything after that is C#. |
| Two Git hosts: github.com for development, a self-hosted GitLab for real work | One provider-agnostic work-item model; hosts are adapters. |
| Production backlog must never be written by accident | Every write goes through one gateway with a sandbox allow-list. |
| Build happens off the workstation | Engine source and the built UI ship in a bundle; nothing is fetched at runtime. |

## Stack

| Layer | Choice | Why |
|---|---|---|
| Launcher | `Launch-Halyard.bat` → `powershell.exe -File scripts\Start-Halyard.ps1` | The only entry point the workstation already allows. |
| Shell | PowerShell module `module/Halyard` | Reads `config/halyard.env`, unlocks Lazarette once, compiles the engine, opens the browser, hides the console. Nothing else. |
| Core | C# 5 source `src/Halyard.Core`, compiled in memory at launch by `Add-Type -Path *.cs -CompilerParameters` (the inbox .NET Framework 4.8 compiler, C# 5 only) | Types, real concurrency, testable logic, one write gateway. References framework assemblies only (`System`, `System.Core`, `System.Net.Http`, `System.Security`). PowerShell 7 compiles the same files. The tests compile them too. |
| JSON | `Halyard.Core.Serialization`: a small hand-written reader, writer and reflection serializer | Framework only (no Newtonsoft.Json). Writes the same JSON text Newtonsoft did; `fixtures/wire` pins the API responses and file formats. |
| Local server | `TcpListener` on 127.0.0.1 inside the core | `HttpListener` needs HTTP.sys URL reservations a standard user lacks. One task per connection; long work runs as background jobs. |
| UI | React 19 + TypeScript, Tailwind CSS 4, d3 (hierarchy, zoom, shape), zod | Built to static files by esbuild + Tailwind's compiler. Validates every response. |
| Storage | JSON files under `output/` (snapshots, proposals, seed manifests, transcripts) | Readable, diffable, no native database driver to get approved. |
| Tests | xUnit (core), Pester 5 (PowerShell), node:test (UI), an offline end-to-end loop | Each layer tested in its own tool; the end-to-end loop runs against a fake GitHub. |

Dropped from the earlier plan: Rust→WASM (no compute in the browser that needs it), service workers
(a page served by a local process does not need offline caching, and stale caches cause bugs),
IndexedDB as source of truth (the engine owns state), and a dev server (the build is the whole story).

## Components

```
Launch-Halyard.bat
  └─ scripts/Start-Halyard.ps1
       └─ module/Halyard (PowerShell)
            ├─ config/halyard.env → process environment
            ├─ vault bridge (Lazarette; GitLab only, or HALYARD_USE_LAZARETTE=1) → GITLAB_TOKEN, GITHUB_TOKEN
            └─ Halyard.Core (src/Halyard.Core/*.cs, compiled in memory at launch)
                 ├─ Host        loopback HTTP server, router, jobs, session token, static UI
                 ├─ Providers   GitHub REST/GraphQL, GitLab REST v4  → IWorkItemProvider
                 ├─ Safety      WritePolicy + WriteGateway (the only path to a writer)
                 ├─ Model       WorkItem, HalyardMeta, RepositoryRef
                 ├─ Schema      taxonomy, sizing policy, definition-of-ready checks, body composer
                 ├─ Analysis    topology audit, hierarchy tree, parent candidates, review report, delivery (feature progress, velocity, blockers) + HTML report
                 ├─ Planning    team.json / skills.json / Shellback cards, sprint calendar, capacity-and-skills sprint planner
                 ├─ Intelligence sizing, refinement drafts, skill tagging, vision drafting, intake classification (model); metadata repair, parents, planner, deterministic fallbacks; manual edits, proposal store, projection + applier
                 ├─ Llm         OpenAI chat-completions client for Fairlead (loopback only) and the Credential Manager key reader
                 ├─ Synthetic   deterministic catalogue, sandbox seeder, teardown
                 └─ Snapshots   point-in-time copies the analysis runs on
```

## Provider-agnostic work items

A `WorkItem` is the same object whether it came from GitHub or GitLab: number, title, body, labels,
state, URL, plus the parsed `~~~halyard-meta` block. **The meta block is the source of truth** for type,
canonical id and parent on both hosts, because both store an issue body and labels the same way.
`type::story`-style labels are a fallback for type only. Native links (GitHub sub-issues) are written as
a convenience when the host supports them, never read as truth.

Adding a host means implementing `IWorkItemProvider` (read side) and `IWorkItemWriter` (write side).
The analysis, UI and tests do not change.

## Write safety

1. `IWorkItemProvider.OpenWriter` is called only by `WriteGateway`, and providers do not implement the writer
   interface themselves, so a provider cannot be cast into a writer (Pester rules enforce both).
2. `WriteGateway.Open` asks `WritePolicy.Assert` first. Sandbox mode allows only the configured sandbox
   repository. Production needs `HALYARD_WRITE_MODE=production`, `HALYARD_ALLOW_PRODUCTION=1` **and** a
   per-operation opt-in in code; the browser cannot set any of these.
3. The configured source repository is added to a protected list whenever it differs from the sandbox.
   Every real write run opens through `OpenVerifiedAsync`, which compares the host's numeric repository ids,
   so one project spelled two ways (`group/project` vs its numeric id, or a renamed GitHub repo) is still caught.
   `HALYARD_PROTECTED_REPOS` adds further never-write repositories.
   Repository identifiers are validated (no `.`/`..` segments) and escaped before they reach a URL.
4. Provider, API URL, source and sandbox come only from `config/halyard.env`. The browser can change the
   label filter and the Fairlead route name, nothing that decides where Halyard reads, writes or sends
   tokens. Model calls go only to the Fairlead address (`HALYARD_FAIRLEAD_URL`, file-only), which must be this
   PC: any other host is refused before a request is made. Halyard's Fairlead key is not a setting at all; it is
   read from Windows Credential Manager (`Fairlead/Halyard`) for each call. The sandbox is read only from
   `HALYARD_SANDBOX_REPO`.
5. Everything created is stamped with the `halyard` label; synthetic items also get `halyard-synthetic`
   and `halyard-run::<id>`.
6. Deletes re-read the item first and check `halyard-synthetic` on the live copy, not the caller's. On GitHub
   the node id is confirmed through GraphQL to belong to the sandbox and the expected number before
   `deleteIssue` runs. A full wipe needs the typed phrase `DELETE <repository>`.
7. Every attempt, successful or not, is recorded in the write audit (Diagnostics → Write audit).
8. Applying proposals re-reads each item, checks it is still the same canonical id, and rewrites meta, the visible `### Halyard` mirror and any
   `## Sizing` section together, so the body never disagrees with itself. `ProposalApplier.Project` is the
   one function that turns an item plus a proposal into the new body; the preview endpoint shows its
   output and the applier writes it, so the diff a reviewer approved is exactly what lands.

## Local host security

- Binds to `127.0.0.1` only and rejects non-loopback peers and non-loopback `Host` headers.
- Every `/api` call except `/api/health` needs the random per-session token in the `X-Halyard-Session`
  header. The launcher passes it once in the page URL; the UI moves it to `sessionStorage` and removes it
  from the address bar. API URLs never accept it. Other local processes and other browser tabs cannot call
  the API. The token is not written to the runtime file in `logs/`.
- Cross-origin requests are refused; there are no CORS headers. HTTP redirects are not followed, so a
  token or a write can never be forwarded to a URL nobody configured.
- Ending the session needs the token and an `X-Halyard-Intent: end-session` header.
- Static files are served from one directory with path normalisation; no directory listing.
- Tokens live only in process memory (the Fairlead key is read from Credential Manager per call and not kept). Logs and
  LLM transcripts redact token-shaped strings. Halyard keeps no secret at rest itself.

## The analysis pipeline

```
sync (job) → Snapshot (JSON) → Review.Run
                                 ├─ TopologyAudit   explicit meta/labels only; nothing inferred
                                 ├─ Deficiencies    definition-of-ready by level
                                 ├─ Taxonomy        required meta keys by level
                                 └─ RelationshipReview  ranked parent candidates with evidence
```

Model-based sizing (`Intelligence/Estimator`) skips items that are not definable, asks for a strict JSON
array, parses tolerantly (JSON, markdown table, regex), clamps to policy (13 → 8, XL → Large) and saves a
batch of proposals. Refinement (`Intelligence/Refiner`) sends one item at a time with its gaps, parent and
siblings, asks for the complete narrative with the missing sections added, strips any meta the model
emits, re-runs the definition-of-ready rules on the result and measures how much of the original text
survived; drafts that lose text or leave gaps open are marked low confidence. Metadata repair and parent
proposals are deterministic. Hand edits are recorded as approved proposals. Nothing is written until a
human approves and applies.

## Upward and downward

The same snapshot feeds two directions. Downward (`Intelligence`, `Planning`): intake turns raw source
tickets into typed sandbox copies, repair / draft / size / tag skills bring them to the definition of
ready, and the planner fills sprints from team capacity and Shellback skills. Upward (`Analysis/Delivery`,
`Intelligence/Visionary`): feature progress, velocity, blockers and capacity roll into the leadership
report and the weekly email, and the visionary drafts the portfolio vision. Every model output is
validated against the backlog before it becomes a proposal, and every change still goes through the
same proposal → approve → gateway path.

A snapshot may hold several repositories (`Snapshot.Repositories`, `repository` per item). Sources are
read-only; proposals are refused for anything outside the sandbox.

## Shellback boundary

Halyard reads exported Shellback peer cards from `config/shellback/` and shares its skill ids with Shellback.
It never writes to Shellback, never stores anything about a person beyond what the card consented to, and
treats the card as the only source of a person's skills and confidence.

## Risk to watch

The engine is compiled in memory on every launch (a few seconds; the launcher prints and logs the time as
`ENGINE_COMPILED`). This depends on the workstation allowing `Add-Type` for a standard user in FullLanguage
mode, which is how the team's other PowerShell apps run. If PowerShell is in Constrained Language Mode or the
policy blocks in-memory compilation, `Import-HalyardCore` stops with a message that says so. Do not work around
the policy, and never cache the compiled engine as a DLL on disk: the policy blocks loading it.
