# Command line and API

Halyard follows the Fleet CLI parity proposal (`comms/proposals/cli-parity`): everything the browser UI can do is a named command,
`halyard <noun> <verb>`, and the same name is used for the loopback API route. This page is the contract; the list itself comes from
the engine so it cannot drift.

## Listing the commands

```powershell
.\scripts\Halyard.ps1 commands                          # table
.\scripts\Halyard.ps1 commands --json                   # the versioned document on stdout
.\scripts\Halyard.ps1 commands --json --out cmds.json   # UTF-8 without a BOM (not `>`, which writes UTF-16 in Windows PowerShell 5.1)
Get-HalyardCommand -Json                                # the same, from the module
```

The running engine serves the same document at `GET /api/commands` (session token required).

```json
{ "app": "halyard", "schemaVersion": 1,
  "commands": [ { "name": "halyard apply rollback-last", "method": "POST", "route": "/api/applies/rollback-last",
                  "summary": "...", "writes": true, "async": true } ] }
```

- `writes`: changes the sandbox (or production, for `promote run`), or stops the engine. These need an approved plan and, where the
  API asks for it, a typed confirmation or `force`/`dryRun` flag. A dry run exists for rollback; promote has a preview.
- `async`: the call returns a job; poll `halyard job show` until `state` is `succeeded`, `failed` or `cancelled`.
- `schemaVersion` changes only for an incompatible change to this document.

## Running a command

The API binds to `127.0.0.1` only and needs the per-session token the launcher puts in the browser URL (header
`X-Halyard-Session`). Request and response bodies are the JSON the UI uses; `fixtures/wire/` pins their shape.

```powershell
$h = @{ 'X-Halyard-Session' = $token }
Invoke-RestMethod http://127.0.0.1:8806/api/applies -Headers $h                                   # halyard apply list
Invoke-RestMethod http://127.0.0.1:8806/api/applies/rollback-last -Method Post -Headers $h `
  -Body '{"dryRun":true}' -ContentType application/json                                           # halyard apply rollback-last (preview)
```

## What is not there yet

- `Halyard.ps1` runs only `commands`. The other commands are reached through the API (above) or the module functions
  (`Start-Halyard`, `Invoke-HalyardSeed`, `Invoke-HalyardTeardown`, `Get-HalyardWorkItem`, `Test-HalyardConnection`). A CLI front end that reads a
  token file and calls the API by command name, `--yes`, and an MCP server generated from the registry are not built.
- The token is in the launch URL, not yet in a current-user-only file under `%LOCALAPPDATA%\Halyard` as the proposal's requirement 9 asks.
- Project files (proposal batches, apply runs) are JSON under `output/` but have no published schema yet.

## Parity test

`CommandParityTests` fails when an API route has no entry in `CommandRegistry`, when an entry has no route, when a name is not
`halyard <noun> <verb>` or is duplicated, or when a writing command is not a POST. A new feature adds its command in the same PR.
