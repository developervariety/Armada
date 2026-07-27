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
   (`RecordingHttpHandler`, `StubDataSource`, etc.).
   `NullLogger<T>.Instance` or `new LoggingModule()` (armada) for loggers.
3. **Structured logging only.** `{Foo} {Bar}` placeholders, NOT
   `$"..."` interpolation in `LogX(...)`. (Exception: armada's
   `LoggingModule` doesn't support placeholders — concat with `+`
   instead. See vessel-armada-codestyle.)
4. **Never log secrets.** Tokens (PASETO, Bearer, session),
   signatures, shared secrets, API keys, passwords, RSA private
   exponents. At `Information` level: no request/response payloads.
5. **Tenant isolation (multi-tenancy).** Every entity with a
   `FleetId` field gets a `HasQueryFilter(x => BypassTenantFilter ||
   x.FleetId == CurrentFleetId)` registered in the same commit as
   the `DbSet`.
6. **Never hand-edit generated or embedded files.**
   `output/approved-reference/`, `output/reference-export/`,
   `output/source-export/`, source dumps, decompiler dumps, decrypted
   data exports, embedded workflow JSON. **Fix the exporter or extractor
   upstream and re-run.** EF migrations may be edited only to fix
   idempotency issues; document the edit.
7. **Do not reintroduce removed features.** Permanently removed
   product areas must stay removed unless a new mission explicitly
   reintroduces them.
8. **Read the target repo's `CLAUDE.md`** before making changes.
    Repo-specific rules win on conflict.
9. **Do not reference plan / spec / roadmap docs in code,
    commits, or tracked specs.** Inline the *why*, not "see plan section 3"
    / "per the Phase 4 spec" / "tracked in TODO.md". They rot
    independently.
10. **Planning state lives in Armada first.** Objectives/Backlog,
    Planning Sessions, Checks, Releases, Deployments, Incidents,
    Runbooks, History, and Requests are the durable store. Legacy
    `project/docs/superpowers/` and `project/TODO.md` artifacts are
    migration/export material only.
11. **Update Armada records when work lands.** Link final
    mission/voyage/check/release/deployment evidence back to the
    objective. TODO close-out applies only to legacy rows not yet
    migrated into Armada.
12. **All code changes go through Armada.** Orchestrator does NOT
    Edit/Write tracked code in repo subdirs. Bug fixes, features,
    refactors flow through `armada_dispatch`. (You are the captain
    — this rule binds the orchestrator, not you.)
13. **Captains never edit `CLAUDE.md`.** Vessels carry
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

| Vessel / repo | Logger | Style |
|---|---|---|
| armada | `LoggingModule` (SyslogLogging) | `_Logging.Info(_Header + "op " + value)` (concat, NOT `$"..."`) |
| other .NET repos | `ILogger<T>` | `_log.LogInformation("op {Foo} {Bar}", foo, bar)` (placeholders) |

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

### Other vessels (xUnit)

- Production: `src/<area>/<Project>/`.
- Tests: `src/<area>/<Project>.Tests/<Class>Tests.cs`, flat at root
  of the test project.
- One test project per slice or service area unless the vessel playbook
  says otherwise.

### Reference decompiler projects

- Production: `src/<ReferenceDecompiler>/<Project>/`.
- Tests: `src/<ReferenceDecompiler>/<Project>.Tests/<Class>Tests.cs`,
  flat at root.

### Reference source-dump projects

- Production: `src/<ReferenceSourceDump>/<Project>/`.
- Tests: `src/<ReferenceSourceDump>/<Project>.Tests/<Class>Tests.cs`,
  flat at root.

## File-name convention

`{ClassUnderTest}Tests.cs` — one production class → one test class →
one file. If a single production class needs many tests, split by
*behaviour clusters* into multiple test classes
(`FooTests`, `FooEdgeCaseTests`) in the same folder.

## Method-name convention

`{Behavior}_{Condition}_{Expected}` — e.g.
`ProcessAsync_ValidInput_ReturnsExpected`,
`Validate_MissingFleetId_ThrowsArgumentException`.

Don't use `[Theory]` to "save space" if the cases test fundamentally
different behaviours; split them.

## What NOT to test (across all vessels)

- Simple DTO records, value objects, Vogen `[ValueObject]` types.
- EF migration up/down behaviour.
- Razor / Blazor component markup.
- Third-party library behaviour.
- Generated code (under `output/` in reference repos; under `obj/` /
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
  `Options.Create<T>()` unless the production code under test expects it.

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
