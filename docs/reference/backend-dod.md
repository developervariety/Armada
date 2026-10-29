# Definition-of-done history

Mission completion records every definition-of-done result as a scoped
event. A read-only mission report shows the current gate configuration and
the latest recorded result. Neither changes when the gate runs, what it
decides, or landing readiness.

## Recorded evaluation event

Event type: `mission.definition_of_done_evaluated`. The event carries the
mission tenant and user, mission, captain, vessel and voyage identifiers. The
payload is a `DefinitionOfDoneEvaluationRecord`, schema version 1:

| Outcome | When |
| --- | --- |
| `Passed` | The gate ran its required commands and they passed. |
| `Skipped` | The gate does not apply: disabled, persona not applied, or doc-only marker. A skip is not evidence that a build or test ran. |
| `NotVerifiable` | The mission changed nothing, so the commands would measure the base commit. The gate did not run. |
| `Failed` | A required command failed. Label, exit code, failure class and output tail are recorded. |
| `EvaluationError` | The gate threw. Only the exception type is recorded, not its message. |
| `Cancelled` | The mission was cancelled while the gate ran or waited for the host-wide command slot. The gate's command process was stopped and the slot released. No build or test result exists. |

The record also holds the captain, dock, branch, the mission commit hash when
known, the recovery attempt count, and start and end times. The failure output
passes through the shared secret redactor and keeps only its last characters,
4,000 at most including a truncation marker. A failed command also writes its
complete output retained by the bounded process runner to a mission-owned
capture sidecar. The evaluation event stores the capture ID, UTF-8 byte count,
and SHA-256 digest. Read the capture through the mission log endpoint with
`captureId`, `offsetBytes`, and `lengthBytes`; each page is bounded to 64,000
bytes and aligned to UTF-8 character boundaries. The capture is redacted and
the mission scope is checked before the server reads it. A capture write
failure is named in the record and activity log. Runner truncation, timeout,
pipe-drain timeout, or incomplete process containment remains visible in
`RunnerOutputTruncated`, `RunnerOutputOmittedBytes`, and
`RunnerOutputIncomplete`. `Complete` on a capture page means the runner did not
report possible output loss; it does not change the gate result. Each page
returns the whole-capture digest without reading the full file again. After it
joins all pages, the caller can hash the reconstructed bytes and compare them
with that digest. A completion stopped by server shutdown records nothing.

Cancelling a mission (the operator cancel, or a status transition to
`Cancelled`) stops a gate still running for it: the gate's command process
group is killed, the host-wide command slot and dock lease are released, and
completion records `Cancelled`. The mission keeps the `Cancelled` status the
cancel wrote; it is not failed, handed off or landed.

A failed event write is logged with the outcome and exception type. It never
changes the gate outcome or the mission decision.

## Time limits and host load

Each gate command has a time limit: the `gateCommandTimeoutSeconds` of the
workflow profile the command comes from (the vessel's profile for its own
commands, a consumer's profile for that consumer's build and tests), or the
global `DefinitionOfDone.CommandTimeoutSeconds` (default 600, clamped to
30-3600) when the profile sets none. An isolated re-run of a few failing tests
uses the global limit. The limit starts
when the command's process starts, after the host-wide command slot is granted,
so a wait for the slot never counts against it; a wait of a second or more is
logged as `<label> command waited N s for the host command slot`, before the
`running <label> command` line. The limit does count everything the command
does after it starts, including a lock the command takes itself (for example a
`flock` around a suite that captains also run). On Linux the message of a
command stopped at its limit therefore names the processes still in its
process group, redacted and bounded to 20 command lines of 200 characters:
`Still running at the limit: flock -x <lock> ...; dotnet test ...`.

The record keeps two separate facts about a command that hits its limit:

- `TimedOut` is true whenever the command hit its time limit.
- `FailureClass` is `TestFail` when the output named failing tests before the
  limit, and `Timeout` when it named none. A `TestFail` that also timed out
  keeps the named tests in `FailedTestNames` with `FailedTestNamesOverflow`
  set, because the tests still running at the limit never finished, and its
  failure reason reads `<label> command exited -1 and also hit its time limit,
  so some tests did not finish`. Autonomous recovery reads that as a test
  failure; it blocks a rescue only for `Infra` and `Timeout`.

The host is overloaded when the 1-minute load average (the first field of
`/proc/loadavg`) is above 2 times the logical core count. A host without
`/proc/loadavg`, or a reading that cannot be parsed, counts as normal load.

**Before the first command.** A gate that will run commands and is due to
start on an overloaded host waits first, so its time limits count run time and
not time queued behind host load. It holds its dock lease and no host slot,
reads the load every 30 seconds, and starts as soon as the host is no longer
overloaded, or after 20 minutes even when it still is. It records event
`mission.definition_of_done_load_admission`; the payload is a
`DefinitionOfDoneLoadAdmissionRecord`, schema version 1: reason
`host_overloaded`, the load when the gate was due and when it started, the core
count, the threshold, whether load fell, and the seconds waited. A gate that is
skipped does not wait, and a host that is not overloaded starts at once and
records nothing.

**After a time limit.** When an evaluation fails with `TimedOut` set, the gate
reads the load again. The run stood under load when the 1-minute or the
15-minute average (the third field) is above the threshold; the 15-minute
average counts because the 1-minute average falls as soon as the timed-out
command's own processes exit. Two more facts about the timed-out command make
its time limit read as host load rather than a hang:

- **It shared the host.** Another build or test command held a host command
  slot at the same time as the timed-out command, at any moment while it ran
  (`TimedOutOnSharedHost` on the result; the timeout message adds `Another
  build or test command held a host command slot while this command ran.`).
  With `HostCommandSlots` above 1, commands of different vessels hold slots
  together; a command's own work, such as one vessel's suite loading the host
  while another's runs, then counts inside the second's time limit, at any load
  average. Two commands of one vessel never hold slots together: the per-vessel
  suite lock below admits only one of a vessel's suite commands to the slot
  queue at a time.
- **It was still writing output.** The command wrote to standard output or
  standard error within a quarter of its time limit, and at most 60 seconds,
  before the limit fired (`TimedOutWhileProgressing`; the message adds `The
  command was still writing output at the limit.`). A suite printing test
  traffic at its limit was working, not hung.

After a run under load, on a shared host, or with live output, the gate does
not report that result:

1. It releases the host-wide command slot and keeps its dock lease.
2. Only after a run under load: it reads the load again every 30 seconds until
   the 1-minute average is at or below the logical core count, for at most 20
   minutes. At 20 minutes it stops waiting even when load is still high.
3. It records event `mission.definition_of_done_load_rerun`. The payload is a
   `DefinitionOfDoneLoadRerunRecord`, schema version 3: the reason
   (`host_overloaded`, else `host_slot_shared`, else `output_progressing`),
   whether the command shared the host (`SharedHostSlot`), whether it was
   writing output (`OutputProgressing`), `RerunAlone` (true), the command that
   timed out, the 1-minute and 15-minute load at the timeout, the load at the
   re-run, the core count, both thresholds, whether load fell, and the seconds
   waited.
4. It runs the whole evaluation once more **with the host to itself**: the
   re-run asks the host-wide command lock for the whole host. It keeps its place
   in line, starts only when no other build or test command holds a slot, and no
   other command is granted a slot while it runs; requests behind it wait. So
   neither another command's load nor a lock both commands take can reach the
   re-run. Its time limits start when it holds the host, as every command's do.
   Its result is the gate outcome and is recorded as the evaluation event; there
   is no third run.

When the re-run also fails as `Timeout`, and its own time limit still reads as
host load (the host ran overloaded, or the command was still writing output),
the result carries `HostLoadTimeout` and the mission failure reason's first
line ends with `(host_load_timeout: ...)`. Autonomous recovery reads that
marker and rescues the work once (see `docs/armada-ops.md`, "Blocked gate
recovery"). A second time limit on a silent command fails the mission as a
plain `Timeout`.

A time limit hit at normal load on a silent command that held the host alone
fails at once. When the first evaluation fails as a host-fault Timeout (no
failing tests) or as Infra with a signal or timeout, and the result is not a
`HostLoadTimeout`, completion holds the produced work with
`definition_of_done_host_fault:` and records
`mission.definition_of_done_host_fault_hold`. The dispatch-hold GateReruns
sweep re-runs only the gate once, without relaunching the captain. A second
host-fault fails the mission with the existing failure. A `HostLoadTimeout`
fails at once: the inline load re-run already consumed that retry.

While the gate waits for load or for the host it is still a
running gate: it shows in `RunningGates` with a `SlotWait` that reads `Running`
while it holds a slot, `WaitingForSlot` with its `Position` in line while it
waits for one, or `Queued` before its first command, so a gate queued behind
others is told from one that is lost. Cancelling the mission stops the wait and
records `Cancelled`. The interrupted-gate re-run follows the same rules.

**After an isolated re-run that fails again.** When a red unit-test suite (the
vessel's own or a consumer's) is re-run once with only its failing tests, the
re-run runs on the same host as every other gate, check and captain. A fail
there does not rule out load, so the failure text never says it does. It reads
`Isolated re-run of N failing class(es) under the current host load also
failed.` and then the host load at the re-run: the 1-, 5- and 15-minute load
averages, the logical CPU count, `MemAvailable`, how many other
definition-of-done gates run or wait, how many other requests hold the host
command slot and how many wait for it, and how many captains are working. The
host was busy when any load average is above the CPU count, another request
holds the host command slot, or available memory is below
`HostCommandMemoryFloorMb`; the text then says the failure may still be
load-related and should be re-run on a quiet host before it is treated as a
defect. An unknown load average says a load-related failure cannot be ruled
out. A quiet host says load is an unlikely cause, which leaves a defect or an
ordering race in the tests. The same sentence is in the gate log.
The re-run reads its failing tests with the same workflow-profile
`FailedTestLinePattern` as the run it repeats, so its failure text leads with
the failing test names and its result carries them, even when the runner
prints them early in a long output.

**After a step that fails once every test has passed.** A test command (the
vessel's own or a consumer's) can exit non-zero after every test it ran passed:
a wrapper verifies its workspace after the tests, a cleanup step fails. When the
output has at least one test-run summary, every summary reports zero failed
tests (`Failed: 0` or `0 failed`, beside a passed count), and no line names a
failing test, the class is `PostTestStep`, not `TestFail`. A summary that
reports zero failures is never read as test-failure evidence. The gate runs the
same command once more in the same worktree, and that run is the result. When
the second run also fails as `PostTestStep`, the failure reason opens with `0
tests failed`, the number of test runs and passing tests, the exit code, and
the last lines the command wrote to standard error (the step that failed), and
says the command ran twice. Nothing is re-run in isolation, because no test
failed. A consumer's suite that fails this way carries the command label
`consumer_post_test_step_failed: <consumer>`, never
`consumer_tests_failed: <consumer>`.

**After a command the OS terminated by a signal.** A process the OS kills exits
with `128 + signal` on a POSIX shell, so `143` is SIGTERM, `137` is SIGKILL,
`130` is SIGINT. When a command exits in that range and no line names a failing
test, it did not finish on its own -- a stray kill from another process sharing
the host, a host eviction -- so the class is `Infra`, the environment-fault
class, not `TestFail` or `PostTestStep`. A run that still named a failing test
is a genuine failure whatever signal ended it, so it keeps `TestFail`.

**Consumer worktrees are locked while they are in use.** The gate provisions
each consumer and its siblings as worktrees of the vessels' repositories under
a scratch directory that only the admiral's own file system shows. It locks each
one (`git worktree lock`, reason `armada consumer verification in progress`)
right after it creates it, and unlocks it before it removes it. A `git worktree
prune` run by any process that cannot see the scratch directory skips a locked
worktree. Unlocked, the registration would be deleted mid-run, and every git
call inside the worktree, a consumer's own source checks among them, would fail
with `not a git repository`. A lock that cannot be taken is logged and the
verification goes on unlocked. A gate stopped before its cleanup leaves its
registrations locked; `git worktree unlock` and `git worktree prune`, run where
the admiral runs, clear them.

Each gate command (build, unit tests, each consumer build and test) waits for a
host-wide command slot, which it shares with check runs and merge-queue test
runs. `DefinitionOfDone.HostCommandSlots` (`definitionOfDone.hostCommandSlots`
in `settings.json`) sets how many slots the host grants at once: default 1,
clamped to 1-8, applied at startup and on every settings reload. Slots are
granted in request order. A gate takes a slot at its first command and holds
it until the gate ends, so its commands run back to back; work that asked
before the gate began runs first. A count above 1 lets several full suites run
at once, which risks false `Timeout` failures on a host without the CPU, memory
and disk to run them inside `CommandTimeoutSeconds`.

A gate, check run, merge-queue test, or captain suite lease names the vessel
whose suite it runs. `DefinitionOfDone.VesselSuiteSlots`
(`definitionOfDone.vesselSuiteSlots`) sets how many of one vessel's full suites
may run at once: default 1, clamped to 1-8, applied at startup and on every
settings reload. The host-wide cap remains `HostCommandSlots`. One vessel's
suite is also serialized outside Armada by a file lock the vessel's own command
takes (a `flock` around the suite), so two of a vessel's suite commands cannot
run at once however many slots are free: the second blocks inside its command
on that file lock. So a command that names its vessel takes an in-process
per-vessel suite slot before it queues for a host slot, and a command that
would then only block on its vessel's file lock holds no host slot meanwhile —
the slot stays free for another vessel's command. The per-vessel slot is always
taken before the host slot and released after it, and a command holds at most
one, so the order never deadlocks. A captain that runs a full suite takes the
same slots through `armada_suite_lease` (`docs/MCP_API.md`). A wait for that
lease records `captain.suite_lease_wait` and is not a stall. The lease is
released when the captain releases it, when the captain process exits, or when
the mission ends, before the mission's own gate queues for the slot. A gate's one re-run after
a host-load time limit holds the whole host: it is granted when no slot is
held, no other request is granted while it runs, and the requests behind it
keep their order. Its holder entry reads `definition-of-done gate re-run, alone
on the host, for mission <id>`.
`DefinitionOfDone.HostCommandMemoryFloorMb`
(`definitionOfDone.hostCommandMemoryFloorMb`) holds a free slot until the host
has that many megabytes of available memory (`MemAvailable` in
`/proc/meminfo`): default 6144, clamped to 0-1048576, 0 turns it off, applied
at startup and on every settings reload. An unknown reading never holds a
command. The held request keeps its place in line. When no slot is held and it
has been held for memory for 15 minutes, it runs anyway and is recorded as
granted under the floor.

A gate that passes as a whole records each vessel command it ran in full: the
build command, and the unit-test command when it passed on its first run. The
record holds the worktree HEAD and needs a worktree with no uncommitted tracked
change and the same HEAD before and after the command. An automatic Build or
UnitTest Check armed for the same voyage or mission, on the same commit, with
the same configured command and environment, passes by naming the gate instead
of running the suite again. Such a Check ran no command: its record carries
`satisfiedBy` (`kind` `definition_of_done_gate`, the gate's `missionId`, the
command `label`, the `commitHash` the gate ran on and `passedUtc`), its
`durationMs` is null, and its summary says it did not run the command. The
dashboard shows its duration as "Not run" with the gate's mission, and the
operator watcher prints `NOT RUN: satisfied by ...` in place of a run time. A
Check without `satisfiedBy` ran its own command. A failed, cancelled or
timed-out gate records nothing. The automatic Check runner also holds those Checks back while the gate
runs.
`DefinitionOfDone.HostSlotWaitReportMinutes` (default 5, clamped to 1-60) is
how often the automatic Check runner records a wait that lasts: the slot held
for memory, every slot busy with requests waiting, or every runner place taken.
`DefinitionOfDone.HostSlotWaitBoardNoteMinutes` (default 10, clamped to 0-1440,
0 posts nothing) is how long a wait lasts before one board note names the slot
holders, the waiting requests in order and the memory reading.

## Handoff requires a recorded result

Mission status is not evidence that the gate passed: completion writes
`WorkProduced` before the gate runs. A completion interrupted after that write,
for example by an admiral restart while the gate waits for the host-wide slot,
leaves a `WorkProduced` mission with no gate result. The lazy handoff that
dispatch runs for a dependent whose upstream was never handed off, and the
dangling-handoff recovery, therefore read the upstream's latest evaluation
event first. They hand off only when that event is newer than the upstream's
current launch (`StartedUtc`) and its outcome is `Passed`, `Skipped` or
`NotVerifiable`. Otherwise the upstream is held for operator review with a
`definition_of_done_not_run:` reason and its dependents keep waiting. This
applies only when a gate is wired.

The admiral health loop re-runs the gate for every mission held this way, one
background run per mission through the host-wide slot. A gate that passes or is
skipped on the re-run releases the hold, and the handoff or landing it stood in
front of runs. A gate that fails fails the mission with the gate's reason and
hands it to recovery. The orphan-dock reaper keeps the dock of a produced
mission whose gate has no result for its current launch, so the dock is there
when the re-run starts. A handoff after the hold is released, by the re-run or
by an operator, releases the mission's dock so the next stage can check out the
branch. While the re-run is running, `armada_review_hold` refuses `clear` with
`gate_rerun_running`. A mission whose dock is gone cannot be re-run: it stays
held, its activity log says why, and the operator clears the hold with
`armada_review_hold` to accept the work as it stands, or fails it to send it to
recovery.

The server builds the gate only at startup, and only when
`DefinitionOfDone.Enabled` is true. With no gate, completion records no
evaluation event. A settings reload replaces the `DefinitionOfDone` settings
object but not the gate: the running gate keeps the settings it was built
with, and enabling or disabling DoD takes effect only after a restart.

## Mission report

`GET /api/v1/missions/{id}/definition-of-done` reads the mission in the
caller's scope and returns `MissionDefinitionOfDoneReport`.

`Configuration` describes the gate mission completion actually uses, not the
current settings file. With no gate wired, `GateActive` is false, `Enabled` is
false and `ExpectedSkipReason` says completion records no evaluation. With a
gate, `DefinitionOfDoneGate.DescribeAsync` runs against that gate's own
settings. It uses the same skip rule and workflow-profile resolution as
evaluation: active profiles
for the mission tenant, vessel scope, then fleet, then global, with the default
profile first in each scope. It reports whether commands exist, never the
command text. It reports consumer settings and the global trigger prefixes.
Whether consumer tests run for a change is decided from the producer diff at
evaluation; the report does not read a diff. When evaluation cannot read the
producer's changed paths, it treats the change as reaching every trigger: each
consumer with trigger prefixes runs its suite, and the gate logs the
`changed_paths_unavailable` reason. An unreadable change is never treated as an
empty change. It does not list declared
consumers or per-edge trigger overrides.

`HistoryState` reads the latest two evaluation events for the mission in the
caller's scope (admin, tenant admin, or tenant and user):

| State | Meaning |
| --- | --- |
| `Recorded` | The latest record was read. `LatestEvaluation` holds it. |
| `NotRecorded` | No record in scope. Missions completed before this change have none. This is not a pass or a failure. |
| `Unavailable` | The latest record has no payload, is larger than 262,144 characters (checked before it is parsed), is malformed, lacks its schema version, outcome or times, has another schema version, uses a name that is not an exact declared outcome or failure class (numbers, combined names and case variants are rejected), has a negative recovery attempt count, combines an outcome with fields the writer never produces, the two latest share a timestamp, or the history could not be read. An older record is never reported instead. |

The report never infers a result from mission status, `Complete`, or Check
runs.

### Stored-record validation

Only mission completion writes this event; no REST route or MCP tool creates
events. The rules below therefore reject rows the writer cannot produce, such
as a damaged row or a manual edit, and each rejection names its reason.

| Outcome | Required | Rejected |
| --- | --- | --- |
| `Passed` | — | a skipped reason, command label, exit code, failure class, output tail or capture |
| `Skipped`, `NotVerifiable`, `Cancelled` | a skipped reason | a command label, exit code, failure class, output tail or capture |
| `Failed` | a command label | a skipped reason |
| `EvaluationError` | a command label | a skipped reason or command output capture |

The skipped reason and command label pass through the shared secret redactor
and keep their first 1,000 characters, a truncation marker included. The same
rule applies when the record is written and when it is read, so an older row
is returned with the same bound. The output tail keeps its existing 4,000
character bound. Captain, dock, branch and commit identifiers are never
truncated; the payload size limit is far above the writer's largest record
with full-length Unicode identifiers.

A reversed start and completion time is still `Recorded`. A clock step during
evaluation can produce it on a real result, and hiding that result would be
worse than showing its times. This is an accepted limit.

## Validation

- A skipped completion records a `Skipped` event with the disabled reason. The
  mission still reaches WorkProduced.
- A real enabled gate with no workflow commands records `Failed` with
  `missing-commands` and `Infra`. The mission still fails.
- Missing, malformed, unsupported-version, unknown-outcome and same-timestamp
  cases read `NotRecorded` or `Unavailable` as above. A malformed newest
  record hides an older pass.
- Tenant B cannot see tenant A's record.
- Vessel profile selection wins over global, and command text and secrets
  from both profiles are absent from the serialized report.
- Persona and doc-only skips are reported without running a gate.
- Failure output is redacted and bounded.
- Failed command output pages reconstruct the redacted runner capture, including
  output between selected diagnostics and the tail. The mission binding and
  capture markers are checked; the caller can verify the whole-capture digest
  after it joins all pages. Older evaluation events without capture metadata
  remain readable.
- API: the owning tenant reads the report, another tenant receives 404, and
  an unauthenticated caller receives 401.

No migration: the history uses the existing events table. No deployment is
included.
