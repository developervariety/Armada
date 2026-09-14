# Testing

## Run All Tests

All commands run from the repository root. Each test project is a standalone console application.

```bash
dotnet run --project test/Armada.Test.Automated --framework net10.0
dotnet run --project test/Armada.Test.Unit --framework net10.0
dotnet run --project test/Armada.Test.Runtimes --framework net10.0

# Shared Touchstone suites over src/Test.Shared (lists every skipped case with its reason)
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net10.0

# Database driver tests (explicit SQLite provider)
dotnet run --project test/Armada.Test.Database --framework net10.0 -- --type sqlite --filename test.db

# PostgreSQL
dotnet run --project test/Armada.Test.Database --framework net10.0 -- --type postgresql --hostname localhost --port 5432 --username postgres --password secret --database armada_test

# SQL Server
dotnet run --project test/Armada.Test.Database --framework net10.0 -- --type sqlserver --hostname localhost --port 1433 --username sa --password secret --database armada_test

# MySQL
dotnet run --project test/Armada.Test.Database --framework net10.0 -- --type mysql --hostname localhost --port 3306 --username root --password secret --database armada_test
```

## Test Projects

| Project | Tests | What It Covers |
|---------|-------|----------------|
| `Armada.Test.Automated` | ~891 | REST API, MCP tools, WebSocket, authentication, end-to-end workflows |
| `Armada.Test.Unit` | ~3344 | Database operations, model serialization, service logic |
| `Armada.Test.Runtimes` | ~179 | Agent runtime adapters (Claude Code, Codex, Gemini, Cursor, Mux, OpenCode) |
| `src/Test.Automated` | ~2480 | Shared Touchstone suites over `src/Test.Shared`: database, models, runtimes, services, end-to-end |
| `Armada.Test.Database` | ~100+ | Database driver CRUD operations across all 4 backends (SQLite, PostgreSQL, SQL Server, MySQL) |
| `Armada.Test.Common` | — | Shared test infrastructure (TestRunner, TestSuite, TestResult) |

## How It Works

The fork unit, automated, runtime and database runners are console applications. The shared test suites run through the `src/Test.Automated` console runner and also have NUnit and xUnit adapters under `src/`; see [Shared Suite Runner](#shared-suite-runner) and [test discovery](upstream-review/test-discovery.md). Use the command for the selected runner; `dotnet test` does not execute the console runners.

- `TestSuite` — abstract base class in `Armada.Test.Common`. Each suite groups related tests, provides assertion helpers, and cleans up its own test data.
- `TestRunner` — orchestrates suites, prints colored results, generates summary with failed test details.
- `RunTest(name, action)` — wraps each test with a Stopwatch. Prints PASS/FAIL with elapsed milliseconds. Catches exceptions and records failure details.

## Output

```
================================================================================
ARMADA UNIT TEST SUITE
================================================================================
...
================================================================================
TEST SUMMARY
================================================================================
Total: 3524  Passed: 3524  Failed: 0  Runtime: 283243ms

================================================================================
RESULT: PASS
================================================================================
```

The totals above are an ILLUSTRATION of the output shape, not a gate. Only
`Failed: 0` is a contract. The pass total moves with every suite that lands and
differs between hosts, so a run that reports a different total than this page
is not a regression - read the failure count.

Because there is no reflection-based discovery in this runner, the total IS the
check that a new test is wired in: after adding tests, confirm the total moved
by exactly the number you added. A total that did not move means the tests are
not registered and will never run, never fail, and never appear.

## Shared Suite Runner

`src/Test.Automated` runs every suite in `src/Test.Shared` through the Touchstone console runner. Suites are discovered by reflection, so a written suite cannot be left unregistered.

- **Build.** The project is in `src/Armada.sln`, so the solution build compiles it on every change. It is multi-targeted; pass `--framework`.
- **Gate.** `scripts/common/run-tests.sh` runs it as the `shared` suite beside `unit`, `automated` and `runtimes`. A failure in any of the four fails the combined result.
- **Options.** `--suites <prefixes>` narrows the run to suite ids with those comma-separated prefixes. `--results <path>` writes JSON results. `--db-type`, `--db-host`, `--db-port`, `--db-user`, `--db-pass` and `--db-name` target a server provider.
- **Output.** After the summary the runner prints `Skipped Tests: N`, each skipped case id with its reason, and the counts by disposition. A run with no source checkout above it says that legacy owners were not verified.
- **Exit codes.** 0 when nothing failed. 1 when a case failed. 2 when discovery fails (a stale disposition record), when the suite filter matches no suite, or when the selection would execute nothing.

### Ownership

The legacy executables remain the owners of the fork cases they execute. The shared runner owns every shared case it executes. When a shared case cannot execute correctly, it is recorded once in `src/Test.Shared/Infrastructure/SharedCaseDispositions.cs` and reported by every runner as a named, counted skip:

| Disposition | Meaning | Owner named in the record |
|-------------|---------|---------------------------|
| Duplicate of an executed legacy case | The shared copy predates a contract change that the legacy case already asserts. | The legacy file and registered case name |
| Awaiting owner decision | The shared case asserts behaviour the fork does not implement. | None; the owner decides whether to implement the behaviour or retire the case |

Discovery fails when a record names no discovered case or names a legacy case that its file no longer registers, so a rename or removal cannot silently hide a case. The per-case list and the reasons are in [the case mapping](upstream-review/test-discovery-cases.md#shared-runner-failure-inventory).

| Shared suite prefix | Executed by | Legacy runner with overlapping cases |
|---------------------|-------------|--------------------------------------|
| `Database.*`, `Models.*` | Shared runner | `test/Armada.Test.Unit`, `test/Armada.Test.Database` |
| `Services.*` | Shared runner | `test/Armada.Test.Unit` |
| `Runtimes.*` | Shared runner | `test/Armada.Test.Runtimes` |
| `E2E.*` | Shared runner | `test/Armada.Test.Automated` |

A shared end-to-end suite that creates missions or voyages cancels its active work after each case, because fleet capacity admission counts every active voyage and standalone mission.

### Reproducing the inventory

```bash
dotnet build src/Test.Automated -f net10.0
dotnet run --no-build --framework net10.0 --project src/Test.Automated -- --results shared-results.json
python3 -c "import json; [print(r['testId'], '|', (r['message'] or '').splitlines()[0]) for r in json.load(open('shared-results.json')) if not r['success'] and not r['skipped']]"
```

## Command-Line Options

```bash
# Run with default settings (temporary SQLite database, cleaned up after execution)
dotnet run --project test/Armada.Test.Automated --framework net10.0

# Keep test database after run (for debugging)
dotnet run --project test/Armada.Test.Automated --framework net10.0 -- --no-cleanup

# Test against PostgreSQL instead of default temp SQLite
dotnet run --project test/Armada.Test.Automated --framework net10.0 -- --type postgresql -h localhost -u postgres -w secret -d armada_test

# Test against SQL Server
dotnet run --project test/Armada.Test.Automated --framework net10.0 -- --type sqlserver -h localhost --port 1433 -u sa -w secret -d armada_test

# Test against MySQL
dotnet run --project test/Armada.Test.Automated --framework net10.0 -- --type mysql -h localhost --port 3306 -u root -w secret -d armada_test
```

### Database Arguments

| Argument | Short | Description | Default |
|----------|-------|-------------|---------|
| `--type` | | Database backend: `sqlite`, `postgresql`, `sqlserver`, `mysql` | Required by Test.Database; Test.Automated defaults to SQLite |
| `--filename` | | SQLite database file path | Temp file (auto-cleaned) |
| `--hostname` | `-h` | Database server hostname | `localhost` |
| `--port` | | Database server port | Backend default |
| `--username` | `-u` | Database username | — |
| `--password` | `-w` | Database password | — |
| `--database` | `-d` | Database name | — |
| `--schema` | | Database schema | Backend default |

If no `--type` is provided, Test.Automated uses a temporary SQLite database. Test.Database requires `--type`: omitting it prints validation errors and usage, then exits with code 2 without running tests. Supply an isolated SQLite filename or an explicitly provisioned server database. See the [database runner guide](../test/Armada.Test.Database/README.md) for migration scenarios and cleanup rules.

## Multi-Database Testing

Armada supports four database backends: SQLite, PostgreSQL, SQL Server, and MySQL. The testing strategy covers databases at two layers:

- **Test.Database** exhaustively tests the database driver layer directly, running CRUD operations for all 9 entity types (fleets, vessels, captains, missions, voyages, docks, signals, artifacts, merge queue entries) against each backend.
- **Test.Automated** tests the full stack (REST API, MCP tools, WebSocket) and can now target any database backend via the `--type` argument.

### CI Recommendations

- Run **Test.Database** against all 4 backends to ensure driver correctness across SQLite, PostgreSQL, SQL Server, and MySQL.
- Run **Test.Automated** at minimum against SQLite (fast, no external dependencies) plus one server-based backend (e.g., PostgreSQL) to verify full-stack behavior with a real database server.

### Connection Pooling

Test runs create and dispose many database connections rapidly. When testing against server-based backends, be aware that connection pooling settings affect test behavior. The default pool sizes are generally sufficient for test runs, but if you see connection timeouts or failures under heavy parallel test execution, consider increasing the pool size or running test suites sequentially.

## Test Data Isolation

Each test suite creates its own data, asserts only on that data, and cleans up after itself. Suites track created entity IDs and delete them at the end. This pattern is followed by all test projects, including Test.Database. This means:
- Suites never assume the database is empty
- Suites never assert exact total counts across entity types
- Suites can run in any order without affecting each other
- Use `--no-cleanup` to preserve test data after a run for debugging

## Agent Runtimes and Credentials in Test Hosts

The in-process Admiral in `test/Armada.Test.Automated` and in the shared `E2EServerFixture` never starts the agent CLIs installed on the machine running the suite.

- **Test runtime.** Both hosts construct `ArmadaServer` with `TestAgentRuntimeFactory` (`src/Test.Shared/Infrastructure`). Every CLI runtime (Claude Code, Codex, Gemini, Cursor, OpenCode, Mux) is a `NonLaunchingAgentRuntime`. A start registers a synthetic process identifier that the health checks treat as alive, writes the mission log header, and does no work. It runs until a stop arrives through any runtime instance, then reports exit code 137 on the instance that started it. A captain therefore stays Working exactly as long as its mission holds it, and no agent exit hands Pending work to other idle captains. API-endpoint runtimes run in-process and are created as usual.
- **Explicit exits.** A test that needs a failed or completed run calls `NonLaunchingAgentRuntime.Exit(processId, exitCode)`. No test may rely on a real CLI failing to start.
- **Launch check.** Each start is recorded in `TestProcessLaunchLog`. After the run, `test/Armada.Test.Automated` and `src/Test.Automated` fail with `RESULT: FAIL (agent process launches)` when any operating-system agent process was started for a runtime that was not opted in. The message names each executable, its process id and its exit code. The check also runs when `--suite` or `--suites` narrows the run.
- **Opting in to a real runtime.** Set `ARMADA_TEST_REAL_RUNTIMES` to a comma-separated list of runtime names, for example `ARMADA_TEST_REAL_RUNTIMES=ClaudeCode`. Only those runtimes start their CLI. A name that is not a CLI runtime stops the runner at startup. A suite that needs a real runtime calls `TestAgentRuntimeFactory.RealRuntimeSkipReason(runtime, optedIn)` and skips with the returned reason, which names either the missing opt-in or the executable not found on `PATH`.
- **Stopping a start.** Cancelling a voyage cancels only its Pending and Assigned missions, so a started mission keeps its test runtime running, as a real agent would. A test that must free the captain cancels the mission and calls `POST /api/v1/captains/{id}/stop`.
- **Runtime tool configuration.** `CaptainToolService` reads a busy captain's user-level runtime configuration (`.claude.json`, `.gemini/settings.json`, `.mux`) and may start every MCP server listed there to probe it. By default that is the current user's profile. Tests pass their own `userProfileDirectory`, so they never read the developer's configuration or start its MCP bridges. `DiscoveryCases_NeverReadOrStartTheProcessUserProfileServers` points the process profile at a sentinel configuration and fails if a discovery case lists or starts its server.
- **Provider environment.** The unit, automated, runtimes and shared runners, and the E2E fixture, remove model-provider credentials and agent-session variables from their own process before any test runs, and print how many they removed (names only). The removed names are the prefixes `ANTHROPIC_`, `OPENAI_`, `AZURE_OPENAI_`, `CLAUDE_CODE_`, `CODEX_`, `CURSOR_`, `GEMINI_`, `OPENCODE_`, `OPENROUTER_` and `DEEPSEEK_`, and `CLAUDECODE`, `GOOGLE_API_KEY` and `MISTRAL_API_KEY`. A test that asserts on inherited provider variables sets them itself. Set `ARMADA_TEST_KEEP_PROVIDER_ENVIRONMENT=1` only for a deliberate real-runtime run.

## Adding Tests

1. Find or create the appropriate suite in `Suites/`
2. Add a call to `RunTest("Test Name", async () => { ... })` inside the suite's `RunTestsAsync()` method
3. Use assertion helpers: `Assert()`, `AssertEqual()`, `AssertNotNull()`, `AssertTrue()`, `AssertStatusCode()`
4. Track any created entity IDs and delete them in the suite's cleanup section
5. Register new suites in `Program.cs` via `runner.AddSuite(new YourTests(...))`
