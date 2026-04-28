## Model Context
The following context was accumulated by AI agents during previous missions on this repository. Use this information to work more effectively.

## Test Framework

Armada uses a custom TestSuite framework, NOT xUnit. Key facts:
- Base class: `Armada.Test.Common.TestSuite` (test/Armada.Test.Common/TestSuite.cs)
- Test files live under: `test/Armada.Test.Unit/Suites/<area>/<Name>Tests.cs`
- Test project csproj: `test/Armada.Test.Unit/Test.Unit.csproj` (not `Armada.Test.Unit.csproj`)
- Run tests: `dotnet run --project test/Armada.Test.Unit/Test.Unit.csproj --framework net8.0`
- Register suites in `test/Armada.Test.Unit/Program.cs` via `runner.AddSuite(new MySuite())`
- Pattern: `public class FooTests : TestSuite`, `public override string Name => "Foo";`, `protected override async Task RunTestsAsync()`
- Each test: `await RunTest("MethodName_Condition_Expected", async () => { ... });`
- Assertion helpers: `AssertEqual(expected, actual, msg)`, `AssertNotNull(val, msg)`, `AssertNull(val, msg)`, `AssertContains(substring, container, msg)`, `AssertTrue(condition, msg)`, `AssertFalse(condition, msg)`
- No `Assert.IsType<T>()` -- use `AssertTrue(r is EvaluationResult.Fail, "msg")` then cast
- Required usings in test files: `Armada.Test.Common` for TestSuite base class
- Async test lambdas do NOT need `return Task.CompletedTask` at the end

## Pre-existing Test Failures (as of 2026-04-28, branch main)

5 tests fail on main and are NOT regressions to worry about:
1. Status Health Route Uses ProductVersion Constant
2. MergeBranchLocalAsync Cleans Conflict State After Failure
3. MergeBranchLocalAsync Succeeds When TargetCheckout Is A GitWorktree
4. MergeBranchLocalAsync Materializes MissingTargetBranch In Landing Checkout
5. ValidateCaptainModelAsync returns timeout error when runtime does not exit

## Code Style (C#)

- No `var` -- always explicit types
- No tuples -- use `out` parameters or named structs instead
- `using` statements inside namespace blocks (not file-scoped)
- XML documentation on all public members
- Public: `PascalCase`; Private fields: `_PascalCase`
- One type per file
- Sealed classes where possible; `record` for value/result types
- String interpolation `$"..."` is allowed in normal code but NOT inside structured log calls
- `ImplicitUsings` is enabled in `src/Directory.Build.props` -- no need for explicit `using System;`
- Multi-target: `net8.0;net10.0` set in `src/Directory.Build.props`

## AutoLand Predicate (M1 -- landed 2026-04-28)

New types added in M1 (foundation for M2-M5):
- `src/Armada.Core/Models/AutoLandPredicate.cs` -- POCO config class + `EvaluationResult` sealed record hierarchy (Pass / Fail(Reason))
- `src/Armada.Core/Services/Interfaces/IAutoLandEvaluator.cs` -- DI seam
- `src/Armada.Core/Services/AutoLandEvaluator.cs` -- pure evaluator, uses `Microsoft.Extensions.FileSystemGlobbing.Matcher` for glob matching
- Evaluation order: Enabled -> MaxFiles -> MaxAddedLines -> DenyPaths -> AllowPaths
- Diff parser: collects paths from `+++ b/` lines, counts lines starting with `+` (excluding `+++` headers)
- `Microsoft.Extensions.FileSystemGlobbing` is already a package reference in Armada.Core.csproj

## AutoLand Predicate (M2 -- landed 2026-04-28)

Changes from M2:
- `src/Armada.Core/Models/Vessel.cs` -- added `string? AutoLandPredicate` property (null default) and `GetAutoLandPredicate()` method returning `Armada.Core.Models.AutoLandPredicate?` (fully-qualified to avoid name collision with the property). Method returns null on whitespace/empty/parse-failure (no rethrow).
- Schema migration v32 added to all 4 backends: Sqlite/Mysql use `ALTER TABLE vessels ADD COLUMN auto_land_predicate TEXT;`, Postgresql uses `IF NOT EXISTS` variant, SqlServer uses `IF COL_LENGTH('vessels','auto_land_predicate') IS NULL ALTER TABLE vessels ADD auto_land_predicate NVARCHAR(MAX);`
- All 4 VesselMethods.cs files updated (INSERT/UPDATE/SELECT): auto_land_predicate added to column lists, parameter binding uses `vessel.AutoLandPredicate ?? (object)DBNull.Value`, reader mapping uses `reader["auto_land_predicate"] as string`
- IMPORTANT: For Sqlite and SqlServer, VesselFromReader is in the DatabaseDriver file (SqliteDatabaseDriver.cs / SqlServerDatabaseDriver.cs), NOT in VesselMethods.cs. For Postgresql and Mysql, VesselFromReader is inline in VesselMethods.cs.
- MySQL migration registration is split: static SQL arrays live in `Queries/TableQueries.cs` (e.g. `MigrationV32Statements`), registration via `SchemaMigration(32, "...", TableQueries.MigrationV32Statements)` is in `MysqlDatabaseDriver.cs:GetMigrations()`. Use `LONGTEXT` for MySQL (not `TEXT`).
- `[JsonIgnore]` attribute cannot be applied to methods -- only properties/fields. Methods are never serialized anyway.

## AutoLand Predicate (M4 -- landed 2026-04-28)

Changes from M4:
- `src/Armada.Server/MissionLandingHandler.cs` -- added `IAutoLandEvaluator _AutoLandEvaluator` field; added `IAutoLandEvaluator autoLandEvaluator` parameter between `IMergeQueueService` and `IMessageTemplateService` in constructor. After the `_Logging.Info(...auto-enqueued...)` line (~line 476), added the auto-land evaluation block: reads `vessel?.GetAutoLandPredicate()`, calls `_Git.DiffAsync(dock.WorktreePath, targetBranch)`, evaluates via `_AutoLandEvaluator.Evaluate()`, emits `merge_queue.auto_land_triggered` or `merge_queue.auto_land_skipped` events (direct `_Database.Events.CreateAsync` pattern, same as existing `merge_queue.enqueued`), fires `Task.Run(() => _MergeQueue.ProcessEntryByIdAsync(capturedEntryId))` on Pass with captured variable to avoid closure issues.
- `src/Armada.Server/ArmadaServer.cs` -- added `IAutoLandEvaluator _AutoLandEvaluator` field; instantiated `new AutoLandEvaluator()` before `MissionLandingHandler` creation; passed to `MissionLandingHandler` constructor. No .NET DI container used -- Armada instantiates services directly (not via `services.AddSingleton`).
- IMPORTANT: Armada does NOT use .NET's built-in DI container (`IServiceCollection`). All services are instantiated directly in `ArmadaServer.cs`. The plan's suggestion of `services.AddSingleton<IAutoLandEvaluator, AutoLandEvaluator>()` was incorrect for this codebase -- the correct approach is direct instantiation.
- `test/Armada.Test.Unit/Suites/Services/LandingPipelineTests.cs` -- constructor call for `MissionLandingHandler` needed updating to include new `IAutoLandEvaluator` parameter (passes `new AutoLandEvaluator()`). Always grep for ALL places that instantiate a class when changing its constructor.
- Event emission pattern in `MissionLandingHandler`: no `EmitEventAsync` helper exists. Events are emitted by creating `ArmadaEvent`, setting fields (EntityType, EntityId, MissionId, VesselId, VoyageId, CaptainId, Payload), and calling `await _Database.Events.CreateAsync(event)` inside a try/catch.
- `ArmadaEvent.Payload` is a `string?` field used for JSON-serialized additional data. Used `System.Text.Json.JsonSerializer.Serialize(new { ... })` with anonymous object for the payload -- required adding `using System.Text.Json;` to MissionLandingHandler.cs.

## AutoLand Predicate (M5 -- landed 2026-04-28)

Changes from M5:
- `src/Armada.Server/Routes/VesselRoutes.cs` -- POST (create) and PUT (update) vessel handlers now accept `autoLandPredicate` as a JSON object. Uses `ValidateAndExtractAutoLandPredicate` static helper (in same class) that strips the property via `System.Text.Json.Nodes.JsonNode`/`JsonObject` before `Vessel` deserialization (avoids object->string type conflict), validates via `JsonSerializer.Deserialize<AutoLandPredicate>`, and returns 400 with "invalid autoLandPredicate JSON: <message>" on failure. `System.Text.Json.Nodes` namespace is imported.
- `src/Armada.Server/Mcp/Tools/McpVesselTools.cs` -- `armada_add_vessel` and `armada_update_vessel` schemas include `autoLandPredicate` (type=object, additionalProperties=true). The `add` handler validates and sets. The `update` handler uses no-clobber semantics: property absent = leave unchanged, property null = clear, property object = validate and set. Validation extracts raw JSON via `JsonElement.GetRawText()` from the args `JsonElement`.
- IMPORTANT: `Vessel.AutoLandPredicate` is `string?` on the model. The REST and MCP handlers accept a JSON *object* from clients and serialize it to a string for storage. The client should never pass a raw string -- always a JSON object. The `ValidateAndExtractAutoLandPredicate` helper strips the property from the request body to avoid deserialization failure (can't deserialize JSON object into string?).
- `test/Armada.Test.Unit/Suites/Routes/VesselAutoLandPredicateRoutesTests.cs` -- new test file in a new `Routes` subdirectory. Tests: DB round-trip, MCP invalid-JSON error, MCP partial-update no-clobber. McpVesselTools can be tested by capturing handlers via a custom `RegisterToolDelegate` lambda.

## MCP Tool Testing Pattern

McpVesselTools (and other static MCP tool registrars) can be unit-tested directly:
1. Declare `Func<JsonElement?, Task<object>>? handler = null;`
2. Call `McpVesselTools.Register((name, _, _, h) => { if (name == "armada_add_vessel") handler = h; }, db.Driver);`
3. Create args with `JsonSerializer.SerializeToElement(new { ... })` and call `await handler!(args)`
4. Check result with `JsonSerializer.Serialize(result)` and `AssertContains`

## Project Structure Notes

- Multi-target: net8.0 and net10.0
- `dotnet run` on multi-target projects requires `--framework net8.0`
- Services interfaces live in `src/Armada.Core/Services/Interfaces/`
- Database backends: Sqlite, Postgresql, Mysql, SqlServer -- each has its own VesselMethods.cs and TableQueries.cs
- Sqlite and SqlServer: VesselFromReader lives in the DatabaseDriver class (SqliteDatabaseDriver/SqlServerDatabaseDriver). Postgresql and Mysql: VesselFromReader is inline in VesselMethods.cs.
- MySQL backend migration pattern: SQL arrays in TableQueries.cs, registration in MysqlDatabaseDriver.cs:GetMigrations(). Use LONGTEXT for large text columns (not TEXT).
- When adding a new column to vessels, 5 files per backend must be touched: TableQueries.cs (migration), VesselMethods.cs (INSERT + UPDATE SQL + parameter bindings), and DatabaseDriver.cs (VesselFromReader) for Sqlite/SqlServer, or VesselMethods.cs alone for Postgresql/Mysql. Plus MysqlDatabaseDriver.cs for the migration registration.
- MCP tool DTO files (VesselAddArgs.cs, VesselUpdateArgs.cs) live in `src/Armada.Server/Mcp/`. When a new field is accepted as a JSON object from MCP clients but stored as string (like autoLandPredicate), extract it directly from `args.Value` as a `JsonElement` rather than adding a property to the DTO -- this avoids touching out-of-scope files.
- Test suites are organized by area: `Suites/Database/`, `Suites/Models/`, `Suites/Services/`, `Suites/Routes/`
- No .NET DI container (`IServiceCollection`/`IServiceProvider`) used -- all services are directly instantiated in `ArmadaServer.cs`. When adding a new service dependency to a handler, instantiate it in ArmadaServer.cs and pass it directly. Always grep for ALL constructor call sites when adding new ctor parameters.


## Playbooks
These playbooks are part of the required instructions for this mission. Read and follow them.

### proj-corerules-summary.md
project CORE RULES 1-17 distilled — captains can't read project/CLAUDE.md from inside dock; this carries the rule set

# project CORE RULES — distilled reference

A captain can't read `project/CLAUDE.md` from inside a dock — only the
target vessel's `CLAUDE.md`. This playbook distils the project-wide
**CORE RULES** for missions that touch any vessel under
`project/`. Rule numbers match `project/CLAUDE.md`.

## Headline non-negotiables (read these first)

1. **Tests are required.** Every new public type, service, endpoint,
   or handler gets a test in the parallel `*.Tests` project. Bug
   fixes get a regression test.
2. **No mocking libraries.** Hand-rolled doubles only
   (`RecordingHttpHandler`, `ConstantVehicleDataSource`, etc.).
   `NullLogger<T>.Instance` (otrbuddy/j1939mitm/jpro/otr) or
   `new LoggingModule()` (armada) for loggers.
3. **Algorithm math lives in `j1939mitm` only.** Per-OEM primitives
   are pure static functions in `J1939Mitm.Core/<Manufacturer>/`.
   Signers and command handlers are thin adapters (pack seed → call
   static signer → unpack key). Two copies of the same algorithm = bug.
4. **Structured logging only.** `{Foo} {Bar}` placeholders, NOT
   `$"..."` interpolation in `LogX(...)`. (Exception: armada's
   `LoggingModule` doesn't support placeholders — concat with `+`
   instead. See vessel-armada-codestyle.)
5. **Never log secrets.** Tokens (PASETO, Bearer, session),
   signatures, shared secrets, API keys, passwords, RSA private
   exponents, full seed/key byte sequences. At `Information` level:
   no request/response payloads.
6. **Tenant isolation (multi-tenancy).** Every entity with a
   `FleetId` field gets a `HasQueryFilter(x => BypassTenantFilter ||
   x.FleetId == CurrentFleetId)` registered in the same commit as
   the `DbSet`. (otrbuddy primarily.)
7. **UDS 0x34 (RequestDownload / reflash) stays guarded.** No
   service byte `0x34` in any j1939mitm code path, agent command
   handler, or future `IDiagnosticSession` implementation.
   Reflashing a truck ECU can brick hardware.
8. **J1939 sentinels are "not available", not zero.** `0xFF`
   (8-bit), `0xFFFF` (16-bit), `0xFFFFFFFF` (32-bit). Check before
   trusting; preserve prior value, don't overwrite with zero.
9. **Never hand-edit generated or embedded files.**
   `output/jpro-export/`, `output/otr-export/`, `otrperformance/`
   JADX dump, embedded workflow JSON in otrbuddy. **Fix the
   extractor upstream and re-run.** EF migrations may be edited
   only to fix idempotency issues; document the edit.
10. **Do not reintroduce removed features.** otrbuddy's IFTA / Fuel
    Tax Compliance is permanently removed.
11. **Read the target repo's `CLAUDE.md`** before making changes.
    Repo-specific rules win on conflict.
12. **Do not reference plan / spec / roadmap docs in code,
    commits, or tracked specs.** Inline the *why*, not "see plan §3"
    / "per the Phase 4 spec" / "tracked in TODO.md". They rot
    independently.
13. **Planning artifacts under `project/docs/superpowers/` or
    `project/TODO.md`.** Never inside a repo subdir (the only
    exception is each repo's own `CLAUDE.md`).
14. **Update planning records when work lands.** TODO close-out
    happens in the same session as the landing.
15. **All code changes go through Armada.** Orchestrator does NOT
    Edit/Write tracked code in repo subdirs. Bug fixes, features,
    refactors flow through `armada_dispatch`. (You are the captain
    — this rule binds the orchestrator, not you.)
17. **Captains never edit `CLAUDE.md`.** Vessels carry
    `ProtectedPaths = ["**/CLAUDE.md"]`; commits to CLAUDE.md are
    rejected with a coaching message. Surface rule proposals as a
    `[CLAUDE.MD-PROPOSAL]` block in your final report:

    ```
    [CLAUDE.MD-PROPOSAL]
    File: <repo>/CLAUDE.md (or project/CLAUDE.md)
    Section: <heading>
    Change: add | update | remove
    Rationale: <one-line why>
    Proposed text:
    ---
    <the rule text>
    ---
    ```

## Quick test-design rules

- One test = one behaviour. No `_Setup`/`_Teardown` shared state.
- Test name: `{Behavior}_{Condition}_{Expected}`.
- File location varies per vessel — see the vessel-specific
  `vessel-<name>-tests` playbook attached to your mission.
- DO NOT test: simple DTOs, EF migrations themselves, Razor markup,
  third-party library behaviour.

## Quick logging cheat-sheet

| Vessel | Logger | Style |
|---|---|---|
| armada | `LoggingModule` (SyslogLogging) | `_Logging.Info(_Header + "op " + value)` (concat, NOT `$"..."`) |
| otrbuddy | `ILogger<T>` | `_log.LogInformation("op {Foo} {Bar}", foo, bar)` (placeholders) |
| j1939mitm | `ILogger<T>` (rare; mostly pure static) | `_log.LogDebug("op {Foo}", foo)` |
| jpro | `ILogger<T>` | `_log.LogInformation("op {Foo}", foo)` |
| otrperformance | `ILogger<T>` | `_log.LogInformation("op {Foo}", foo)` |

Never `$"..."` interpolation in `ILogger<T>.LogX(...)` calls.

## Final-report contract

Every mission ends its `AgentOutput` with:

```
[ARMADA:RESULT] COMPLETE
<one to three paragraphs summarising what shipped>
```

`[ARMADA:RESULT] BLOCKED` is also acceptable when the mission can't
land — explain *why* and what you tried.

### proj-test-project-layout.md
project test-project layout — tests next to production in src/, no top-level tests/, flat root

# project test-project layout

How test projects are structured across the `project/` directory.
Pair with the vessel-specific `vessel-<name>-tests` playbook attached
to your mission.

## TL;DR

- **Tests sit next to production** in `src/<area>/<Project>.Tests/`.
- **No top-level `tests/` directory** anywhere. There is no
  `project/tests/`, no `<repo>/tests/`. If you find yourself wanting
  to create one, stop and read this playbook again.
- Test files live **flat at the root** of the test project — NOT
  mirroring the production folder layout.

## Layout per vessel

### armada

- Production: `src/Armada.Core/`, `src/Armada.Server/`,
  `src/Armada.Runtimes/`, `src/Armada.Cli/`.
- Tests: `test/Armada.Test.Unit/Suites/<area>/<Name>Tests.cs`.
- (armada is the one exception to "flat root" — suites are grouped
  by area folder under `Suites/`. Still no top-level `tests/`.)
- Single test project: `Armada.Test.Unit`.
- Register new suite: `runner.AddSuite(new MySuiteTests());` in
  `test/Armada.Test.Unit/Program.cs`.

### otrbuddy

- Production: `src/<App>/Features/<Slice>/`.
- Tests: `src/<App>/Features/<Slice>/<Slice>.Tests/<Class>Tests.cs`,
  flat at root of the test project.
- One test project per slice.

### j1939mitm

- Production: `src/J1939Mitm/J1939Mitm.Core/<Manufacturer>/`.
- Tests: `src/J1939Mitm/J1939Mitm.Core.Tests/<Area>/<Name>Tests.cs`.
- The `<Area>/` subfolder is the only allowed nesting — typically
  `<Manufacturer>/` mirroring source.

### JproDeobfuscator

- Production: `src/JproDeobfuscator/<Project>/`.
- Tests: `src/JproDeobfuscator/<Project>.Tests/<Class>Tests.cs`,
  flat at root.

### OtrPerformanceDeobfuscator

- Production: `src/OtrPerformanceDeobfuscator/<Project>/`.
- Tests: `src/OtrPerformanceDeobfuscator/<Project>.Tests/<Class>Tests.cs`,
  flat at root.

## File-name convention

`{ClassUnderTest}Tests.cs` — one production class → one test class →
one file. If a single production class needs many tests, split by
*behaviour clusters* into multiple test classes
(`FooTests`, `FooEdgeCaseTests`) in the same folder.

## Method-name convention

`{Behavior}_{Condition}_{Expected}` — e.g.
`Sign_KnownSeed_ReturnsExpectedKey`,
`Validate_MissingFleetId_ThrowsArgumentException`.

Don't use `[Theory]` to "save space" if the cases test fundamentally
different behaviours; split them.

## What NOT to test (across all vessels)

- Simple DTO records, value objects, Vogen `[ValueObject]` types.
- EF migration up/down behaviour.
- Razor / Blazor component markup.
- Third-party library behaviour.
- Generated code (under `output/` in jpro/otr; under `obj/` /
  `bin/`; auto-scaffolded API client classes).

## Cross-vessel consistency

Even though armada uses TestSuite and the others use xUnit, these
project-wide conventions apply identically:

- One test = one behaviour, no shared state.
- File name `{ClassUnderTest}Tests.cs`.
- Method name `{Behavior}_{Condition}_{Expected}`.
- Hand-rolled doubles only (CORE RULE 2).
- Tests next to production, no top-level `tests/` directory.

## Common pitfalls

- Don't create `project/tests/`, `armada/tests/`, etc. — there is no
  such convention.
- Don't mirror the production tree under `<Project>.Tests/` —
  flat root is the rule.
- Don't share `WebApplicationFactory` / `TestDatabase` across tests
  — each test owns its own.
- Don't write your test files in a `Tests/` subfolder of the
  production project; the test project is a separate `.csproj`
  alongside production.

### vessel-armada-tests.md
armada vessel — TestSuite custom framework, no xUnit, hand-rolled doubles only

# armada vessel — test conventions

This playbook applies to missions on the `armada` vessel
(`developervariety/Armada` fork). The armada vessel uses a **custom
TestSuite framework**, NOT xUnit. Apply these conventions to every
test you write.

## Framework

- **NEVER** use xUnit attributes (`[Fact]`, `[Theory]`, `[InlineData]`,
  `[MemberData]`, `Assert.*`). The vessel does not reference the xUnit
  package; tests written with xUnit attributes will not compile.
- Tests inherit from `TestSuite` (custom base class in
  `Armada.Test.Common`). Override `Name` (string) and
  `RunTestsAsync()` (async).
- Inside `RunTestsAsync()`, register each case with
  `await RunTest("CaseName", async () => { ... });`.
- Use `AssertEqual(expected, actual, optionalMessage)`,
  `AssertTrue(condition, optionalMessage)`,
  `AssertFalse(condition, optionalMessage)`,
  `AssertNotNull(value, optionalMessage)`,
  `AssertContains(needle, haystack, optionalMessage)` from `TestSuite`.
- Register the new suite in `test/Armada.Test.Unit/Program.cs` via
  `runner.AddSuite(new MySuiteTests());` so it runs in the unit-test
  binary.

## Mocking — DON'T

- **No mocking libraries.** No `Moq`, `NSubstitute`, `FakeItEasy`. The
  vessel deliberately doesn't reference them; importing one will fail
  at restore time.
- **Hand-rolled doubles only.** For each interface you need to fake,
  write a small `private sealed class Recording<T> : I<T>` (or
  `Stub<T>`) inside the test file or a sibling helper. Capture inputs
  via lists and surface state via properties.
- For `LoggingModule` use a fresh `new LoggingModule()` and disable
  console output via `logging.Settings.EnableConsole = false`.
- For options/settings, just `new Foo { ... }` directly — no
  `Options.Create<T>()` here (that's an otrbuddy idiom).

## Database tests

- Use `TestDatabaseHelper.CreateDatabaseAsync()` to spin up an isolated
  SQLite-backed `TestDatabase`. Wrap usage in `using (TestDatabase db
  = await ...)` so the file is cleaned up.
- Don't share state between cases — every `RunTest` should create its
  own `TestDatabase`.

## File location

- Test files live under `test/Armada.Test.Unit/Suites/<area>/<Name>Tests.cs`.
- One suite per file. File name matches the `class <Name>Tests : TestSuite`.

## Common pitfalls

- Don't import `Xunit;` — it isn't referenced.
- Don't use `[Fact]` / `[Theory]` — the runner won't pick them up.
- Don't create a new test project under `test/` — register the suite
  in the existing `Armada.Test.Unit` project.
- Don't write integration tests that hit `localhost:7890` — the
  admiral isn't running during unit-test execution.

## Quick reference template

```csharp
namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Test.Common;

    public class FooTests : TestSuite
    {
        public override string Name => "Foo";

        protected override async Task RunTestsAsync()
        {
            await RunTest("DoesX_WhenY_ReturnsZ", async () =>
            {
                // arrange
                Foo foo = new Foo();

                // act
                int result = await foo.ComputeAsync(2);

                // assert
                AssertEqual(4, result);
            });
        }
    }
}
```

### vessel-armada-codestyle.md
armada vessel — code style: _PascalCase fields, LoggingModule (not ILogger), MCP tool patterns

# armada vessel — code style

Production code conventions for the `armada` vessel
(`developervariety/Armada` fork). Pair with `vessel-armada-tests.md`
when writing tests.

## Project structure

- Production code under `src/Armada.Core/`, `src/Armada.Server/`,
  `src/Armada.Runtimes/`, `src/Armada.Cli/`.
- Multi-targets `net8.0` and `net10.0` (some projects target only
  `net10.0`). Use `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`
  pattern when adding cross-version code; `net10.0` only for new
  Server-side services.
- Each interface lives in `src/Armada.Core/Services/Interfaces/I<Name>.cs`
  with the implementation in `src/Armada.Core/Services/<Name>.cs`.

## Naming + visibility

- `private readonly` instance fields: `_PascalCase` (e.g.
  `_Logging`, `_Database`, `_Settings`). Underscore + Pascal — NOT
  `_camelCase`.
- `private static readonly` fields: `_PascalCase` too.
- Private const: `_UPPER_SNAKE` is fine for protocol constants.
- Public properties: `PascalCase`. Public fields are rare; prefer
  properties.
- Method-local variables: `camelCase`.
- Public types: `sealed class` by default unless extension is
  intentional.

## Logging (CORE RULE 4)

Service classes use `LoggingModule` (from `SyslogLogging` package),
not `ILogger<T>`:

```csharp
private readonly LoggingModule _Logging;
private const string _Header = "[MyService] ";

_Logging.Info(_Header + "operation_name " + value);
_Logging.Warn(_Header + "validation failed: " + reason);
_Logging.Error(_Header + "unexpected error: " + ex.Message);
```

- Header constant per class: `_Header = "[ClassName] "`.
- Concatenate with `+`, NOT `$"..."` interpolation. (The
  CORE RULE 4 placeholder rule from CLAUDE.md is for
  `Microsoft.Extensions.Logging.ILogger<T>`; armada's `LoggingModule`
  doesn't support placeholders, so concatenation is correct here.)
- Operation names are stable per workflow.

## Async + cancellation

- Public async methods accept `CancellationToken token = default` as
  the last parameter.
- `await _Foo.BarAsync(...).ConfigureAwait(false)` on every await in
  library code (Server + Core).
- Use `Task` for pure side-effect; return concrete `Task<T>` for
  values; `ValueTask<T>` only when measured.

## MCP tools

- New MCP tools live under `src/Armada.Server/Mcp/Tools/Mcp<Area>Tools.cs`.
- Each tool registered via the `RegisterToolDelegate` lambda passed
  into `Register(...)`.
- Add registration to `McpToolRegistrar.RegisterAll(...)`.
- Tool names use snake_case with `armada_` prefix
  (e.g. `armada_drain_audit_queue`).

## Records + value types

- DTOs and POCOs: `public sealed class` if mutable for serialization,
  `public sealed record` for immutable payloads in `Models/`.
- Strong-typed IDs (mission, voyage, vessel) use the `xxx_xxxxxxxx_<8>`
  string format — there's no Vogen here; use `string` and validate
  with prefix conventions.

## DI + service registration

- Constructor inject via plain `public ClassName(IFoo foo, ...)` —
  no Microsoft.Extensions.DependencyInjection patterns; admiral wires
  things up explicitly in `ArmadaServer.cs`.

## Common pitfalls

- Don't use `ILogger<T>` — the project standard is `LoggingModule`.
- Don't add `Microsoft.Extensions.DependencyInjection` references —
  admiral hand-wires services.
- Don't introduce `record struct` inside `Armada.Core` unless you
  measured a perf reason; classes are the default.
- Don't reference plan/spec/roadmap docs in XML doc or comments
  (CORE RULE 12).

# Mission Instructions

You are an Armada worker agent. Implement only the current mission description, stay within scope, run the most relevant validation you can, commit your changes, and end with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary of what changed.

## Mission
- **Title:** feat(armada): Vessel.DefaultPlaybooks — auto-merge with dispatch selectedPlaybooks
- **ID:** msn_moj00bgj_Acew5jIg6pE
- **Voyage:** vyg_moj00bg6_iqwjDG9k13k

## Description
**Goal:** Add a `DefaultPlaybooks` field on the `Vessel` entity so `armada_dispatch` automatically attaches a vessel's standard playbooks (vessel-tests, vessel-codestyle, project-corerules-summary, project-test-project-layout) without the caller having to remember to specify them every time.

**Why:** Orchestrators dispatching against a known vessel routinely forget to set `selectedPlaybooks`, dropping the captain back to a no-context-priming briefing. Vessel-level defaults eliminate the recurrence at the source.

**Files to create / modify (inspect actual paths first; this list is a guide):**

- Modify `src/Armada.Core/Models/Vessel.cs` — add `List<SelectedPlaybook> DefaultPlaybooks { get; set; } = new();` (or matching type if `SelectedPlaybook` lives elsewhere; reuse the existing `{playbookId, deliveryMode}` shape used on `Voyage.SelectedPlaybooks` and `Mission.SelectedPlaybooks`).
- Modify whichever EF/Sqlite mapping describes `Vessel` (likely under `src/Armada.Database/...` or `src/Armada.Core/Database/...` — inspect to confirm) — persist `DefaultPlaybooks` as JSON column or similar to existing collection-on-entity patterns. Add migration if EF migrations are in use; if hand-rolled SQL, follow the project's pattern.
- Modify `src/Armada.Server/Mcp/Tools/McpVesselTools.cs` (or equivalent) — `armada_create_vessel` and `armada_update_vessel` accept an optional `defaultPlaybooks` array; `armada_get_vessel` returns it.
- Modify dispatch flow (`McpVoyageTools.cs` / `armada_dispatch` handler / wherever `selectedPlaybooks` is currently read off the request) — merge logic: start with `vessel.DefaultPlaybooks`; for each entry in caller-supplied `selectedPlaybooks`, if its `playbookId` is already in defaults, REPLACE the default's deliveryMode with the explicit one; otherwise APPEND. Final list is what gets persisted on the voyage/mission.
- Add test coverage in the appropriate test suite directories (this vessel uses TestSuite, NOT xUnit — reuse the pattern from existing dispatch tests; see attached vessel-armada-tests playbook for conventions).

**Test cases (TestSuite pattern):**

1. Vessel with no DefaultPlaybooks + caller supplies 2 playbooks → mission gets exactly those 2.
2. Vessel with 3 DefaultPlaybooks + caller supplies none → mission gets the 3 defaults.
3. Vessel with 3 defaults + caller supplies 2, one overlapping with a default → mission gets 4 entries total, overlapping playbook uses caller's deliveryMode.
4. `armada_get_vessel` returns the persisted `DefaultPlaybooks` list (round-trip).
5. `armada_update_vessel` mutates the list (add and remove an entry).

**Acceptance:**

- Build clean.
- All new tests pass.
- No regression in pre-existing vessel/dispatch tests.
- `armada_get_vessel` exposes `DefaultPlaybooks` (visible to MCP callers).
- A new vessel created via `armada_create_vessel` with no `defaultPlaybooks` arg has an empty list (not null).
- Existing vessels without DefaultPlaybooks behave unchanged after migration.

**Out of scope:**

- Populating DefaultPlaybooks for the existing vessels — that's a separate orchestrator-driven config step after this lands.
- Pipeline-level default playbooks — vessel-level only.
- Changing the deliveryMode enum or adding new modes.
- Do NOT edit `CLAUDE.md` (CORE RULE 17 — ProtectedPaths gate will reject).
- Do NOT edit `ModelContext` or other armada-vessel meta files.

**Conventions:** see attached playbooks (corerules-summary, test-project-layout, vessel-armada-tests, vessel-armada-codestyle). Highlights:

- This vessel uses TestSuite (NOT xUnit / NOT [Fact]). Follow the existing test-class shape exactly.
- No mocking libraries (CORE RULE 2). Hand-rolled doubles only.
- Structured logging (CORE RULE 4).

**End-of-mission:** one commit, message `feat(armada): Vessel.DefaultPlaybooks — auto-merge with dispatch selectedPlaybooks`. End final response with `[ARMADA:RESULT] COMPLETE` line followed by a one-sentence summary.

## Repository
- **Name:** armada
- **Branch:** armada/cursor-sonnet-1/msn_moj00bgj_Acew5jIg6pE
- **Default Branch:** main

## Rules
- Work only within this worktree directory
- Stay strictly within the mission scope and listed files
- Do not create, modify, or delete files outside the listed scope unless the mission explicitly requires it
- If you discover a necessary out-of-scope change, report it in your result instead of expanding scope on your own
- Commit all changes to the current branch
- Commit and push your changes -- the Admiral will also push if needed
- If you encounter a blocking issue, commit what you have and exit
- Exit with code 0 on success
- Do not use extended/Unicode characters (em dashes, smart quotes, etc.) -- use only ASCII characters in all output and commit messages
- Do not use ANSI color codes or terminal formatting in output -- keep all output plain text

## Project CORE RULES (non-negotiable)

These rules apply to every mission against the otrbuddy / j1939mitm / JproDeobfuscator / OtrPerformanceDeobfuscator repos. Read the repo's in-tree CLAUDE.md before any change for repo-specific overrides.

1. **Tests are required.** Every new public type, service, endpoint, or handler gets an xUnit test in the parallel `*.Tests` project. Bug fixes get a regression test. No exceptions.
2. **No mocking libraries.** Ever. Hand-rolled test doubles only (`RecordingHttpHandler`, `ConstantVehicleDataSource`, `FlakyPollHandler`, ...). `NullLogger<T>.Instance` for loggers. `Options.Create(...)` for options. No Moq / NSubstitute / FakeItEasy / JustMock.
3. **Structured logging only.** `{DeviceId}`, `{StatusCode}`, `{ElapsedMs}` placeholders. NEVER `$"..."` string interpolation inside `LogInformation` / `LogDebug` / `LogWarning` / `LogError`.
4. **Never log secrets.** Tokens (PASETO, Bearer, session), signatures, shared secrets, API keys, passwords, RSA private exponents, Cummins password-table rows, full seed/key byte sequences. At Information level: no request/response payloads.
5. **UDS 0x34 (RequestDownload / reflash) stays guarded.** No code path may emit the service byte `0x34`. Reflashing a truck ECU can brick hardware; this guard protects field equipment.
6. **J1939 sentinels are "not available", not zero.** `0xFF` (8-bit), `0xFFFF` (16-bit), `0xFFFFFFFF` (32-bit) are SAE-defined "signal not available" sentinels. Decode -> check sentinel -> only then trust the value. Properties stay null on sentinel; consumers skip rather than fail.
7. **Test files live at the ROOT of the test project.** Flat layout, NOT mirrored under feature folders. File name `{ClassUnderTest}Tests.cs`. Method name `{Behavior}_{Condition}_{Expected}`.
8. **Do not reference plan / spec / roadmap docs from code comments.** Plans rot or move; comments must be self-contained. Never write `see docs/superpowers/plans/...`, `per the Phase 4 spec`, `see TODO.md`, or `library Spec D5` style references. Inline the WHY in the comment itself.
9. **Update planning records when work lands.** If `project/TODO.md` is reachable from your worktree (it usually isn't -- see Dock isolation below), flip the relevant item to shipped with this commit's SHA. If unreachable, surface in your result and the orchestrator will update it.

## Dock isolation (Windows + Armada specific)

Your worktree is `C:\Users\Owner\.armada\docks\<vessel>\<missionId>\` -- a clone of ONLY the target vessel's repo. You CANNOT reach paths like `project/CLAUDE.md`, `project/TODO.md`, `project/PORTING-FROM-SOURCES.md`, or `project/docs/superpowers/...`. Do not attempt to read those paths. The mission description inlines anything you need from them.

## Context Conservation (CRITICAL)

You have a limited context window. Exceeding it will crash your process and fail the mission. Follow these rules to stay within limits:

1. **NEVER read entire large files.** If a file is over 200 lines, read only the specific section you need using line offsets. Use grep/search to find the right section first.

2. **Read before you write, but read surgically.** Read only the 10-30 lines around the code you need to change, not the whole file.

3. **Do not explore the codebase broadly.** Only read files explicitly mentioned in your mission description. If the mission says to edit README.md, read only the section you need to edit, not the entire README.

4. **Make your changes and finish.** Do not re-read files to verify your changes, do not read files for 'context' that isn't directly needed for your edit, and do not explore related files out of curiosity.

5. **If the mission scope feels too large** (more than 8 files, or files with 500+ lines to read), commit what you have, report progress, and exit with code 0. Partial progress is better than crashing.

## Avoiding Merge Conflicts (CRITICAL)

You are one of several captains working on this repository. Other captains may be working on other missions in parallel on separate branches. To prevent merge conflicts and landing failures, you MUST follow these rules:

1. **Only modify files explicitly mentioned in your mission description.** If the description says to edit `src/routes/users.ts`, do NOT also refactor `src/routes/orders.ts` even if you notice improvements. Another captain may be working on that file.

2. **Do not make "helpful" changes outside your scope.** Do not rename shared variables, reorganize imports in files you were not asked to touch, reformat code in unrelated files, update documentation files unless instructed, or modify configuration/project files (e.g., .csproj, package.json, tsconfig.json) unless your mission specifically requires it.

3. **Do not modify barrel/index export files** (e.g., index.ts, mod.rs) unless your mission explicitly requires it. These are high-conflict files that many missions may need to touch.

4. **Keep changes minimal and focused.** The fewer files you touch, the lower the risk of conflicts. If your mission can be completed by editing 2 files, do not edit 5.

5. **If you must create new files**, prefer names that are specific to your mission's feature rather than generic names that another captain might also choose.

6. **Do not modify or delete files created by another mission's branch.** You are working in an isolated worktree -- if you see files that seem unrelated to your mission, leave them alone.

Violating these rules will cause your branch to conflict with other captains' branches during landing, resulting in a LandingFailed status and wasted work.

## Runtime Signals
If you emit Armada signals, print each signal on its own standalone line with no bullets, quoting, or extra Markdown:
- `[ARMADA:PROGRESS] 50` -- report completion percentage (0-100)
- `[ARMADA:STATUS] Testing` -- transition mission to Testing status
- `[ARMADA:STATUS] Review` -- transition mission to Review status
- `[ARMADA:MESSAGE] your message here` -- send a progress message
- `[ARMADA:RESULT] COMPLETE` -- worker/test engineer mission finished successfully
- `[ARMADA:VERDICT] PASS` -- judge approves the mission
- `[ARMADA:VERDICT] FAIL` -- judge rejects the mission
- `[ARMADA:VERDICT] NEEDS_REVISION` -- judge requests follow-up changes
Architect missions must not emit `[ARMADA:RESULT]` or `[ARMADA:VERDICT]`; they must output only real `[ARMADA:MISSION]` blocks.

## Model Context Updates

Model context accumulation is enabled for this vessel. Before you finish your mission, review the existing model context above (if any) and consider whether you have discovered key information that would help future agents work on this repository more effectively. Examples include: architectural insights, code style conventions, naming conventions, logging patterns, error handling patterns, testing patterns, build quirks, common pitfalls, important dependencies, interdependencies between modules, concurrency patterns, and performance considerations.

If you have useful additions, call `armada_update_vessel_context` with the `modelContext` parameter set to the COMPLETE updated model context (not just your additions -- include the existing content with your additions merged in). Be thorough -- this context is a goldmine for future agents. Focus on information that is not obvious from reading the code, and organize it clearly with sections or headings.

If you have nothing to add, skip this step.
