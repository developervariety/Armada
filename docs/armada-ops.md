# Armada Operator Guide

Routing preview accepts a mission capability hint and shows the Legacy selector's
ranking factors beside each candidate. Use Settings > Routing > Preview Smart
Routing to inspect tier, active capability score, rank and tie preferences.
See [Smart Routing](USAGE_ROUTING.md) for the selection rules.


This guide is the canonical operating procedure for Armada. It describes how an
orchestrator creates, monitors, verifies, lands, and closes work, and it lists
every MCP tool the server registers.

The guide is split into per-chapter files under [`ops/`](ops/) so an
orchestrator can load only the chapter it needs instead of one large file. Each
chapter file opens with front-matter (`topic`, `summary`, `read_when`,
`applies_to`, `tier`) for retrieval. Section numbers such as `4.6` and `8.19`
still appear inside the chapters, and a cross-reference to one resolves to its
chapter file below.

**The chapters are operator-local.** A filled chapter names a deployment's real
hosts, vessels, accounts, and policy, so git ignores it. The repository tracks a
template for each chapter (`ops/NN-<chapter>.example.md`); copy it without
`.example` and fill it in. See [`ops/README.md`](ops/README.md). Product
reference that every deployment shares stays tracked: the
[MCP tool catalog](MCP_TOOL_CATALOG.md), [typed decisions](TYPED_DECISIONS.md),
and [model routing](USAGE_ROUTING.md).

Use [ORCHESTRATOR_INSTRUCTIONS.md](ORCHESTRATOR_INSTRUCTIONS.md) as the prompt bootstrap for any orchestrator runtime. Use [MCP_API.md](MCP_API.md) for transport and schema discovery. Use
[DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
detail. Use [MERGING.md](MERGING.md), [PIPELINES.md](PIPELINES.md), and
[SCHEDULING.md](SCHEDULING.md) for subsystem detail. Use
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for playbooks, runbooks, workflow
profiles, environments, personas, pipelines, and their links.

For captain evidence scope and complete papercut page traversal, use the
[read contracts in MCP_API.md](MCP_API.md#captain-evidence-reads).

Generated captain instructions use [unified context recall](CONTEXT_INDEX.md#unified-retrieval-and-the-armada_search_context-tool)
for guidance, native facts and indexed code. Compatibility recall tools share
the same durable mission call budget. Keep them available while custom client
and template cutover is unverified.

For a rescue after Judge rejection, the brief points to the full, safely
redacted parent output and every rescue stage reads it with
`armada_mission_output`. The tool returns pages with one digest for the full
artifact. The stage reads until `hasMore` is false and checks `complete` and
`sha256` before it treats the review as complete. The embedded review cap is
12,000 characters. The brief keeps Correctness, Failure Modes, Tests, Evidence,
Residual Risks and Verdict findings ahead of less actionable text; if protected findings exceed
the cap, it marks the embedded list incomplete and points to the full output.
Unbulleted `R<number>` and `P0`–`P3` findings retain their explanation and
source anchors. Passing test summaries do not appear as required fixes.
The final criterion result determines completion even when its description
contains a failure-result phrase.
Every rescue stage carries the current owning objective brief. Binding revision
requirements belong on that objective, rather than only in coordination chat.
Declared execution requirements are checked before rescue dispatch. Missing
requirements leave an `execution_unavailable` incident without launching work or
spending the recovery attempt. Reconsideration uses the original failure's
lookback window; after that window, the operator dispatches new work from the
objective and preserved branch. This checks declared environment facts, not
service reachability. Before non-objective dispatch, Armada checks that every
required pipeline persona and requested tier has configured captain coverage.
Rescue planning checks the rescue Worker and each required re-review stage before
it creates rescue work. An unserviceable requirement records a refusal or
deferral; it does not treat temporary capacity pressure as missing configuration
or spend a rescue attempt. A rescue that re-runs its review stages checks fleet
and sibling-lane capacity before it writes its rescue voyage, so a full lane
writes no voyage. When capacity fills between that check and the admission, the
rescue voyage that received no mission is deleted rather than left as a
cancelled record. A capacity deferral spends no recovery attempt. Its
`autonomous_recovery.capacity_deferred` event and its
`rescue_capacity_deferred:` line on the incident's `RecoveryNotes` are written
when the deferral first happens and again only when its refusal code or limit
changes; each later sweep that is still refused logs the refusal with the
number of rescues waiting for capacity.
Later stages can read the failed mission through the same-owner rescue chain.
The marked rescue root names the additional failed-parent grant. Existing
direct-parent and dependency reads remain available within each caller's
normal evidence scope.

When a stage fails and its voyage is kept alive so a rescue can take over the
work, the stages waiting behind the failed stage can never run, so Armada
cancels them through the one dependent walker. This holds however the stage
failed, including a stalled captain whose recovery is exhausted and a stage
watchdog that fires with no captain heartbeat, so the parent voyage reaches a
terminal state and stops counting toward the fleet and sibling-lane limits once
nothing in it can make progress. The rescue voyage is separate and runs to
completion on its own.

For a premise check's selected fact and unknown identity, use
[the premise contract](MCP_API.md#armada_check_premise). The default TestEngineer
role respects explicit objective limits on test changes and preserves required,
permitted validation; see [pipelines and personas](../README.md#pipelines-and-personas).

## Judge lenses and bounded reviews

Judge reviews use three lenses: correctness, safety and blast radius, and
source fidelity. A single Judge uses all three. A parallel Judge group assigns
one primary lens to each Judge in creation order, cycling through the three
lenses. Each Judge also reviews through the other two lenses. Source-fidelity
review checks ported values and test vectors against real source material.

A Judge can block only when the review shows a real affected case in the
actual code or corpus. A hypothetical case that cannot occur is a follow-up
note. For each deliverable in a code, test, data or document change, the Judge
cites the branch diff against its base. Presence at the branch tip alone does
not prove delivery. A report required in a stage's final response is checked
in that stage's mission output. The active Judge prompt defines the full
review sections, evidence and verdict contract.

A completed report can await its configured future reviews without cancelling
those stages. The completion gate verifies the pending owned dependency chain
and any stated review-role count; unfinished current or earlier work remains
blocking. See [report completion](PERSONAS.md#a-report-only-worker-that-ends-complete-with-an-unfinished-report).

## Check evidence reuse

Automatic Build and UnitTest Checks reuse a full passing definition-of-done
command on the same vessel and commit with the same command and environment.
Gates publish this evidence before they release their host command slot. Checks
read it before and after waiting for a slot.
The Check records the source gate in `SatisfiedBy` and has no execution duration.

## Consumer verification inputs

Consumer verification applies workflow environment variables to its provisioned
consumer and sibling worktrees. Absolute paths under their registered working
checkout roots are rebased to the corresponding verification tree, preserving
relative suffixes. The longest matching directory root wins. Relative paths,
path lists and unrelated values retain their configured meaning. Build, test
and retry commands use the same rebased environment.

## Blocked gate recovery

A recorded definition-of-done failure class of `Infra` or `Timeout` blocks
an autonomous rescue on the same host. The blocked event message contains the
reason `gate_class_not_rescuable:`. The incident retains that reason, the failure
class and the original diagnostics. The operator corrects the host or workflow
configuration and reruns the gate. `Compile` and `TestFail` remain eligible for
rescue under the other recovery rules.

A `Timeout` recorded while the host was overloaded, while another build or
test command held a host command slot at the same time, or while the command
was still writing output, has already been run twice: the gate runs once more
with the host to itself before it reports the failure (after load falls, when
the host was overloaded), and records `mission.definition_of_done_load_rerun`
with the reason `host_overloaded`, `host_slot_shared` or `output_progressing`
(see `docs/reference/backend-dod.md`). A `Timeout` that reaches this block with
that event beside it timed out on both runs.

A `Timeout` whose re-run still read as host load keeps one rescue. Its failure
reason's first line carries `(host_load_timeout: ...)`: on the re-run, which
held the host alone, the host ran overloaded or the command was still writing
output at its limit. The rescue decision reason is `host_load_timeout_retry:`,
and the rescue carries the same work through a fresh gate. A rescue that times
out again is not rescued again (the auto-rescue block). When a suite needs more
time than the limit gives it on this host, set `gateCommandTimeoutSeconds` on
its workflow profile instead of relying on the rescue.

A full six-role Research chain can be replayed by an exact-owner global
operator with an explicit preview and fingerprinted apply. All prior reports
and statuses remain in immutable attempt history; the same stage IDs run again.
This is separate from ordinary restart and does not waive independent duties
or reset autonomous recovery budgets. See
[Full Research attempt replay](PIPELINES.md#full-research-attempt-replay).

A failed stage whose voyage ended `Failed` can also be restarted by an operator
(`restart_mission`): the restart reopens the voyage and runs the later stages
that were cancelled behind it again, whatever the failure class.

A `PostTestStep` failure keeps the rescue. Every test passed and only a step
after the tests failed, on the gate's run and on its one retry (see
`docs/reference/backend-dod.md`), so neither this vessel nor a consumer has a
test to fix. The rescue decision reason is `post_test_step_retry:`, and the
rescue carries the same work through a fresh gate run. A rescue that fails the
same way is not rescued again. A consumer suite that fails this way carries the
command label `consumer_post_test_step_failed: <consumer>` and never reads as
`consumer_test_failure_not_rescuable:`.

A `TestFail` on a declared consumer's test suite (command label
`consumer_tests_failed: <consumer>`) also blocks the rescue. The gate runs a
consumer's suite only after the vessel's own build and tests pass, so the
failing tests are in the consumer's repository, and a rescue in the producer
cannot change them. The blocked event and the incident summary carry the reason
`consumer_test_failure_not_rescuable:`. The incident recovery notes name the
consumer vessel, its id, and the failing consumer tests. The operator fixes the
consumer repository, or corrects the producer change when the consumer failure
is shown to come from it, and reruns the gate. A `TestFail` in the vessel's own
suite stays eligible for rescue.

A Judge NEEDS_REVISION or FAIL that declares a `CRITERION-CONFLICT:` line is
held for an operator ruling, not failed. The Judge requires a fix that cannot
be made without breaking a stated acceptance criterion, so a rescue would fail
the next Judge. The mission stays `WorkProduced` under the operator-review
hold, the voyage stays open, and the inbox reads "Awaiting operator ruling on a
criterion conflict" with the incident whose root cause `criterion_conflict`
lists each conflict. Rule with `armada_review_hold`: `clear` lands the work as
it stands, `fail` ends it `Failed` with the `criterion_conflict:` rescue block.
Either ruling closes the incident. See [PIPELINES.md](PIPELINES.md),
"Criterion conflicts".

DoD commands use their workflow profile's environment variables. Consumer commands
use the consumer profile. Isolated reruns keep those variables and add their failing
test names. Reused evidence compares the configured environment with unambiguous field
lengths, so a newline in a value cannot match another variable.

## Check rules on a split voyage

A voyage splits into lanes when two or more stages depend on the same stage,
as when an Architect plans several lines of work. A lane is a leaf of the
voyage's `DependsOnMissionId` graph plus every stage above it. The members of
one parallel stage group are one step of one lane. A voyage in which no stage
has more than one dependent has a single lane, and its Checks follow the
ordinary rules unchanged.

On a split voyage each lane is decided by its own Check records:

- A record belongs to the lane whose stages hold its mission, else its
  commit, else its branch. A record that matches several lanes or none (an
  unstamped record armed at dispatch, or one run at the planner's commit)
  decides no lane.
- A Judge's gate reads only the records of its own lane and the records
  attached to the Judge itself. Another lane's green is not stale for it, and
  another lane's red does not reject it.
- A record is stale only against the work of its own lane. The supersession
  re-arms it stamped at its lane's commit, never at another lane's.
- A lane whose stages are done runs its Checks while another lane still
  works. Once a lane is done, every Check type armed for the voyage that the
  lane has no record of is armed for it, stamped at its commit. A lane with no
  record of its own is therefore held for a verdict, not read as green.
- A record that spans several lanes is cancelled once every lane it spans has
  a record of its own for that Check type.

The Judge gate, Check eligibility and stamping, and the supersession all
resolve lanes through one rule (`VoyageLanes`), so they cannot disagree about
which lane a record measures.

## Chapters

These links open the tracked templates. On an installed deployment, read the
corresponding filled file without `.example` from its configured docs root.

| # | Chapter | Read when |
| --- | --- | --- |
| 1 | [Source Of Truth](ops/01-source-of-truth.example.md) | Deciding what to trust as the state of work, delivery, or a result. |
| 2 | [Selective Upstream Integration](ops/02-selective-upstream-integration.example.md) | Working on upstream absorption, or checking whether a reviewed upstream feature is live. |
| 3 | [Available Features And Active Policy](ops/03-available-features-and-policy.example.md) | Before depending on a feature such as code indexing, autonomous recovery, or a landing mode. |
| 4 | [Connection And Discovery](ops/04-connection-and-discovery.example.md) | Connecting a client to the Admiral, or starting an operator session. |
| 5 | [Standard Workflow](ops/05-standard-workflow.example.md) | Dispatching and running any non-trivial work through to a closed record. |
| 6 | [Recovery And Incident Workflow](ops/06-recovery-and-incidents.example.md) | A mission or voyage failed, or an incident needs driving to closure. |
| 7 | [Release And Deployment Workflow](ops/07-release-and-deployment.example.md) | Shipping a release, verifying or rolling back a deployment, or upgrading the running Admiral. |
| 8 | [Configuration And Administration](ops/08-configuration-and-administration.example.md) | Changing settings, routing, migrations, or the typed-decision configuration. |
| 9 | [Complete MCP Tool Catalog](MCP_TOOL_CATALOG.md) (tracked) | Choosing a tool, or checking what a tool family does and its risk. |
| 10 | [Safety Rules](ops/10-safety-rules.example.md) | Before any cancel, delete, purge, restore, rollback, or server stop. |
| 11 | [Verification Checklist](ops/11-verification-checklist.example.md) | Before reporting completion of any objective or task. |

## Dashboard routes and already-integrated work

The dashboard provides Code Index at `/code-index` and notification history at
`/notifications`. The `/token-usage` path redirects to `/activity?source=tokens`.
Code Index reports why a selected vessel's index is unavailable and discards
responses from a previous vessel selection. The vessel page shows repository
health from `GET /api/v1/vessels/{id}/repository-health`. The report is
advisory: it does not replace readiness, preflight, exact-commit Checks, or
the definition-of-done gate, and it does not block dispatch. A Harbor-owned
vessel is evaluated on its owner host. An offline owner is unavailable. The
evaluation uses read-only Git commands and does not fetch.

Dashboard GET requests retry a connection failure after 250 ms and 750 ms,
with one deadline covering the attempts and the response body. HTTP refusals
and server errors remain visible. A failed write is not repeated. Ask Armada
keeps the question and any partial reply when the response is lost or times
out. It shows an unconfirmed-result notice and releases the composer without
a tab reload. Check the result before resending a request that can run tools.

Ask turns launch a captain's CLI runtime with read-only native tools by default: no
native shell commands and no file writes or edits. The Dashboard Server page
(Ask Settings) and `ask.nativeToolAccess` in `settings.json` widen that to the
mission launch defaults for every Ask turn. A runtime that cannot express read-only
is refused for Ask with a message that names it. Mission launches never use this
setting. See [native tool access](ASK_CONVERSATIONS.md#native-tool-access).

The home dashboard reports failed reads in an inline alert and keeps the last
available rows and voyage labels. The alert names the failed section and its
error. Retry and the next scheduled or relevant event refresh use the same
bounded load path. A successful refresh clears the read alert. Action errors
keep their own dialog. Chat tokens, logs and protocol acknowledgements do not
trigger home reloads; record changes and reconnect resyncs do.

An implementation mission whose branch already matches the merge-queue target
requires a recorded delivery commit and a reachable commit made strictly after
`Mission.StartedUtc`. Git commit time supplies this check. Missing or unreadable
history fails closed. Review stages and rescues with no substantive work cannot
use this path.

## Verify a continuation base

Set `StartFromRef` on the objective, or supply `missions[].startFromRef` at
dispatch to override it. Check `MissionStartRefs` in the dispatch result and
`mission.start_ref_resolved` in the event log against the accepted commit.
Alias dispatch follows the same rule. Downstream stages use the predecessor
branch and do not have a separate start reference. The exception is a worker
fanned out from an Architect plan: it is cut fresh, from the commit the
Architect started at when that was not the target branch, so a plan made on a
recovery tip builds on that tip. The planner guard counts only what the
planner changed after that start commit; code inherited from the tip is not
planner-authored. When a non-target start commit cannot be resolved, the guard
fails the planner and says the attribution is unproven.

A dock collision reports the holding worktree and detected process IDs in
the mission failure reason. A live owner is protected from reclamation. A
second `dock_worktree_held` failure ends the mission with `retry bound`; the
operator can inspect the reason without reading server logs. A stage whose
inherited branch is checked out in the dock of an earlier stage of its own
chain is not a collision: it returns to Pending with the assignment hold
`predecessor_dock_held`, naming that stage, and does not count toward the
bound. That dock is released when the earlier stage's completion handling
hands off, so the stage is assigned on a later pass.

A restart resets an in-flight mission to Pending and keeps its branch and its
active dock. A root (stage-1) mission reassigned this way continues on its own
persisted branch at the committed head; the replacement captain attaches to that
branch rather than cutting a fresh one from the default branch, so committed work
is never orphaned. Continuation is authorized by an active dock on the same
vessel, tenant and branch, resolved from the live dock records and not from the
mission's dock pointer. A retry after a failed launch or provision clears that
pointer but keeps the committed branch, and the still-active dock re-authorizes
the continuation, so the retry never cuts a fresh branch from the default branch.
When the persisted branch still carries committed work but no active dock
authorizes it, the mission holds with the assignment hold
`root_continuation_dock_missing` rather than cutting a fresh branch. If an active
dock authorizes the branch but it no longer resolves in the vessel repository,
the mission holds with `root_continuation_ref_missing`. A branch name with no
committed work and no active dock still gets a fresh captain branch, exactly as a
first assignment would.

## Ref cleanup evidence

See [Branch Cleanup Policy](MERGING.md#branch-cleanup-policy) for managed
namespaces, deletion events, and the limits of raw Git attribution.

## Repeated model gateway timeouts

A recognized model gateway timeout can retry once for each mission. If it
occurs again, the mission fails with `provider_timeout` and the configured
endpoint label. It does not requeue onto another captain on that endpoint.
The retry and failure facts retain the same cause code. See
[model gateway timeouts](USAGE_ROUTING.md#model-gateway-timeouts) for the
endpoint label and retry rules.

## Captain board notes

Captains can post one-line notes from their normal worker output. A line that
starts with `[ARMADA:NOTE] ` is stored as a Captain-authored message on the
shared coordination board when board storage is available (SQLite or
PostgreSQL). Ask captains to post a claim note before work and an outcome note
when work ends. Each note can contain at most 500 characters; Armada redacts
credential-shaped values and accepts at most 20 Captain notes for one mission.
Keep notes in plain text and do not include absolute host paths. Notes are
advisory records for operators and the dashboard. They do not control mission
state and do not send messages to other captains. This path does not add
captain access to operator coordination tools.

## Admiral memory growth

The admiral samples its own memory every 5 seconds. While the managed heap or
the working set is at or above 6 GiB, it logs an `[AdmiralMemoryWatch]`
warning. The warning gives the managed heap, the working set, the container
memory and limit, and the REST requests still running, oldest first with
their age. It repeats after each further 1 GiB of growth or every 60 seconds.
The request log records a request only when it completes, so a request that
is still running when the process is killed appears only in this warning.

Every sample above the threshold also updates the run marker
(`admiral-run.json` in the data directory). After an out-of-memory kill, the
next start logs the last time the run was known alive, with its managed heap
and container memory, and records an `admiral.unclean_exit` event. To find
the cause, read the last `[AdmiralMemoryWatch]` lines before that time and
the kernel journal (`journalctl -k`).

Reads that span the whole history are bounded. The history timeline reads at
most 2000 records from each large source, newest first, and names a source
with more records in `TruncatedSources`.

## Startup refusal for default credentials

A new database seeds the admin `admin@armada` with a published default password
and a `default` bearer token. Before the REST listener binds, the admiral
checks them against `Rest.Hostname`. On a loopback host it logs a warning. On
any other host it exits with code 4 and prints `STARTUP REFUSED` with the active
credentials; nothing is bound, so a restart loop reads as a crash, not an
outage. Resolve it with `ARMADA_INITIAL_ADMIN_PASSWORD` (applies once, while the
default password is active), by changing the password through
`PUT /api/v1/account/password`, or by setting `AllowDefaultCredentialsOnNetwork`
to `true`, which logs a warning at every start and accepts the exposure.

Changing the admin password deactivates the `default` bearer token. Any client
that still sends it, including Helm's stop command without a configured
`ApiKey`, receives `401` until it uses a real credential. MCP authentication and
caller scopes do not change.
