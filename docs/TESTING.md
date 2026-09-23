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

## Combined Gate Script

`scripts/macos/run-tests.sh` and `scripts/linux/run-tests.sh` build the four console runners in sequence, then run them at the same time, each writing its own log. Sharded and concurrent runs are the default.

```bash
scripts/macos/run-tests.sh                              # unit shards, automated, runtimes, shared
scripts/macos/run-tests.sh unit                         # one runner (unit|automated|runtimes|shared)
scripts/macos/run-tests.sh --shards 4                   # unit shard count; flags go before the runner name
ARMADA_TEST_UNIT_SHARDS=1 scripts/macos/run-tests.sh    # unit as one process
scripts/macos/run-tests.sh unit --suite "Git Service"   # one runner with its own arguments, never sharded
```

The script prints one summary line per process, the summed unit totals, and one combined `RESULT: PASS` or `RESULT: FAIL`. It fails when any process exits non-zero or prints no `Total:` line. For the unit shards it also fails when a shard's last `RESULT:` line is not `PASS`, when fewer shards summarised than were started, or when the per-shard suite counts do not add up to the registered suite count. A shard that crashed or executed nothing therefore fails the gate. The logs stay in the printed directory on failure, and on success when `ARMADA_TEST_KEEP_LOGS` is set. `ARMADA_TEST_LOG_DIR` names the log directory instead of a new temp directory; a directory named that way is never deleted. The script unsets `ANTHROPIC_*` for every child. When `ARMADA_TEST_RESULTS_DIRECTORY` is set, the unit runner runs as one process, because a results manifest is keyed by executable.

Use the script locally for quick runs of one runner or one suite. The full gate runs on a Linux host; see [Gate Host](#gate-host).

The `Mission Review Diff` unit suite owns all six review-diff cases. It checks
unchanged small diffs, retained code and file headers, generated-data elision,
small data files, and generic elision for large code files.

## Gate Host

The gate is the four runners together: `unit` (sharded), `automated`, `runtimes` and `shared`. A commit passes only when all four pass in one combined run. Run the gate on a Linux host, not on a macOS workstation.

**Why.** Many unit suites start git thousands of times. Process start-up is far slower on macOS than on Linux, and that overhead, not git's own work, dominates the git-heavy suites. The other suites take about the same time on both.

**Measurements.** Same commit, serial runs:

| Runner or suite | Linux server, 16 cores, idle | macOS workstation, under load |
|-----------------|------------------------------|-------------------------------|
| Build from a clean clone | 23 s | — |
| `unit` | 177 s | 541 s |
| `automated` | 50 s | 160 s |
| `runtimes` | 24 s | 25 s |
| Merge Queue Branch Cleanup | 5 s | 99 s |
| Branch Cleanup Sweep | 2 s | 55 s |
| Vessel Branch Write Service | under 2 s | 28 s |
| Git Service | under 2 s | 26 s |
| Self Deploy Cutover (no git) | 25 s | 25 s |
| Harbor Transport (no git) | 10 s | 10 s |

A git trace of Branch Cleanup Sweep on the macOS workstation recorded 21 tests, 45 s of wall time and 1884 git processes, but only 10 s inside git (4.6 ms per process on average). About 35 s was process start-up outside git. 259 of the processes were `git maintenance` runs that git starts by itself after commits; the test hosts turn those off (see [Git in test processes](#git-in-test-processes)).

Sharded combined run on the Linux server (16 cores), measured with `server-gate.sh`: build 24 s, then all four suites in **59 s** wall clock — unit 4878 tests in six shards (slowest shard 40 s), automated 1050 in 49 s, runtimes 184 in 24 s, and shared 2504, all at once. The whole run from a workstation, including push and build, took 116 s. The same four suites run serially on the macOS workstation took about 12 minutes.

**Use Microsoft's .NET SDK on the gate host, not a distribution package.** A distribution runtime built against the system libunwind (for example Ubuntu's `dotnet-runtime-10.0`, which links `libunwind8`) can fail to unwind the stack when managed code throws, and the runtime then aborts the process with `Internal CLR error. (0x80131506)` (`SoftwareExceptionFrame::Init` → `PAL_VirtualUnwind` fails → `EEPolicy::HandleFatalError`). Under `dotnet build` this shows up as a build that stops before compiling. Microsoft's builds bundle their own libunwind and do not abort. Install the SDK with Microsoft's install script into `~/.dotnet` (`dotnet-install.sh --version <sdk> --install-dir ~/.dotnet --no-path`); `server-gate.sh` prefers `~/.dotnet/dotnet` over `PATH`, prints the SDK it uses, and warns when the selected runtime links the system libunwind.

As a safety net, `server-gate.sh` retries that abort at most twice, and only when the build log holds nothing else; it prints each retry and keeps the runtime crash report (`build-abort-<n>.<pid>.crashreport.json`) and the aborted log in the gate log directory. Any other build failure fails the gate at once.

**Running the gate.** `scripts/linux/server-gate.sh` runs the gate for one commit from a workstation:

```bash
scripts/linux/server-gate.sh <ref> [--ssh-host <alias>] [--scratch-dir <path>] [--shards <n>]

# host and scratch directory from the environment
export ARMADA_GATE_SSH_HOST=<server-host>
export ARMADA_GATE_SCRATCH_DIR=<scratch-dir>
scripts/linux/server-gate.sh HEAD
```

The script:

1. Refuses to run while tracked files have uncommitted changes. The gate tests a commit, so commit first.
2. Creates `<scratch-dir>/repo.git` on the host when it is absent, and pushes the commit to it under `refs/gate/<sha>`. No branch moves.
3. Clones or fetches into `<scratch-dir>/worktree`, checks the commit out detached, and removes untracked build output.
4. Builds `src/Armada.sln`, then runs `scripts/common/run-tests.sh` with its logs kept.
5. Prints the combined summary and exits non-zero when the build or any runner fails. The build log, the combined log and every runner log (`runners/`) stay on the host under `<scratch-dir>/logs/<time>-<sha>/`.

Only one gate runs per scratch directory at a time. The host needs git, bash and the .NET SDK; `~/.dotnet` is added to `PATH` when `dotnet` is not already on it.

**Rules.**

- The gate tests a commit pushed to the scratch repository. It never runs in a shared checkout, a deployed checkout, or any directory outside the scratch directory, and it never touches a running service.
- Keep the host alias and the scratch path out of the repository. Pass them as arguments or through `ARMADA_GATE_SSH_HOST` and `ARMADA_GATE_SCRATCH_DIR`.
- Only ssh reaches the network; every other step runs locally or on the host.

## Git in Test Processes

Every test host (`unit`, `automated`, `runtimes` and `shared`) calls `TestGitEnvironment.DisableAutoMaintenance()` before any test runs. It sets `maintenance.auto=false`, `gc.auto=0` and `receive.autogc=false` for every git process the host starts: test helpers and the production code under test (for example `GitService`) inherit them. Production defaults are unchanged.

The settings use two layers:

- `GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_n` / `GIT_CONFIG_VALUE_n`, appended after any entries already set, so the settings have command-line precedence for the processes the host starts.
- `GIT_CONFIG_SYSTEM`, pointed at a generated file in the temp directory that includes the original system configuration and adds the same settings. Git's local transport clears the `GIT_CONFIG_COUNT` entries before it starts `git-receive-pack` in the other repository, so without this layer a push to a file-path remote still runs maintenance there.

The `Test runner contracts` suite asserts both: git reads the settings, and a traced commit and push to a file-path remote start no `git maintenance` or `git gc`.

## Sharded Unit Runs

`test/Armada.Test.Unit` accepts `--shard <index>/<count>` (1-based) and runs only the suites assigned to that shard. `--list-suites` prints the suite names a run would execute, one per line; with `--shard` it prints that shard's names. A shard cannot be combined with `--suite`. A shard assigned no suites fails like any other empty selection.

```bash
dotnet run --project test/Armada.Test.Unit/Test.Unit.csproj --framework net10.0 -- --shard 2/6
dotnet run --project test/Armada.Test.Unit/Test.Unit.csproj --framework net10.0 -- --list-suites --shard 2/6
```

Each shard prints `Shard i/N: k of m suites` and the normal summary.

- **Assignment.** `SuiteShardPlan` (in `Armada.Test.Common`) is deterministic. Serial suites go to shard 1. The other suites are placed heaviest first on the shard with the least expected time, ties broken by suite name and then by the lowest shard index. Each shard keeps registration order. Every suite lands on exactly one shard, so the union of the shards' `--list-suites` output is the full list with no duplicates.
- **Weights.** `test/Armada.Test.Unit/shard-weights.json` maps suite name to expected seconds; a suite it does not name gets `DefaultSeconds`. Regenerate it from one or more unit logs (serial or shard logs); the largest total per suite wins:

  ```bash
  dotnet run --project test/Armada.Test.Unit/Test.Unit.csproj --framework net10.0 --no-build -- --list-suites > suites.txt
  python3 scripts/common/generate-shard-weights.py unit-shard-*.log --suites suites.txt > test/Armada.Test.Unit/shard-weights.json
  ```

- **Serial suites.** `test/Armada.Test.Unit/serial-suites.json` names each suite that always runs on shard 1, with its reason: it touches machine-wide state (for example the shared temp directory), asserts a wall-clock bound, or changes process-global state (environment variables, `HOME`, `PATH`, `TZ`, static registries). Each shard is its own process, so process-global state cannot leak between shards; keeping these suites together keeps them out of the balanced split and in one place to review. Every run loads the list, and a name that is not a registered suite fails the run, so a rename cannot silently unpin a suite. A new suite of this kind is added to the list in the same change.

## Tests That Wait On Time

A test does not wait out a real timeout, interval or retry backoff. When production code has one, it exposes an injectable value with the production default unchanged, and the test passes a short value or a fake clock:

- a constructor `TimeSpan` or settable interval (for example `SelfDeployNativeCommandRunner(TimeSpan)`, `ArmadaServer.HealthLoopInterval`, `AgentLifecycleHandler.ProcessLivenessInterval`);
- a `TimeProvider` paired with a delay that advances it (`FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`, as in `OpenCodeServerLauncher`);
- a delay function that records the requested wait and returns at once, so the test asserts the backoff instead of sleeping (`ReleaseWebhookDispatcher`, `DeepSeekInferenceClient`, `VoyageEmbeddingClient`).

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

The fork unit, automated, runtime and database runners are console applications. The shared test suites run through the `src/Test.Automated` console runner and also have NUnit and xUnit adapters under `src/`; see [Shared Suite Runner](#shared-suite-runner) and [test discovery](reference/test-discovery.md). Use the command for the selected runner; `dotnet test` does not execute the console runners.

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

The legacy executables remain the owners of the fork cases they execute. The shared runner owns every shared case it executes. Each behaviour has one executed implementation: a shared case that only repeats an executed legacy case is deleted, and a legacy case that only repeats an executed shared case is deleted, so neither survives as a skip. When a shared case cannot execute correctly, it is recorded once in `src/Test.Shared/Infrastructure/SharedCaseDispositions.cs` and reported by every runner as a named, counted skip:

| Disposition | Meaning | Owner named in the record |
|-------------|---------|---------------------------|
| Duplicate of an executed legacy case | The shared copy predates a contract change that the legacy case already asserts. | The legacy file and registered case name |
| Awaiting owner decision | The shared case asserts behaviour the fork does not implement. | None; the owner decides whether to implement the behaviour or retire the case |
| Intentional fork difference | The shared case asserts behaviour the fork has decided not to adopt. The reason says what the fork does instead and why. | None; the decision is made, so nothing is pending |

The runner prints each kind with its own prefix and count, so a decided fork difference never reads as pending owner work. Discovery fails when a record names no discovered case or names a legacy case that its file no longer registers, so a rename or removal cannot silently hide a case. The per-case list and the reasons are in [the case mapping](reference/test-discovery-cases.md#shared-runner-failure-inventory).

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
- **Test.Automated** tests the full stack (REST API, MCP tools, WebSocket) and can target any database backend via the `--type` argument.

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
- **Listen ports.** Both hosts, and the route-contract proxy, find their loopback ports by binding port 0 and releasing it, then start the server on those ports through `LoopbackPorts.StartAsync` (`src/Test.Shared/Infrastructure`). Another socket on the host can take a port in between, so a start whose bind fails with address-in-use is stopped and repeated on newly found ports, up to five attempts; each repeat is printed to standard error. Any other start failure propagates at once.
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
