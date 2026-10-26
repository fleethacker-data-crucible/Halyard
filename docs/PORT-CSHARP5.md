# Port plan: Halyard.Core as C# 5 source compiled by Add-Type

Status: in progress on `port/csharp5-source` (2026-10-26).

## Why

The workstation's application control blocks loading custom DLL files (confirmed by the user on
2026-10-26). Halyard 1.0 ships a prebuilt `Halyard.Core.dll` plus the third-party `Newtonsoft.Json.dll`
and loads both with `Add-Type -LiteralPath`, so it can never start there. The team baseline
(fleethacker-comms-data-crucible `docs/TECH-STACK-BASELINE.md`, the way phoenix-lai does it) is:
C# ships as source and Windows PowerShell 5.1 compiles it in memory at launch with
`Add-Type -Path <*.cs> -CompilerParameters`. That is the inbox .NET Framework 4.8 CodeDom compiler, which
accepts **C# 5 only**, and the code may reference framework assemblies only.

## Target

| | 1.0 | 1.1 |
| --- | --- | --- |
| Engine shipped as | `lib\net48\Halyard.Core.dll`, `lib\netstandard2.0\Halyard.Core.dll`, `Newtonsoft.Json.dll` | `src\Halyard.Core\**\*.cs` (source) |
| Loaded by | `Add-Type -LiteralPath <dll>` | `Add-Type -Path <*.cs> -CompilerParameters` (in memory, nothing written next to the bundle and nothing loaded by path) |
| Language | C# 12, nullable enabled | C# 5 |
| JSON | Newtonsoft.Json 13 | `Halyard.Core.Serialization` (hand-written reader/writer + DOM, framework only) |
| References | Newtonsoft.Json, System.Net.Http | framework only: System, System.Core, System.Net.Http |

The browser UI, HTTP endpoints, JSON on the wire, `halyard.env` (CASCADE_* shims included), Lazarette
integration and the write gateway rules do not change.

## Inventory

`src/Halyard.Core`: 47 files, 10,958 lines. Tests: `src/Halyard.Core.Tests`, 7 files, 2,118 lines,
153 xUnit tests (all passing before the port).

Compiling the sources with Roslyn `-langversion:5` against the .NET Framework 4.8 reference assemblies
(no packages) reports, in the first (declaration) pass:

| Error | Count | What |
| --- | --- | --- |
| CS8026 nullable reference types | 349 | `string?`, `T?` on reference types, `!` null-forgiving |
| CS8026 auto property initializer | 270 | `public List<X> Items { get; set; } = new ...` |
| CS8026 readonly auto property | 3 | `{ get; }` with no setter |
| CS0246 type not found | 106 | Newtonsoft types |
| CS8627 | 4 | nullable type parameter (`T?` unconstrained) in `Json.Read<T>` |

Body-level C# 6+ syntax only shows once declarations compile; a grep over the sources finds roughly:
string interpolation 21, `?.`/`?[` 18, expression-bodied members 12, `out var` 7, `is T x` patterns 35,
tuples / `ValueTuple` 47, ranges/indices 11, positional `record`-like classes 16 (Vocabulary.cs),
`using var` 1, index initializers (`new X { ["k"] = v }`) many, `nameof` 0, switch expressions 0.
Most of the code already uses C# 5 style bodies (`get { return ...; }`), so the bulk of the work is
mechanical: nullable annotations, property initializers (moved into constructors), and the JSON swap.

Files with the most declaration errors: Analysis/Delivery.cs 36, Planning/TeamConfig.cs 34,
Intelligence/Intake.cs 34, Intelligence/Proposals.cs 30, Llm/LlmClient.cs 29, Planning/SprintPlanner.cs 28,
Host/HalyardHost.cs 28, Synthetic/SyntheticCatalog.cs 24, Intelligence/Visionary.cs 23, Host/Diagnostics.cs 23.

### Newtonsoft.Json usages (product code, 22 files)

- DOM: `JObject` 125, `JArray` 92, `JToken` 18, `JProperty` 1, `JTokenType` 14, `ToString(Formatting.*)` 13.
  Used for provider REST payloads (GitHub, GitLab), LLM requests/replies, taxonomy, team config,
  snapshots, synthetic catalog, diagnostics log lines, LLM-output parsing.
- Reflection serializer: `Host/Router.cs` `Json.Serialize` (every API response): camelCase property names
  (`CamelCaseNamingStrategy`, dictionary keys untouched), enums as camelCase strings, nulls included,
  ISO dates, no indentation. `JObject.FromObject` with the same settings in ApiRoutes.
- `JToken.ToObject<T>` in `Json.Read<T>` (request bodies).
- `JsonConvert.SerializeObject/DeserializeObject<T>` with default settings (PascalCase names, enums as
  numbers, indented) for persisted files: seed manifests, intake manifests, vision drafts, proposal batches.

## Approach

1. **Pin the wire formats on the current code first** (Newtonsoft still in place): golden tests for
   `Json.Serialize` over every response DTO and anonymous shape the routes use, the camelCase rules,
   enums, nulls, numbers (integral doubles keep `.0`, round-trip `R` format), `DateTimeOffset`/`DateTime`
   ISO text, string escaping (control chars, ` `), dictionaries, the persisted-file formats
   (indented PascalCase, enums as numbers), the provider and LLM request bodies, and parsing behaviour the
   code relies on (`(string)` of numbers, `(int?)` of strings, missing keys → null).
2. **Write `Halyard.Core.Serialization`**: a DOM with the subset of the Newtonsoft API the code uses
   (`JToken`/`JObject`/`JArray`/`JValue`/`JProperty`, explicit/implicit conversions, `Parse`,
   `ToString(Formatting)`), a reader, a writer that reproduces Newtonsoft's text output, and a reflection
   serializer/deserializer with the two configurations above. Swap the usings; the golden tests must pass
   unchanged.
3. **C# 5 rewrite**, directory by directory, in reviewable commits. Each commit keeps the xUnit suite
   green and lowers the `-langversion:5` error count to zero.
4. **Launcher**: `Import-HalyardCore` compiles `src\Halyard.Core\**\*.cs` with
   `Add-Type -Path ... -CompilerParameters` (GenerateInMemory, framework references only) on Windows
   PowerShell 5.1, `Add-Type -Path ... -ReferencedAssemblies` on PowerShell 7. It measures and logs the
   compile time. No DLL is read from or written to the bundle.
5. **Tests**: the xUnit project compiles the same `.cs` files (`<Compile Include>`) on .NET 8 (and net48
   on Windows); no project reference to a built DLL. Pester and the e2e loop run against the
   Add-Type-compiled engine. CI gains a step that compiles the engine exactly as the launcher does and
   fails on any C# 6+ syntax, plus the `-langversion:5` Roslyn check (`build/csharp5`).
6. **Bundle and release**: `Build-Halyard.ps1` ships the `.cs` files, no `lib\*.dll`; the release
   workflow verifies the zip has no `.dll`. Docs and TechStack updated; ROADMAP item 10 (signed DLL) removed.

## Risks

| Risk | Mitigation |
| --- | --- |
| JSON text differs from Newtonsoft (number format, escapes, date text, property order, nulls) and the UI or a saved file breaks | Golden tests written against Newtonsoft before the swap; the same tests must pass after it. |
| Newtonsoft's `JToken.Parse` turns ISO-date strings into dates (`DateParseHandling.DateTime`); code that does `(string)token` on such a value gets Newtonsoft's re-formatted date text | Audit every `(string)` read of a date field; the new reader keeps strings as strings and converts to `DateTimeOffset` on explicit cast. Any visible difference is reported in the PR. |
| Old persisted files (seed/intake manifests, proposal batches, vision drafts written by 1.0) must still load | Deserializer matches property names case-insensitively and accepts enums as numbers or names; a test loads files written by 1.0. |
| Error text for a malformed request body came from Newtonsoft (`Body is not valid JSON: <message>`) | The prefix and status/code stay; the parser message wording changes (only the human-readable tail). |
| Roslyn `-langversion:5` is not the legacy CodeDom compiler; a construct could pass one and fail the other | CI compiles with the real Windows PowerShell 5.1 Add-Type on the self-hosted Windows runner. |
| Compile time on every launch (≈11k lines) | Measured by the launcher and in CI; reported. Compiled assemblies are never cached on disk (the policy blocks loading them). |
| PowerShell 7 (`pwsh`) has no `-CompilerParameters` | `Import-HalyardCore` uses `-ReferencedAssemblies` there; C# 5 compiles under newer compilers too. |
| Engine "holder" detection in `Halyard-Process.ps1` looks for processes that hold `Halyard.Core.dll` | No file is held any more; the runtime-file based detection stays, the DLL-holder probe is dropped for the source build. |
