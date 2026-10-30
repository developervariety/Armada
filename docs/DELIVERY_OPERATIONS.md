---
topic: "Delivery Operations"
summary: "Release, deployment, verification, rollback, incident, and runbook records."
read_when: "Shipping a release, running or rolling back a deployment, or driving an incident."
applies_to: orchestrator
tier: leaf
---
# Delivery Operations

This guide covers release, deployment, verification, rollback, incident, and
runbook operations. Use [armada-ops.md](armada-ops.md) for the complete
objective-to-closeout workflow and MCP catalog.

## 1. Record Ownership

| Record | Owns |
| --- | --- |
| Workflow profile | Repeatable build, test, package, deploy, verify, and rollback commands |
| Check | Command evidence and gate state |
| Environment | Rollout target and approval policy |
| Release | The work and artifacts that can ship |
| Deployment | Rollout, verification, monitoring, and rollback state |
| Incident | Impact, diagnosis, mitigation, and postmortem |
| Runbook execution | Evidence that an approved procedure ran |

Do not put this state only in a mission report or release note.

## 2. Prepare The Delivery Surface

Before a release or deployment:

1. Confirm the vessel working directory and default branch.
2. Confirm the workflow profile.
3. Confirm the target environment.
4. Confirm whether the environment requires approval.
5. Create Pending Checks for every required gate beyond build and unit test.
   Dispatch arms a voyage's Build and UnitTest Checks itself, so those two are
   already attached; add the rest.
6. Link the objective, voyage, missions, and Checks.

The profile should define the commands the delivery needs. Common fields are
build, unit test, integration test, package, release version, deploy, smoke
test, deployment verification, rollback, and rollback verification.

Pending Checks are requirements, not proof. They become proof only after a
real command result or valid external evidence resolves them. An armed Pending
Check is executed in place when the Judge stage reaches it, which is what lets
dispatch arm the build and unit-test gates without loading the host at the
moment the first captain starts working.

A check command runs through the platform shell as the leader of its own
process group. Its timeout, and a cancellation, kill the command with every
process it started. Both output streams are read at once, and each keeps 4 MiB:
past that the beginning and the end stay, with a marker naming the omitted
bytes. After the command exits, a background process that still holds its
output (a build server, for example) gets five seconds; the check then records
the exit code, and the admiral log names the held pipe. The Definition-of-Done
gate runs its commands the same way with 16 MiB per stream, and a failing-test
set read from truncated output is marked incomplete, so it is never compared.

Every build or test command the admiral starts runs with MSBuild node reuse,
the MSBuild server and the shared compiler server off
(`MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`,
`UseSharedCompilation=false`): Checks, Definition-of-Done gate commands,
merge-queue test commands, workspace exec commands, the API-endpoint
`run_command` tool, the self-deploy build, and captain launches. No MSBuild
worker node, MSBuild server or `VBCSCompiler` outlives the command, so an idle
admiral has no build process left running. A workflow profile variable of the
same name still wins for a Check, and a project that sets
`UseSharedCompilation` itself keeps its own value.

Use `list_workflow_profiles`, `get_workflow_profile`,
`validate_workflow_profile`, and `preview_workflow_profile` to inspect the
resolved command set. Use `create_workflow_profile` or
`update_workflow_profile` only with a complete profile object.

Use `list_environments` and `get_environment` to inspect the target. The words
Staging or Production do not prove that real deployment commands exist.

## 3. Create A Release

Use `create_release` when the work becomes a candidate for delivery.

Link:

- the vessel;
- the objective;
- the voyages and missions;
- required and completed Checks;
- artifacts and source commit;
- release notes that describe the delivered behavior.

Read the release with `get_release`. Use `update_release` to correct links,
notes, version, or state. `armada_update_release` is a compatibility alias.

Do not mark a release as shipped until its required evidence is complete.

### 3.1 CD webhook on approval

When a release transitions to Shipped and the optional `cdWebhook` setting is
configured (`enabled`, `url`, `bearerToken`, `timeoutSeconds`, `maxRetries`,
`retryBackoffSeconds` in `~/.armada/settings.json`), the admiral POSTs a
`release.shipped` JSON payload to the configured endpoint. The payload carries
the release identity, version, tag, and linked voyages, missions, and check
runs. An external continuous-delivery system can consume it to pick up
deployment.

Delivery attempts are recorded as `release.webhook.delivered` or
`release.webhook.failed` events on the release. Retriable failures (5xx,
network, timeout, auth) retry up to `maxRetries` times; a failure never blocks
the release update. Read delivery history with
`GET /api/v1/releases/{id}/webhook-events`, the CD Webhook Delivery card on the
dashboard release page, or by enumerating events for the release. Verify
endpoint reachability and authentication first with the MCP
`test_release_webhook` tool, which sends a clearly-labeled synthetic payload.

## 4. Create And Approve A Deployment

Use `create_deployment` with a named environment and a source release or ref.
Read the result with `get_deployment`.

### 4.1 The pinned candidate

A deployment resolves its `SourceRef` once, when it is created, to the full
commit the ref names in the vessel's own repository, and records it as
`SourceCommit`. The vessel's local repository is read first, then its working
directory; discovery never climbs above that directory. A branch is tried as a
local branch, then as `origin/<branch>`, then as a tag. `ArtifactIdentity`
records the artifact the deployment ships, such as an image digest or a
package version, when the operator supplies one. Together the commit and the
artifact are the candidate.

- A ref that names no commit in an existing repository refuses the create or
  the update with `deployment_source_unresolved`.
- A vessel with no git repository creates an unpinned deployment, and the
  admiral log names it.
- An update that keeps the ref keeps the pinned commit. An update that
  changes the ref resolves the new ref. A changed commit or artifact clears
  the recorded validation evidence.
- Once a deployment is approved, started or finished, its candidate is fixed.
  An update that changes the ref, the artifact or the vessel refuses with
  `deployment_candidate_frozen`; create a new deployment instead.

The deploy and rollback Checks run with `CommitHash` set to the pinned commit,
and every check command of a deployment receives the candidate as environment
variables: `ARMADA_DEPLOYMENT_ID`, `ARMADA_DEPLOYMENT_SOURCE_REF`,
`ARMADA_DEPLOYMENT_SOURCE_COMMIT` and `ARMADA_DEPLOYMENT_ARTIFACT`. These
override a workflow profile variable of the same name. A deploy script that
checks out `ARMADA_DEPLOYMENT_SOURCE_COMMIT` ships the pinned commit even when
the branch has moved. A Pending deploy or rollback Check is reused only when it
names the same commit.

When the ref has moved since the deployment pinned it, execution still ships
the pinned commit, and `ValidationSummary` names the commit the ref names now.

### 4.2 Required Check evidence

An environment's `RequiredCheckTypes` lists the Check types that must have
passed at the candidate commit, for example `Build` and `UnitTest`. Deploy,
rollback, smoke test, health check and the verification types measure a
running target and cannot be required. The default is an empty list: the
environment requires no evidence.

Each required type is decided on its own, and a passing Build never stands in
for a UnitTest. A record counts for a type only when it measured the vessel at
exactly the full candidate commit. A record that Armada ran must have run the
workflow profile's own command for that type, so a filtered run or a command
override is not proof. A record imported from an external system counts as
that system's verdict. Of the records that count, the newest Passed or Failed
record decides, so a failure newer than a pass withdraws the proof. A
Canceled, Pending or Running record neither proves nor disproves the type.

The evidence is evaluated at approval and again at execution. A refusal names
each type and why it is not proven, with the prefix
`deployment_validation_missing`, `deployment_validation_failed` or
`deployment_source_unpinned`. The deciding Passed records are stored in
`ValidationCheckRunIds`, and the verdict in `ValidationSummary`. A refused
approval leaves the deployment pending, so new proof can follow. A deployment
that executes without approval and is refused is marked Failed with the
refusal as its summary.

### 4.3 Approval names the candidate

If the environment requires approval, the deployment waits for
`approve_deployment`. Approval is an outward action. The operator must review
the target, the pinned commit, the artifact, the profile, and the required
Checks before approval.

Pass the reviewed candidate with the approval: `expectedSourceCommit` (the
full commit or a prefix of at least 7 characters) and
`expectedArtifactIdentity` over MCP, or `SourceCommit` and `ArtifactIdentity`
in the REST approval body. Approval refuses with
`deployment_candidate_changed` when the deployment names a different
candidate. An approval records `ApprovedSourceCommit` and
`ApprovedArtifactIdentity`; execution refuses with
`deployment_approval_mismatch` when they differ from the deployment's
candidate, and with `deployment_approval_missing` when a required approval is
absent.

Use `update_deployment` to correct deployment metadata before execution.
`armada_update_deployment` is a compatibility alias.

## 5. Verify A Deployment

Use `verify_deployment` to run the configured verification path. Review:

- deploy command result;
- smoke test;
- health check;
- deployment verification;
- monitoring window and summary;
- linked incidents.

Deployment actions use matching Pending deployment Checks when they exist. If
no matching Check exists, Armada creates one so the evidence is durable.

A successful deploy command does not prove that the rollout is healthy. The
verification command or an equivalent state query must prove the result.

## 6. Roll Back

Use `rollback_deployment` on the existing deployment record. Do not create a
second deployment only to represent the rollback.

Before rollback:

1. Confirm the exact deployment and environment.
2. Read the failure evidence.
3. Confirm the rollback source or procedure.
4. Open or update the related incident.

After rollback:

1. Read rollback and rollback-verification Checks.
2. Confirm the deployment state.
3. Confirm the actual target state.
4. Link the evidence to the incident and objective.

### 6.1 Retain local Docker images before a rebuild

For a local Docker deployment, run
`scripts/common/rebuild-local-image.sh` before rebuilding a mutable image tag.
Give it the running container name, the mutable image tag, the Dockerfile, and
the build context. It inspects the running container and the existing tag,
creates two unique dated retention tags, and starts the local build only after
both tags exist. Read and record the printed retention tags with the deployment
evidence.

The helper fails closed when the container is absent or stopped, the mutable
tag cannot be inspected, a retention tag already exists, or either tag command
fails. It never pushes an image. If the build fails, the two retention tags
remain available for rollback. Use the exact retained tag recorded by the
helper; do not rebuild or overwrite it.

After a successful build the helper keeps the newest three distinct images of
the repository (`ARMADA_RETAINED_KEEP` changes the count) plus the image the
container runs and the image just built. From every older image it removes each
tag matching `ARMADA_PRUNE_TAG_PATTERNS` (default `armada-retained-*
rollback-before-* candidate-* superseded-* refused-*`), prints each removed tag,
and then prunes dangling layers. The mutable tag is never removed. Docker keeps
an image that a container still uses or that an unmatched tag names. A tag it
cannot remove is reported as a warning and does not fail the build.

## 7. Incidents

Use `armada_create_incident` when a delivery failure has operational impact.
Use `armada_get_incident` and `armada_update_incident` during diagnosis and
recovery.

Record:

- impact;
- affected environment, deployment, release, vessel, voyage, and mission;
- failing Check;
- root cause when known;
- recovery and rollback evidence;
- follow-up objective;
- postmortem.

Use `armada_close_incident` only when linked evidence supports closure. Do not
close an incident only because a captain or operator says that the work is
done. The close requires `rootCause`: the cause you determined. The text the
incident was opened with (`OpenedReason`, often a copied failure reason) is
refused as `incident_root_cause_unchanged`, and an empty cause as
`incident_root_cause_required`. The accepted cause is stored with its author
and time.

When incident lifecycle automation is enabled, newer passing Checks,
successful rescue missions, shipped releases, verified deployments, and
completed rollbacks can mitigate or close linked incidents after the quiet
period. A newer matching failure reopens a mitigated incident and raises its
severity to at least High. Re-observing a failure on an incident that is already
open records the new failure evidence and keeps the incident's severity: the
severity set at creation or by an operator is preserved, and the sweep leaves
the incident alone once the evidence is already recorded. Such a close sets
`ClosedAutomatically` and leaves `RootCause` unset when it holds only the
opened text; `OpenedReason` keeps that text.

A cancelled rescue does not resolve the failure it was dispatched for. When the
latest rescue of a failed mission is cancelled, the incident keeps its status:
an Open incident stays Open, and a Mitigated incident stays as the operator set
it. The lifecycle adds one recovery note that names the rescue and quotes its
cancel reason, which names who cancelled it when that is known. An operator
voyage cancel records `Voyage cancelled by operator.` When the failed mission
itself is cancelled, or its own voyage is cancelled, the incident is superseded
and closes.

## 8. Runbooks

Use `list_runbooks` and `get_runbook` to inspect the approved procedure. Use
`start_runbook_execution` when the procedure runs. Record completed step IDs,
notes, and status with `update_runbook_execution`. Read the final evidence with
`get_runbook_execution`. Use `list_runbook_executions` before you start a
duplicate execution.

Use `create_runbook` and `update_runbook` to manage procedures. Use
`delete_runbook` and `delete_runbook_execution` only with explicit destructive
authority.

Good runbook subjects include:

- release readiness;
- deployment approval;
- deployment verification;
- rollback;
- migration;
- incident response;
- Admiral deployment and recovery.

The execution record must show what ran and what result it produced.

The server-owned mission-recovery runbook is part of autonomous recovery. Do
not replace it with a general human checklist.

## 9. Checks And Automatic Resolution

Use `get_check_run` to read one Check. Use `run_check` for initial execution and
`retry_check_run` for a real rerun. Use `resolve_check` only for valid evidence
that was produced outside Armada.

Every Check gate (the Judge gate, voyage completion and the manual completion
proof) reads only the latest record of each Check that is not Canceled. A Check
is its voyage, mission, type, workflow profile, executed command and commit. The
label is a display name only, so a `run_check` re-run of the same command at the
same commit supersedes an earlier failed record under any label. An abbreviated
commit hash and the full hash name the same commit. A run of a different command,
such as a filtered test run, is a different Check and does not clear the full
command's failure.

On a voyage that splits into lanes (two or more stages depend on one stage),
each Judge reads only the records of its own lane, a record is stale only
against its own lane's work, and a done lane with no record of an armed Check
type gets one stamped at its own commit. A record that spans several lanes
decides none of them. See [Check rules on a split voyage](armada-ops.md#check-rules-on-a-split-voyage).

These three return a BOUNDED view: status, exit code, parsed test and coverage
totals, artifacts, the last lines of output, and `testOutcomes`: every failing
test with its first exception line and the skipped tests, read from the whole
log rather than the tail. Failing tests are read with the built-in matchers plus
the `failedTestLinePattern` of the workflow profile the check run recorded. They do not return the whole
command log, which for a real vessel runs to megabytes and overruns the tool
output limit. Pass `includeOutput=true` to `get_check_run` for the complete
record, or `outputTailLines` to widen the tail. A truncated view states the
full log's size and names the call that fetches it, so nothing is withheld
silently. The REST check endpoints are unchanged and still return the full
record.

Test summaries prefer a readable TRX, JUnit or NUnit result artifact over console
counts. Local executions use result and coverage files only when their collected
write time is at or after the stored command start time. Files with an older or
unknown write time remain in the artifact list but do not describe the current
execution. If no eligible result parses, the summary uses console output. Invalid
or outside-root artifact paths are ignored. External imports retain their supplied
summary contract.

Console parsing reads per-test Jest and Vitest summaries, including skipped and
todo counts, the final valid watch summary, and the first elapsed-time value on
the final Time or Duration line. Suite and file totals are not test totals.
Windows line endings are supported. A .NET run aggregates counts from every
project summary and uses the longest project duration because projects can run
in parallel. Pytest uses its final summary line.

Expensive Checks, definition-of-done gate commands and merge-queue test runs
share host-wide command slots. `definitionOfDone.hostCommandSlots` in
`settings.json` sets how many slots there are: default 1, clamped to 1-8. With
the default, the host runs one full build or test suite at a time, so a Check
never runs concurrently with a gate command or a merge-queue test run. Slots
are granted in request order. A Check or merge-queue test holds a slot for one
command. A gate takes a slot at its first command and holds it until it ends
(its build, its unit tests, each consumer build and test), so a Check that
asked before the gate began runs first, and a Check that asks while a gate runs
waits for the rest of the gate instead of splitting it. The count is applied
at startup and on every settings reload. A raised count grants waiting
requests at once; a lowered count takes no slot from a running command and
grants nothing new until the holders drop below it.

A free slot is granted only while the host has enough free memory.
`definitionOfDone.hostCommandMemoryFloorMb` sets the floor in megabytes:
default 6144, clamped to 0-1048576, applied at startup and on every settings
reload. Set it to 0 to turn the check off. Armada reads `MemAvailable` from
`/proc/meminfo`; on a host without that file, or when the value cannot be read,
the reading is unknown and never holds a command. While the next request is
held only for memory, Armada reads memory again every 5 seconds. The held
request keeps its place, so no later request runs ahead of it. Memory used by
processes outside Armada's commands can stay below the floor, so when no slot
is held and the next request has been held for memory for 15 minutes, it runs
anyway; its holder entry is marked `GrantedUnderMemoryFloor` and the
`UnderFloorGrants` count rises. The slot snapshot (`HostSlot` in
`armada_dispatch_hold`) carries `MemoryFloorMb`, the last `AvailableMemoryMb`
reading, `HeldForMemory` and `MemoryWaitReason`, for example
`waiting for free memory: 4,100 MB available, floor 6,144 MB`.

A waiting request has no process, so the automatic Check runner reports every
wait on each heartbeat sweep. It reports four waits: the slot held for free
memory (`check.memory_wait_*`), every slot held while requests wait
(`check.slot_busy_*`), every place of the runner holding a started Check
while more Checks are pending (`check.runner_full_*`), and Build or UnitTest
Checks held back behind a running definition-of-done gate
(`check.gate_overlap_*`). Each wait records a
`_started` event when it begins, a `_continuing` event every
`definitionOfDone.hostSlotWaitReportMinutes` (default 5, clamped to 1-60) while
it lasts, and an `_ended` event with its length. Each event names the memory
reading and floor, every slot holder and since when it holds its slot, and every
waiting request in order. The setting applies on reload.

Raising the count above 1 risks false failures. Concurrent full suites contend
for the same CPU, memory and disk, and on a host without the capacity for them
the contention shows as failures that look like broken code, most often a burst
of tests classified `Timeout` that pass when the command runs alone. Raise it
only on a host that can run that many suites inside
`definitionOfDone.commandTimeoutSeconds`.

`run_check` waits at most two minutes. A Check still waiting for a slot, or
still running, comes back unfinished and keeps running; read it again with
`get_check_run`. Every unfinished Check view carries `SlotWait`: its state
(`Queued`, `WaitingForSlot` or `Running`), its place in line, every request that
holds a slot (in grant order) and since when the earliest holder has held it,
`MemoryWaitReason` when free memory holds the queue, `QueueReason` when the Check
has not asked for a slot because every place of the automatic Check runner holds
a started Check, and guidance that the wait is an environment condition,
not a code defect or an owner question. A Judge PASS given while a Check is
unresolved is held until the Check passes at the reviewed commit, so a stage
never needs to fail or block because a Check has not finished. A Judge
NEEDS_REVISION given while a Check is unresolved is not recorded either: it
waits in its own hold, and once the Checks resolve the verdict is decided by
what they say. A Check that failed at the reviewed commit proves the work is
broken, so the held NEEDS_REVISION is recorded and a revision Worker runs, not
a Judge re-run on the same commit. A Check that passed (or no Check gates the
verdict) re-runs the Judge once with the Checks' result, because the revision
may have been asked only because the Checks were pending; the wait budget
ending re-runs it too. The re-run happens once per mission and spends no
recovery budget; a NEEDS_REVISION after it is recorded as usual. A Judge held for
its Checks, by either hold, is not decided by them when its reviewed commit is
already on `origin/<default branch>`, or a commit there carries its exact
`Armada-Mission-Id` trailer (the proof the landing sweep reads). The Judge then
completes without a re-run or a landing, `mission.check_hold_decided` records
`delivered`, and the voyage ends by its ordinary completion rule. Every Judge
run a mission goes through keeps its output and verdict: an in-place re-run
preserves the run it replaces, so the persisted mission output read path shows
every run and the first run's requested revisions, never only the last. The automatic
Check runner logs how many started Checks run and how many wait for a slot,
what holds each slot, and how many of the slots are in use when there is more
than one.

The heartbeat runs eligible non-deployment Checks automatically, up to three per
sweep: a voyage-armed Check once a stage has committed work to a branch and
no stage that can still commit remains (a Pending, Assigned or InProgress stage,
or a Testing or Review stage with no completion time, since a running captain
reports those as progress, other than the Judge, a persona whose normal result is an empty diff, or a
read-only mission), so the Check measures the commit the Judge reviews instead
of an intermediate one a later stage replaces (on a split voyage, only the
stages of the record's own lane count); a mission-linked Check after its mission completes, a release-linked Check when
the release is ready, and an unlinked Check when its vessel is idle. A Pending
Check of a voyage that ended without completing is cancelled with its reason
(`voyage_cancelled` or `voyage_failed`), which the discard writes into the
Check's summary and output and records as a `check.discarded` event.
Cancellation, execution and source stamping re-read the current Check under a
shared state lock. A discard cancels a Check only while it remains Pending; a
started Check is left intact. A Check that an operator's `run_check` or
`retry_check_run` is executing is never discarded and never run by the
heartbeat: it runs to completion even when its voyage has ended, because an
operator re-runs a failed voyage's Check to prove a commit before landing it. A source stamp applies only before the Check claims
the host slot or begins source preparation, so a stale heartbeat cannot retag
work already in progress. A Build or UnitTest Check makes its isolated
checkout before it waits for the host slot. The disk lifecycle sweep keeps the
checkout of every Check that has not finished, and a Check that holds the slot
and finds its checkout gone makes it again at the same source; when that fails,
the Check fails with a reason that names the removed checkout.

A definition-of-done gate runs the same full build and unit-test commands as the
Build and UnitTest Checks armed for its voyage. A captain that runs that suite
by hand takes the same per-vessel and host-wide slots first through
`armada_suite_lease` (`docs/MCP_API.md`). While a gate of one of the
voyage's missions (or of the Check's own mission) runs on the Check's vessel, the
heartbeat does not start such a Check: it waits, its `SlotWait.QueueReason` names
the gate, and the wait is reported as `check.gate_overlap_*`. A gate that passes
as a whole records each command that passed after a full run, in a worktree
with no uncommitted tracked change and the same HEAD before and after. A
heartbeat Check of a voyage or mission whose commit, configured command and
environment variables match such a record passes without running the command,
and its summary names the gate's mission. A failed, cancelled or timed-out gate
records nothing, and a pass from the gate's isolated re-run of failing classes is
not a full-suite pass. A UnitTest or IntegrationTest Check whose full run fails on
at most three named tests re-runs them alone under the same rule as the gate
(`docs/OPERATIONAL_ASSETS.md`, Workflow Profiles), in the same checkout and under
the host command slot; it passes only when that re-run passes, and its summary
then reads `passed on isolated re-run` with the full run's failure. The records live in the admiral's memory, so after a
restart the Check runs its command. An operator's `run_check` or
`retry_check_run` always runs the command.

A passing gate's mission evaluation keeps a redacted, digest-backed capture of
its last successful full vessel unit-test command, or its build command when no
unit-test command is configured. The capture header names the command, including
a full post-test-step retry. Successful consumer verification preserves that
vessel capture; a consumer failure keeps its own failure output. An isolated
subset retry does not supply a full-suite pass capture. Read the referenced
mission's evaluation and retrieve its capture through `armada_get_mission_log`
instead of rerunning the suite to recover its footer. Capture availability means
the file was written; the runner's truncation and incomplete-output flags still
show whether the retained output is complete. Existing mission access, redaction,
byte bounds and log retention apply. Passing records keep failure fields null.

Cancelling a voyage stops its queued and running definition-of-done evaluations,
including evaluations for missions already marked `WorkProduced`. Those mission
outcomes remain available for recovery. A late evaluation reads the durable
mission and voyage cancellation state after registration, before waiting or
running commands. Failed captain recall leaves the voyage and its gate active.

Direct landing also reads stored mission and voyage cancellation after
integration validation, before it pushes or advances the target ref. Operator
mission and voyage cancellation share that publication boundary in one Admiral
process. The suite and captain recall run outside it. When cancellation is
durable first, publication is refused, the source branch stays available, and
late landing work emits no completion or landing-failure event. This does not
stop an integration Check already running or undo a ref that advanced before
cancellation. The merge-queue path has separate publication control.

A Check command has a 30-minute default. An explicit workflow profile
`GateCommandTimeoutSeconds` also sets the shared execution budget for a Build or
UnitTest Check: its full command and isolated retry must fit that budget together.
Other Check types keep their existing per-command limit. A timeout is a failure.
The budget is fixed for the run. Local commands use a monotonic clock; Harbor
commands use the authenticated runner duration and the remaining whole seconds
allowed by its protocol. Missing runner duration cannot pass a declared shared
budget. Provisioning and output retrieval do not spend command time. Initial
and retry host-slot waits spend no execution budget; a retry gets only the remainder after
it acquires its slot. A positive service-constructor timeout override remains
authoritative for callers that provide one.

An explicit profile value also bounds a logical DoD build or unit-test family
across its full command, post-test-step retry, flake retry, isolated retry, and
host-load recovery attempt. One evaluation keeps the same ledger when it replaces
its host slot. Each vessel or consumer has independent build and test budgets;
passing one does not fund another. An exhausted declared budget fails without a
fresh retry, even when host load or continued output would otherwise allow one.
Admission and load waits do not replenish or spend the execution budget. Native
process time and authenticated Harbor duration spend it; a remote remainder
smaller than one second cannot start a new command. The retained output records
cumulative command time and the declared budget. Without an explicit profile
value, DoD keeps its existing global per-command timeout and recovery policy.
A reused full DoD pass must have the same resolved declared execution budget
as the Check. An undeclared pass cannot fund a declared budget, and a changed
budget or caller timeout override requires fresh verification. Both undeclared
policies retain their existing exact-source, command, and environment reuse.

On every path that ends a started Check (exit, timeout, error, or the cancel at an
Admiral restart), `DurationMs` is `CompletedUtc - StartedUtc`. Completion follows
isolated retry work and checkout cleanup, so this duration includes a retry's
slot wait and other work after the first command. The output's final
`Check execution timing` line records active command milliseconds and retry-slot
wait milliseconds separately. `QueueDurationMs` still describes creation to the
first command start. These are different measures; do not read the wall duration
as pure test time. Deployment deploy, verify, and rollback Checks stay under the
deployment action so the deployment record remains authoritative.

While the dispatch hold is engaged, the heartbeat runs eligible Checks for
work on active voyages. These Checks let existing voyages pass their Check gate
and drain, including work produced by a mission that was already running when
the hold began. Checks for idle vessels, completed voyages and unrelated work
stay Pending until the hold clears. One `check.auto_deferred_dispatch_hold`
event per hold engagement names the holder and reason. New mission dispatches
and autonomous rescues remain blocked. `run_check` and `retry_check_run` still
run a Check on an operator's request.

## 10. Minimum Closeout

Before you report delivery completion:

- the release links the correct work and source commit;
- required Checks passed;
- the deployment targets the correct environment;
- the deployment names the intended `SourceCommit` and `ArtifactIdentity`;
- approval exists when required, and names the same candidate;
- verification proves the live state;
- rollback evidence exists if rollback occurred;
- incidents match their evidence;
- follow-up work has a separate objective or backlog item;
- the parent objective contains the final links and verified outcome.

## 11. Current Boundaries

Armada's internal records are authoritative for its own workflow. External CI,
provider review, and deployment systems can require separate evidence links.
Do not report external state as verified until Armada has ingested it or the
operator has checked it directly.
