# Armada Operator Guide

This guide is the canonical operating procedure for Armada. It describes how
an orchestrator creates, monitors, verifies, lands, and closes work. It also
lists every MCP tool that the server registers.

Use [MCP_API.md](MCP_API.md) for transport and schema discovery. Use
[DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
detail. Use [MERGING.md](MERGING.md), [PIPELINES.md](PIPELINES.md), and
[SCHEDULING.md](SCHEDULING.md) for subsystem detail. Use
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for playbooks, runbooks,
workflow profiles, environments, personas, pipelines, and their links.

## 1. Source Of Truth

Armada records are the source of truth for work and delivery state.

| Record | Owns |
| --- | --- |
| Objective or backlog item | Scope, acceptance criteria, priority, constraints, and deferred work |
| Planning or refinement session | Decisions made while the scope is not ready for dispatch |
| Voyage and mission | Work assignment and captain execution |
| Check | Command evidence and gates |
| Merge entry | Review, integration, test, and landing state |
| Release | A candidate or shipped unit |
| Deployment | Rollout, approval, verification, and rollback |
| Incident | Failure impact, diagnosis, mitigation, and closure evidence |
| Runbook execution | Evidence that a repeatable procedure ran |
| Event, signal, and request history | Timeline and communication evidence |

Do not use mission prose as a replacement for these records. Do not treat a
captain report as proof. Run the command or query that proves the result.

## 2. Available Features And Active Policy

The repository contains features that an operator can disable. Documentation
of a feature does not mean that a deployment enables it.

Check these settings before you depend on the related workflow:

| Setting or record | Effect |
| --- | --- |
| `codeIndex.enabled` | Enables code search, graph search, and context packs. |
| `learnedFactsEnabled` | Enables learned-playbook injection and reflection workflows. |
| `seedDockRuntimeMcpConfig` | Gives supported captains the local Armada MCP URL through runtime-appropriate dock or launch configuration. Default: enabled. |
| `autonomousRecovery.enabled` | Enables bounded server-side mission recovery. |
| `incidentLifecycle.enabled` | Enables evidence-driven incident transitions. |
| `remoteTrigger.enabled`, mode, and `agentWake.deliveryMode` | Enables AgentWake process and/or signal delivery. |
| Objective `AutoDispatchEnabled` and scheduler state | Enables autonomous objective dispatch. |
| Vessel or voyage landing mode | Selects `LocalMerge`, `PullRequest`, `MergeQueue`, or `None`. |
| Vessel default pipeline | Selects the normal persona path. |
| Workflow profile | Defines the commands that Checks and delivery operations run. |

When a feature is off, use the explicit fallback. For example, search the
checkout directly when code indexing is off. Do not call disabled tools in a
loop.

## 3. Connection And Discovery

The Admiral exposes stateless Streamable HTTP MCP at `/mcp`. `/rpc` is a
compatibility alias. An SSH stdio bridge can forward a local MCP client to a
loopback-bound remote Admiral. The bridge must connect to the running Admiral.
It must not start a second embedded Admiral process.

Start each operator session with:

1. Call `armada_status`.
2. Call `armada_enumerate` with small pages for active voyages, missions,
   captains, merge entries, incidents, objectives, and checks.
3. Call `armada_drain_audit_queue` before new dispatches.
4. Read each relevant open objective in full.
5. Check incidents and the merge queue before you create more work.
6. Check `armada_unlanded_branches` when prior work can exist outside the
   normal landing path.
7. Call `armada_list_papercuts` to see the friction that recent captains
   reported. Section 4.9 gives the triage rules.

MCP `tools/list` is paginated. Follow `nextCursor` until it is absent. The
normal built-in catalog fits on one 500-tool page. Pagination remains active
for larger extension catalogs. A client that ignores `nextCursor` can hide
valid tools.

Supported captains receive the local MCP URL through runtime-appropriate dock
and launch configuration. Claude strict mode and Codex receive explicit launch
arguments because a project file alone is not sufficient for those paths. The
catalog also contains dispatch,
administration, deployment, restore, purge, and server-control actions. Those
tools stay outside normal captain scope; the operator owns them unless the
mission explicitly assigns the action. Set `seedDockRuntimeMcpConfig=false`
only when the deployment intentionally removes all Armada tools from captains.

`armada_enumerate` supports these entity types:

`fleets`, `vessels`, `captains`, `missions`, `voyages`, `docks`, `signals`,
`events`, `merge_queue`, `personas`, `prompt_templates`, `pipelines`,
`playbooks`, `objectives`, `incidents`, `checks`, `releases`, and
`deployments`.

Use `pageSize` from 10 to 25 unless a larger page is necessary. Large text
fields are excluded by default. Request `includeDescription`, `includeContext`,
`includeTestOutput`, `includePayload`, or `includeMessage` only when needed.

## 4. Standard Workflow

### 4.1 Capture The Work

For non-trivial work, find or create an objective before dispatch.

1. Search with `list_objectives`, `list_backlog`, or
   `armada_enumerate(entityType: "objectives")`.
2. Read the selected record with `get_objective` or `get_backlog_item`.
3. Add clear acceptance criteria and verification requirements.
4. Put deferred work in a separate objective or backlog item. Do not bury it
   in closed-record prose.

Use backlog refinement when intent is still unclear and repository context is
not yet needed. Use a planning session when the work is tied to a vessel and
must become dispatch-ready.

### 4.2 Inspect Before Dispatch

Read the target repository rules and inspect the current code and git history.
Confirm that the requested work is not already present. Confirm the vessel,
pipeline, model tier, landing mode, protected paths, and workflow profile.

Read the coordination board before you dispatch or touch incidents
(`armada_coordination_read`). Post a claim note before you start
(`armada_coordination_post`) so a concurrent operator session does not dispatch
the same work or rescue the same incident twice. The board is advisory context;
it never injects into captain briefs. See 8.9 for the tool list.

If code indexing is enabled, use `armada_index_status` before index-dependent
work. If it is disabled, use checkout search and set `codeContextMode` to
`off`.

### 4.3 Select Mission Shape

Use mission mode `Implementation` for work that must produce a commit. Use
`Audit` or `Research` for report-only work. Read-only modes do not require a
commit and must not receive implementation-only instructions.

Use the vessel's configured pipeline unless the approved work calls for a
different existing pipeline. Use the full configured persona path. Do not
remove review stages only to make a voyage faster.

Dispatch with `preferredModel: "low"`, `"mid"`, or `"high"`. Do not put a
concrete provider model in an ordinary mission brief.

### 4.4 Dispatch

Use `armada_dispatch` for a voyage. Put all ordered missions for one vessel in
one request. Use aliases and dependency aliases when order matters.

Include:

- `objectiveId` for durable work;
- the vessel ID;
- the pipeline when the vessel default is not correct;
- one mission title and complete description per unit of work;
- mission mode;
- model tier;
- exact scope and exclusions;
- required verification;
- only the prestaged files that downstream stages can read and need.

`armada_dispatch` is durable-first. A successful response means that the
voyage and mission rows exist. Assignment, dock provisioning, and captain
launch continue asynchronously. Save the voyage ID. Do not redispatch only
because the first status call shows `Pending`.

Long operations can return an accepted job. Poll `armada_job_status` with the
returned job ID. Background jobs are reaped on a health-loop cadence: a job
left Accepted or Running past the stale threshold (a worker that hung or died)
is failed automatically so it reaches a terminal status instead of reading as
in-flight forever. Poll a job past its expected runtime; if it was reaped, the
status is `Failed` with a reason naming the stale window.

#### Controlled parallel dispatch

Armada can run several voyages at once. The scheduler's
`maxConcurrentVoyages` is a fleet-wide ceiling, not a requirement to dispatch
one voyage at a time. For normal autonomous work, keep two or three independent
writable lanes ready when the fleet has enough safe work. Keep
`maxConcurrentVoyagesPerVessel=1` unless one vessel is proven safe for parallel
docks and suites.

Before a lead auto-enables a lane, confirm:

- a different active voyage does not own the same files or objective;
- no other active voyage uses the same vessel unless its suite and worktree
  policy are proven safe for concurrent docks;
- captains will not run conflicting dock-side suites or move a shared sibling;
- the objective is not hardware-dependent or operator-only;
- the brief premise is true at the target tip; and
- required ordering is in `BlockedByObjectiveIds`.

Prefer parallel voyages on different vessels. Treat repositories as one lane
when either suite builds or tests the other through a sibling-project reference.
Every dock is provisioned as `docks/<Vessel>/<mission>/<Vessel>` with its
declared siblings beside that checkout (`docks/<Vessel>/<mission>/<Sibling>`), so
a sibling is pinned at provisioning and is never shared between docks. A dock
that re-provisions the same mission (a retry) reuses its own sibling; when that
sibling is behind the branch tip the admiral logs both commits and records
`dock.sibling_stale`.

Declare that relation with `buildParticipant: true` on the sibling entry of the
vessel that builds the other; the scheduler then applies the per-vessel ceiling
to every vessel in the lane and reports a refused dispatch as
`lane_busy:<vesselA>+<vesselB>`. A read-only sibling (a decompiled or
extraction-artifact tree) stays `false` and forms no lane.
Armada-owned Checks serialize on the host interlock, but a captain can still run
a suite directly in its dock.
Use bounded read-only helpers to prepare future lanes; use captains and voyages
for repository writes. When throughput looks low, inspect both the scheduler
ceiling and how many objectives have `AutoDispatchEnabled=true`.

#### Start ref: continue from an accepted tip

An objective can carry `startFromRef` (a branch, tag or commit in the vessel
repository, set through `create_objective` / `update_objective`). When the
scheduler or an operator dispatches a voyage for it, the FIRST stage's branch is
cut from that ref instead of the vessel default branch; later stages continue
the branch as usual. Use it to re-dispatch a row from a `recover/<name>-<sha>`
ref that holds an accepted Worker or TestEngineer tip, so the new voyage does
not rebuild what a Judge already accepted.

The ref is resolved twice, and both failures are loud. At dispatch, a ref that
does not resolve refuses the whole dispatch before any voyage row exists; the
scheduler reports it as `start_from_ref_missing` (never `dispatch_error`) and
writes an `objective_scheduler.start_from_ref_missing` event. At assignment,
the branch is cut at the ref in the bare repository before the dock is
provisioned; if the ref has gone by then, the mission fails with
`start_from_ref_missing` and no captain is launched. There is no fallback to
the default branch: a captain working on the wrong base reads as a captain
defect two stages later. A dependent mission ignores the field, because it
continues its predecessor's branch.

#### Model pinning

`preferredModel` normally takes a complexity tier (`mid` or `high`; the
legacy `low` value maps to `mid`) and Armada picks the best-fit available
captain. It also accepts a literal
model name (for example `claude-opus-5`, `gpt-5.6-sol`,
`opencode-go/glm-5.2`): the dispatcher first filters idle
captains by an exact case-insensitive model match, and only then falls back to
tier routing. A pin is therefore best-effort, not a guarantee -- when no idle
captain carries that exact model, Armada classifies the model into a tier and
picks a peer. Verify the assignment after dispatch when the pin matters;
benching is never required to pin a model.

#### Read-only dispatches stay single-stage

When every mission in a dispatch is `Audit` or `Research` mode and no pipeline
is requested, the vessel or fleet default pipeline is ignored and the voyage
dispatches single-stage. A read-only probe must not silently inherit a
multi-stage default (a four-mission diagnostic once expanded to sixteen
missions). An explicitly requested pipeline is always honored.

### Stage handoff is verified, not assumed

A downstream pipeline stage inherits its predecessor's branch. Inheriting a
branch NAME is not the same as inheriting its commit: a local ref can predate
the upstream stage's push, and the resulting worktree looks correct while
missing the work.

Armada now proves the containment before the captain starts. A stage whose
checkout demonstrably lacks the upstream commit fails immediately with
`stage_base_missing`, which names the commit, the branch, and the upstream
mission, and says plainly that the fault is in provisioning rather than in the
stage's own work. Read that before reading the diff - the previous version of
this failure presented as a Worker that could not compile its own change, and
the diagnosis started in exactly the wrong place.

What is NOT failed, deliberately:

| Condition | Verdict |
| --- | --- |
| Mission has no upstream stage | Not applicable |
| Dependency is in another vessel | Not applicable - commits are not shared across repositories |
| Upstream produced no commit (Audit, Research) | Unverified, stage proceeds, fact logged |
| Ancestry probe could not answer | Unverified, stage proceeds, fact logged |

A base that could not be proved is never reported as one that was. The git
ancestry probe answers true, false, or UNKNOWN, and its default for any
implementation that does not consult a real repository is unknown - so a stub
can never manufacture a passing verification.

### A quiet-host gate must enumerate TERMINAL states, not guess at active ones

Before any action that interrupts running work - restarting the Admiral,
rebuilding its image, reclaiming docks - operators check whether the host is
busy. The obvious query is wrong.

A voyage is not only `InProgress`. It is also `Open`, and a gate written as
`where status = 'InProgress'` reports a quiet host while `Open` voyages are
running captains. That is a false quiet, and acting on it destroys work.

Enumerate what is FINISHED and treat everything else as active:

```sql
select count(*) from voyages
where status not in ('Complete','Failed','Cancelled');
```

Written that way, a status added to the vocabulary later reads as active and
the gate fails safe. Written the other way, a new status is invisible and the
gate fails silently open.

Voyages alone are not enough either. Check executing missions and captain
processes too, because a mission can outlive its voyage's terminal status:

```sql
select count(*) from missions where status in ('InProgress','Assigned','Testing');
```

The same reasoning applies to any "is it safe to act?" predicate in Armada.
State the finished set, not the busy set.

### 4.5 Monitor

Dispatch is the start of the operator loop.

1. Poll `armada_voyage_status` in summary mode.
2. Read `armada_mission_status` for the active or failed stage.
3. Read mission and captain logs when progress is unclear.
4. Use `armada_captain_diagnostics` before deep process inspection.
5. Poll incidents and Checks on the same cadence.
6. Use `armada_nudge_voyage` or `armada_send_signal` only for live work that
   needs missing context.
7. Inside a blocking monitor loop, watch for the `[ARMADA WAKE]` banner. When
   your client sends the participant header (below), Armada appends pending
   directed messages to ANY tool result, so a status poll delivers your mail.
   Pause and address those first, then acknowledge each with
   `armada_mark_signal_read`. Without the header, heartbeat or read the board
   with your participantKey between iterations instead; those two tools are the
   only others that carry `UnreadWakes`.
8. Do not steer a terminal mission. Use restart, recovery, or a new mission.

A quiet captain is not proof of a stall. Compare the mission state, process
ID, dock status, log activity, and elapsed time.

### 4.6 Verify With Checks

Create Pending Checks when the objective or voyage is created. Build and unit
test are the minimum for code changes. Add the vessel-profile gates that the
change needs.

Armada runs at most ONE expensive command on a host at a time. Two full build
or test suites at once produce a burst of simultaneous sub-millisecond failures
across unrelated test classes, usually classified Timeout, which reads exactly
like a real regression - and the same command passes alone. A host-wide
interlock now serializes all four callers that run a vessel's build and test
commands: a check run, a pending check executed at the Judge stage, a
definition-of-done gate, and a merge-queue test run.

Two consequences for an operator:

- A check submitted while a gate is running does not fail and does not race. It
  QUEUES, so it can take much longer to return than the command itself takes.
  A slow check is not necessarily a slow suite.
- The contended resource is the host, not the vessel, so checks on DIFFERENT
  vessels serialize against each other too.

The interlock covers commands Armada starts. It cannot see a captain running a
suite by hand inside its own dock, because that is a separate process, so a
dock-side suite can still overlap a gate.

It equally cannot see an OPERATOR running commands over SSH, and that matters
more than it sounds. A vessel whose profile provisions a sibling shares one
sibling directory across every check: each isolated check sandbox is private,
but they all reach the same parent path, and each run fetches and resets it.
The interlock makes that safe between checks, because only one executes at a
time. It does not make it safe against an operator who runs git against that
same directory while a check is executing - which re-points the sibling under a
running build.

The practical consequence is for DIAGNOSIS, not correctness. Reading a shared
sibling's state after a check has finished does not tell you what that check
compiled against: any later run has already moved it. To establish what a check
actually used, read the check's own output - the restored project paths name the
sibling - rather than inspecting the directory afterwards. And do not run git
against a shared sibling while checks are in flight; an out-of-band `reset
--hard` there is indistinguishable, later, from a stale sibling. When a wrong-value failure appears
under load, an isolated re-run remains the discriminator: a contention flake
passes alone, a genuine mismatch fails alone every time.

Use `run_check` to execute a check. Use `retry_check_run` for a real rerun.
Use `resolve_check` only when valid evidence was produced outside Armada. Do
not use it to hide a failure.

These tools return a bounded summary rather than the whole command log; section
8.7 gives the fields and how to fetch the full output when the tail is not
enough.

A passing suite proves only that the suite passed. It proves a fix only when
the check covers the original symptom. Record before and after evidence when
the task is a defect.

A Judge PASS is backed by the real signal, not by the Judge's own report. The
gate reads every Check attached to the voyage and to the Judge mission:

| Check state | Effect on a Judge PASS |
| --- | --- |
| All green | PASS stands |
| Any `Failed` | PASS is rejected |
| Any `Pending` or `Running` | PASS is held, then re-run in place |
| `Passed` or `Failed` for a commit other than the reviewed tip | Stale: PASS is held exactly as for `Pending`; the executor cancels the record as superseded and arms a fresh one for the tip |
| None attached | PASS is rejected unless the review carries `[JUDGE-CHECK-EXCLUSION]` |
| `Canceled` | Ignored |

A green is a statement about one commit. A voyage-armed Check is stamped at the
FIRST stage that commits, and every later stage commits on top, so by the Judge
the only green record can describe a commit several stages back - in the worst
case a planner commit that never landed. The gate therefore compares each
`Passed` record's commit to the tip the Judge reviewed (the Judge mission's own
commit hash) and treats a mismatch as unresolved. The hold message names both
commits. While the PASS is held, the check executor supersedes the stale record:
it is set `Canceled` with a summary naming its successor, a fresh `Pending`
record of the same type is armed unless a queued, running, or tip-green sibling
already covers it, and a `check.superseded` event is written. The stale record
stays as history of what it measured; nothing is deleted or rewritten. A
`Failed` record for an older commit is stale in the same sense: the reviewed
commit may be the fix for it, so it holds rather than rejects, and the re-armed
record at the tip decides. A record that carries no commit at all is stale
when it is attached to a voyage (it measured the default branch) and is left
alone otherwise.

Two consequences follow, and both have cost real voyages.

Resolve EVERY failed Check, not most of them. When several Checks fail for one
environmental cause, resolving all but one leaves a record that rejects the
PASS hours later, long after the cause is forgotten. The rejection now names
the specific Checks that blocked it, so read the `FailureReason` and confirm no
record remains `Failed` before the Judge stage runs. Resolve an environmental
failure as `Canceled`, not `Passed`: the run genuinely did not pass, and the
reason field is where the evidence belongs.

Dispatch arms the voyage's Build and UnitTest Checks itself. A type is armed
only when the vessel's resolved workflow profile actually defines the command
for it, and a type already attached to the voyage is never armed twice - adding
a second Build beside a failed one would leave a green and a red on the same
voyage, and one failed Check rejects a Judge PASS however many green ones sit
next to it.

The armed Checks are `Pending`, not executed. An armed record becomes eligible
to run as soon as a stage has committed to a branch, and it is stamped with that
branch and commit before it executes, so it measures the work rather than the
vessel's default branch. Arming therefore costs nothing at dispatch and still
satisfies the real-signal gate; executing at dispatch would put a full suite on
the host at the moment the first captain starts work. An armed record reads
`command = echo` with no branch until that stamp - that is the correct armed
state, not a broken stub.

Arming never fails a dispatch. A voyage that exists without its Checks can
still be armed by hand, whereas refusing to dispatch over a Check record would
turn a convenience into an outage. Set `VoyageCheckArming.Enabled` to `false`
to switch it off, or `ArmBuild` / `ArmUnitTest` to control the types.

Operators may still attach further Checks, and must do so for any gate beyond
build and unit test. What changed is the floor: a voyage no longer reaches its
Judge stage carrying nothing, and a re-dispatch after a cancellation no longer
starts bare because the previous voyage's Checks went with it.

The definition-of-done gate also builds the vessels that DECLARE this vessel as
a sibling repository. A producer's own build cannot observe a break it causes
in a consumer, because the consumer is a different repository with a different
compilation: the producer's gate passes, the branch lands, and the break
surfaces on whatever builds next, attributed to that build rather than to the
change that caused it.

The consumer edge is derived, not configured. A vessel declares the
repositories it depends ON, in `SiblingRepos`; the gate reads that same data in
the opposite direction to find who depends on IT. Nothing extra needs to be set
up for a vessel whose consumers already declare it.

What the gate does for each consumer:

| Step | Behavior |
| --- | --- |
| Provision | A scratch root private to this one verification, never a shared sibling path |
| Producer ref | The mission branch, checked out detached |
| Other siblings | Their declared default branches - only the producer is under test |
| Command | The consumer's `BuildCommand`, not its test suite |
| Cleanup | Worktrees removed and the scratch root deleted, pass or fail |

The private scratch root is load-bearing. A shared sibling checkout that another
dock already owns is REUSED rather than re-pointed, so verifying through one
could compile the consumer against some other commit while reporting on this
one - the exact false green the step exists to prevent.

Consumers are built, not tested. A build catches the break that leaves a target
branch red; running every consumer's suite inside every producer gate would
cost more wall time than the gate itself. A consumer break that shows up only
in a test oracle is therefore still found by the consumer's own gate, not by
the producer's.

A consumer that fails to COMPILE fails the producer's gate. A consumer that
cannot be PREPARED - no workflow profile, no `LocalPath`, a worktree that will
not provision - is an infrastructure fault in the verification rather than
evidence about the producer's change, so by default it is logged and the gate
passes. Set `DefinitionOfDone.FailOnConsumerVerificationError` to make those
fail instead. Set `DefinitionOfDone.VerifyDeclaredConsumers` to `false` to
switch the step off entirely.

### 4.7 Review And Land

Read the mission diff and relevant logs. Drain the audit queue and record the
audit verdict when needed. Check the merge entry before processing it.

Use `armada_process_merge_entry` for one reviewed entry. Use
`armada_process_merge_queue` only when the operator intends to start queue
processing. It returns an accepted job and can no-op when a queue run is
already active. Poll the job and merge entry.

Landing behavior comes from the effective landing mode:

| Mode | Result |
| --- | --- |
| `LocalMerge` | Merge into the configured working checkout. Do not push unless separate policy permits it. |
| `PullRequest` | Create or update provider review state. |
| `MergeQueue` | Use the durable integration, test, and landing state machine. |
| `None` | Leave produced work for explicit operator handling. |

Do not infer successful landing from a `Complete` label alone. Verify the
target branch or remote commit that should contain the work.

Merge-queue landings need extra verification, because the entry status can
mislead:

1. `armada_enqueue_merge` moves the source branch into a
   `refs/heads/armada/merge-queue/<id>` ref. If `armada_process_merge_entry`
   later reports the branch was not found, it may mean the landing succeeded
   (only the original name is gone) or that it failed. Verify with
   `git merge-base --is-ancestor <sha> <target>` in the vessel bare repo
   before re-enqueueing, and restore the branch from the mission commit
   hash when it is genuinely gone.
2. Land entries for the same vessel+target one at a time. Concurrent
   processing of the same target collides on the push and both fail with a
   non-fast-forward rejection.
3. After a batch of landings, confirm the pre-batch target tip is still an
   ancestor; a landing rebuilt from an older base can silently drop sibling
   commits that sat at the previous tip. Cherry-pick any dropped commit
   back.
4. After a direct push from the working checkout, sync the vessel bare repo
   (`git fetch origin` then `git update-ref refs/heads/<target>
   refs/remotes/origin/<target>`) so later Checks build the new code.

When a landing retry fails, the mission's `FailureReason` records the
conflicted-file list (`git diff --name-only --diff-filter=U`) so the operator
sees exactly which paths to fix without re-deriving the merge state. Read the
mission's failure reason before deciding the recovery path.

### 4.8 Close The Record Chain

Before the objective becomes complete:

1. Link the final voyage and missions.
2. Link the passing Checks.
3. Link a release and deployment when work shipped.
4. Link incidents and their final evidence.
5. Create a new record for every deferred task.
6. Update the objective summary with the verified outcome.

### 4.9 Sweep The Papercuts

Captains report friction they meet on an `[ARMADA:PAPERCUT]` line: a stale
document, a dead link, a brief that contradicts itself, a missing sibling
repository, a test that fails under load. Armada stores each report as a
`papercut` event with the reporting mission, captain, vessel, and voyage.

Read them on a schedule. A report that nobody reads is worse than no report:
the captain paid to write it and the next captain still pays the same cost.

1. Run `armada_list_papercuts` after a voyage closes, and again in the weekly
   sweep with `sinceHours: 168`.
2. Read the count and the distinct-captain count first. One captain reporting
   a problem is an anecdote. Several captains reporting it is a defect with
   evidence.
3. Route the group by category:

   | Category | Owner |
   | --- | --- |
   | `MissingDoc`, `BrokenLink`, `RepoFriction`, `TestFlake` | Backlog item on that vessel |
   | `EnvSetup` | Dock or workflow-profile fix, then a Check to prove it |
   | `BriefContradiction`, `PlatformBug` | Armada objective, direct-edit only |
   | `ToolFailure` | Read the mission log before you accept it; a captain calling a tool it never received is a `BriefContradiction` |

4. Quote the group in the record you create: the count, the distinct-captain
   count, the sample title, and the sample mission IDs. Those missions are the
   evidence.
5. Keep the promotion manual. A high count is not authority to dispatch.

Two signals need a different response than a repository fix:

- **A `BriefContradiction` group is a captain-quality defect, not a vessel
  defect.** It means the brief asks for something the captain cannot do. Fix
  the instruction module, not the repository.
- **A category that one runtime reports and no other runtime reports** is
  usually about that runtime, not about the vessel. Compare the reports before
  you change vessel code.

Judge missions do not file papercuts. A judge reports what it finds through
its verdict, and splitting review feedback across two surfaces means the
operator reads only one of them.

### 4.10 Campaign Work

A campaign is an opt-in objective tree for one large effort: a root tagged
`campaign:<name>`, lane children per source or area, and slices beneath the
lanes. Plain objectives outside any campaign remain the default; do not force
ordinary work into one.

Operating rules:

1. Claim the slice (`armada_coordination_claim`) before starting; heartbeat
   while working; release when done.
2. Encode wave ordering with BlockedByObjectiveIds, not prose.
3. Attach the campaign's rules playbook (for porting:
   `porting-campaign-rules`) through selectedPlaybooks on dispatch.
4. On landing, link EvidenceLinks - commit SHAs, green Checks - and the
   source-glossary entry the slice extended. No evidence links means not done.
5. Answer "where does this stand" with `armada_campaign_status`, not ten
   enumerations.

### 4.11 Helper Sessions And The Lead Roster

A lead session that runs host-side helper sessions owns their complete
lifecycle. This is separate from the autonomous objective scheduler: the
scheduler selects ready objectives and dispatches captains inside Armada. An
optional lead cycle reviews the inbox, refills campaigns, and delegates narrow
read-only investigations. Armada does not schedule that lead cycle by itself;
an operator starts a fresh cycle directly, through an external scheduler, or by
registering it for AgentWake process delivery.

Use `scripts/autonomy/spawn-helper.sh` for bounded host-side helpers:

```bash
scripts/autonomy/spawn-helper.sh spawn census /tmp/census-task.md /path/to/repo
scripts/autonomy/spawn-helper.sh offer ready /tmp/fallback-task.md armada-lead /path/to/repo
scripts/autonomy/spawn-helper.sh list
scripts/autonomy/spawn-helper.sh kill census
scripts/autonomy/spawn-helper.sh cull
```

The launcher enforces `AUTONOMY_MAX_HELPERS` (default 2), records PIDs and
participant keys under `AUTONOMY_WORKDIR`, and culls sessions older than
`AUTONOMY_HELPER_TIMEOUT_MIN` (default 90). It supports `opencode`, `claude`,
and `codex`; `AUTONOMY_RUNTIME=command` plus `AUTONOMY_COMMAND` is the local
test adapter. Every prompt receives a fixed contract: use the generated
participant key, drain and acknowledge addressed wakes, stay read-only, post
one outcome, release claims, and exit. Run
`scripts/autonomy/test-spawn-helper.sh` after launcher changes.

`offer` mode posts availability to the named lead and gives it a bounded
four-minute reassignment window. The helper checks for directed Wakes at most
every 25 seconds during that window. It then runs the fallback, accepts the
lead's replacement task, or stands down. `list` shows each helper's mode and
lead key. See `docs/autonomy/helper-offer-prompt.md` for the manual-session
equivalent.

Claude helpers run with strict MCP isolation. The launcher therefore writes a
private Armada-only MCP file and passes it with `--mcp-config`. The default URL
is `http://127.0.0.1:7891/mcp`; override it with
`AUTONOMY_ARMADA_MCP_URL`, or supply an existing file through
`AUTONOMY_CLAUDE_MCP_CONFIG`. Strict mode without the explicit file gives the
helper zero Armada tools and makes the board contract impossible.

The helper's working directory is also its file-sandbox boundary. Give it the
narrowest directory that contains all required evidence. Use a common ancestor
when one task must inspect a checkout, a bare repository, or a sibling. Do not
disable the sandbox to repair a bad working-directory choice.

Three sets from the coordination board define the roster: participants (who is
present and when last seen), active claims (who holds work), and their
difference (a participant present without a claim is idle).

Lead duties:

- Hand a live helper work with an addressed note (`toParticipantKey`) naming
  the task, vessel or objective, and constraints. The note always writes a
  Wake signal, so its next heartbeat delivers the full payload.
- Or stand the helper down explicitly ("stand down, nothing available").
  A script-managed helper must then exit; it must not wait in a polling loop.
- Re-check the roster between loop iterations, not only at session start.

Do not register a script-managed helper for AgentWake. That creates two process
owners for one participant key and can duplicate work. Use one model:

- A bounded script-managed helper handles its initial task and any wake already
  waiting at a tool boundary, reports, and exits.
- An AgentWake process owner has no resident process. Put its stable key in
  `remoteTrigger.agentWake.participantKey` when addressed wakes must survive an
  Admiral restart. A transient registration can override that key for a
  controlled probe until the next restart.

One process owns one participant key. OpenCode AgentWake sessions are always
fresh, so the addressed note must contain the complete task and the bootstrap
prompt must tell the session to reconstruct context from the board and durable
memory. The reusable lead prompt is
`docs/autonomy/lead-bootstrap-prompt.md`.

## 5. Recovery And Incident Workflow

When autonomous recovery is enabled, Armada classifies failed missions,
creates or updates an incident, records a recovery runbook execution, and can
dispatch a bounded rescue mission. It does not use generic rescue missions for
landing failures. Authentication, quota, review, protected-path, dependency,
and exhausted-recovery failures remain for operator action.

Use this order for manual diagnosis:

1. Read the complete mission and captain logs.
2. Use `armada_captain_diagnostics`.
3. Compare with a known-good mission on the same runtime.
4. Separate provider, host, repository, test, and model failures.
5. Create or update the incident before repeated intervention.
6. Cancel only the exact mission or voyage that must stop.
7. Restart or retry only after the cause is understood.

Incident closure is evidence-driven. Produce a newer passing check, successful
rescue, shipped release, verified deployment, or completed rollback. Do not
close an incident only because a captain reported success.

### A rescue of a stage inside a voyage re-enters review

A Worker that fails its gate inside a voyage has already cost that voyage its
TestEngineer and Judge: the pipeline cancels them as blocked dependents when the
Worker fails. Its rescue is therefore dispatched as a rescue VOYAGE — the Worker
revision, then a TestEngineer where the vessel pipeline defines one, then a
Judge — exactly as a Judge rejection is, and with Build and UnitTest Checks
armed. Before this rule a Worker gate failure produced a STANDALONE rescue
mission that passed its own gate and landed through `LocalMerge` with no
reviewer ever reading the final code. A standalone mission that has no voyage
never had review stages and keeps a standalone rescue.

### A rescue brief keeps the reviewer's instructions, not only its diagnosis

The rescue brief embeds the failed mission's reviewer feedback under a size cap.
The cap exists because an uncapped gate log once tripped a provider's content
filter and opened a self-perpetuating loop; it stays. What changed is the shape
of the cut. A Judge report is written diagnosis first and instructions last
(Completeness, Correctness, Tests, Failure Modes, Suggested Follow-ups,
Verdict, then the `[ARMADA:VERDICT]` line), so a head-first cut kept the table
of what failed and dropped every line that said what to do. Four rescues in one
shift each needed an operator to re-send the tail by Mail.

An over-cap Judge report now keeps `## Suggested Follow-ups`, `## Verdict` and
the verdict line whole, fills the remaining budget from the head, and places
the truncation marker where the omitted middle was, naming the sections it
dropped. An under-cap report is embedded whole. Text without those headers, such
as a gate log, keeps the head-first cut, because its signal is at the top. When
a rescue brief still reads `truncated`, the marker names what is missing; the
full report remains on the failed mission's `ReviewComment`.

### A rescue is judged by what it changed

A rescue that starts, logs, and exits satisfies every liveness measure Armada
keeps: the process lived, the captain reported, the pipeline advanced. None of
that says the defect was touched. One rescue ran for twenty-four hours, drew
escalating stall nudges, died on a runtime crash, and left behind a single
changed documentation file.

An Implementation-mode rescue whose change set is empty, or consists only of
documentation, now fails with `ineffective_rescue` and the change set named in
the `FailureReason`. Read that field before retrying: the correct response is a
replacement brief that quotes the defect, not another attempt at the same one.

Three limits keep this from firing on correct work:

- **Only rescues are assessed.** A first-attempt mission may legitimately have
  been dispatched to write documentation.
- **Only Implementation mode.** An Audit or Research mission delivers a report
  and is never expected to change code. Judging those by a diff is the same
  mistake in the other direction, and it once marked correct work Failed.
- **The original mission's paths are NOT compared against.** A rescue is
  expected to rewrite the prior branch from scratch over the same files, so
  treating an overlapping path set as a no-op would flag the normal case.

Documentation means a `.md`, `.txt`, `.rst`, or `.adoc` file, anything under a
`docs/` directory at any depth, or a conventional bare name such as `README` or
`LICENSE`. A directory that merely contains the letters "doc" - `docker/`, a
`DocumentStore/` source tree - is not documentation.

## 6. Release And Deployment Workflow

Use a workflow profile for repeatable commands. Use named environments for
rollout targets.

1. Create a release and link its objective, voyages, missions, artifacts, and
   Checks.
2. Move the release through its supported states only when evidence permits.
3. When a release transitions to Shipped and the `cdWebhook` setting is
   configured, the admiral POSTs a `release.shipped` evidence payload to the
   configured endpoint; delivery or failure is recorded as a
   `release.webhook.*` event on the release. A webhook failure never blocks the
   release update.
4. Create a deployment against a named environment.
5. Approve it when the environment requires approval.
6. Run deployment verification.
7. Use rollback on the same deployment record if verification fails.
8. Link the related incident and runbook execution.

See [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for the detailed procedure.

## 7. Configuration And Administration

Treat fleet, vessel, captain, persona, pipeline, playbook, runbook, workflow
profile, environment, template, backup, and server-stop tools as administrator
surfaces. Read the existing record first. Use
`armada_audit_operational_assets` before and after asset changes. Validate
provider models with a live provider call before putting them in a tier.

### Per-captain provider credentials

A captain whose model is served by an external provider (a `provider/model` id such as `cun-ai/claude-fable-5`) normally uses
the provider's host-level environment variable (for example `CUN_AI_KEY`). A captain may instead carry
its own `apiKey` (and optional `apiBaseUrl`) on its record, which wins over
the environment variable. This lets captains on separate provider subscriptions
run side by side on one Admiral; burn down each subscription, then delete its
captains. The MCP captain surface returns the key masked (last four
characters preserved); the dashboard keeps the raw value so the edit form can
prefill it, and entering the key in the dashboard never passes it through an
orchestrator. Creating a captain whose model is not entitled on the
environment key persists with a warning instead of failing, so a credential
that arrives after creation can be attached from the dashboard.

Vessel instruction files and generated briefing files are protected paths.
Captains must propose instruction changes. The orchestrator reviews and applies
them outside the mission dock.

### Vessel Workspace

The Workspace surface (dashboard `Workspace` page, `POST
/api/v1/workspace/vessels/{vesselId}/exec`, and the diff/file/tree/status REST
routes) operates on a vessel's configured working directory. Use it to browse,
edit, search, review a working-tree diff, or run a one-shot shell command.
Workspace shell commands run through the platform shell, are killed with their
process tree when they exceed the timeout, and are restricted to tenant
administrators. Every git invocation the workspace performs is bounded by a
30-second timeout that kills the process tree and suppresses the pager and
credential prompts, so a wedged git cannot hang the endpoints.

Backups can contain operational state. Store them in an approved location and
apply retention limits. Restore, delete, purge, stop, and bulk operations need
an explicit operator decision.

## 8. Complete MCP Tool Catalog

The built-in catalog contains 177 names. Some names are compatibility aliases.
Some tool families register only when their service is enabled.

Risk labels:

- **Read**: no intended state change.
- **Write**: creates or updates Armada state.
- **Execute**: starts work, a command, landing, or rollout.
- **Interrupt**: stops or cancels active work.
- **Destructive**: deletes, purges, restores, or stops the server.

### 8.1 Status, Enumeration, Jobs, And Diagnostics

| Risk | Tools |
| --- | --- |
| Read | `armada_status`, `armada_enumerate`, `armada_job_status`, `armada_captain_diagnostics`, `armada_unlanded_branches`, `inbox` |

#### Needs-you inbox

`inbox` returns everything across the fleet that is waiting on a decision or
intervention from the operator, ordered most-urgent first. It answers "is there
anything waiting on me?" in one call instead of polling each entity. Two kinds
of item qualify:

- **Human-in-the-loop**: a mission in Review to approve or reject, or a
  deployment pending approval.
- **Human-out-of-the-loop**: a failed mission, a mission whose work could not
  land, a failed merge, a failed or verification-failed deployment, or a
  stalled captain.

Purely informational state changes are excluded. Each item carries a `kind`,
a `severity` (Critical, Warning, or Info), a title, detail, the referenced
entity, and a dashboard href for one-click navigation. An empty list means
nothing currently needs attention. The same surface is available as the
dashboard `Needs You` page and the `armada inbox` CLI command (`--critical`
shows only critical items).

### 8.2 Fleets And Vessels

| Risk | Tools |
| --- | --- |
| Read | `armada_get_fleet`, `armada_get_vessel` |
| Write | `armada_create_fleet`, `armada_update_fleet`, `armada_add_vessel`, `armada_update_vessel`, `armada_update_vessel_context` |
| Destructive | `armada_delete_fleet`, `armada_delete_fleets`, `armada_delete_vessel`, `armada_delete_vessels` |

### 8.3 Captains

| Risk | Tools |
| --- | --- |
| Read | `armada_get_captain`, `armada_get_captain_log` |
| Write | `armada_create_captain`, `armada_update_captain`, `armada_bench_captain`, `armada_unbench_captain` |
| Interrupt | `armada_stop_captain`, `armada_stop_all` |
| Destructive | `armada_delete_captain`, `armada_delete_captains` |

### 8.4 Voyages And Missions

| Risk | Tools |
| --- | --- |
| Read | `armada_voyage_status`, `armada_mission_status`, `armada_get_mission_diff`, `armada_get_mission_log` |
| Write | `armada_create_mission`, `armada_update_mission`, `armada_transition_mission_status` |
| Execute | `armada_dispatch`, `armada_restart_mission`, `armada_retry_landing` |
| Interrupt | `armada_cancel_mission`, `armada_cancel_voyage` |
| Destructive | `armada_purge_mission`, `armada_delete_missions`, `armada_purge_voyage`, `armada_delete_voyages` |

### 8.5 Planning, Objectives, And Backlog

| Risk | Tools |
| --- | --- |
| Read | `list_objectives`, `list_backlog`, `get_objective`, `get_backlog_item`, `list_backlog_refinement_sessions`, `get_backlog_refinement_session`, `get_backlog_planning_session` |
| Write | `create_objective`, `create_backlog_item`, `update_objective`, `update_backlog_item`, `reorder_objectives`, `reorder_backlog_items`, `create_backlog_refinement_session`, `send_backlog_refinement_message`, `summarize_backlog_refinement_session`, `apply_backlog_refinement_summary`, `stop_backlog_refinement_session`, `create_backlog_planning_session`, `delete_objective`, `delete_backlog_item` |
| Execute | `dispatch_backlog_planning_session`, `armada_decompose_plan`, `armada_parse_architect_output` |

### 8.6 Objective Scheduler

| Risk | Tools |
| --- | --- |
| Read | `armada_objective_scheduler_status` |
| Write | `armada_objective_scheduler_set`, `armada_mark_objective_auto_dispatchable`, `armada_objective_scheduler_clear_stale_pause` |

Scheduler state changed through MCP is persisted to the loaded settings file
and survives an Admiral restart. A pause set through `paused=true` should carry
`pausedBy` (your participant key) and `pauseReason`; both are persisted and
shown by `armada_objective_scheduler_status`. A pause outlives the session that
set it, so without an owner nobody can tell a live deploy window from a
departed peer's leftover.

`armada_objective_scheduler_clear_stale_pause` is the autonomy layer's one
permitted write to the pause: clear only, never engage. It succeeds only when
the pause names its owner, the owner is absent from the coordination presence
window, and the absence exceeds
`autonomousObjectiveScheduler.stalePauseAbsenceMinutes` (floor 30, twice the
presence default; a deploy with verification finishes well inside that). It
wakes every active session and posts a board note naming the stale owner, the
recorded reason, the set time and the measured absence, then clears once and
persists. An unattributed pause is refused and stays an operator's to clear.
The dispatch hold is never touched: it clears itself on a successful redeploy,
so a hold that survives is a deploy that stopped halfway and needs a human.
Pass `dryRun=true` to read the decision and evidence without acting. Scheduler dispatches use the same Build and
UnitTest Check arming path as operator dispatches. A dispatch hold blocks both.
See `docs/SCHEDULING.md` for eligibility and ordering, and section 4.11 for the
separate optional lead-cycle layer.

### 8.7 Checks

| Risk | Tools |
| --- | --- |
| Read | `get_check_run` |
| Write | `resolve_check`, `armada_resolve_check` |
| Execute | `run_check`, `retry_check_run` |

`armada_resolve_check` is a compatibility alias for `resolve_check`.

`run_check`, `retry_check_run`, and `get_check_run` return a BOUNDED view of a
check run, not the complete command log. A build or test log is routinely one
to several megabytes; returning it overran the tool output limit, so the caller
received a truncation error instead of the verdict and had to parse the record
out of band on every call - including the common case where the only question
was whether the check passed.

The bounded view carries what answers that question:

| Field | Purpose |
| --- | --- |
| `status`, `exitCode` | The verdict |
| `testSummary`, `coverageSummary` | Parsed totals, replacing a count grepped from the log |
| `artifacts` | Paths to the per-project `.trx` files, which name individual failures |
| `outputTail` | The last 40 lines, where a failure's cause almost always is |
| `outputLength`, `outputTruncated` | How much was withheld |
| `outputRetrieval` | The exact call that returns the rest |

Nothing is silently withheld: a truncated view states the full log's size and
names the call that fetches it. Use `get_check_run` with `includeOutput=true`
for the complete record, or `outputTailLines` to widen the tail.

### 8.8 Merge Queue And Audit

| Risk | Tools |
| --- | --- |
| Read | `armada_get_merge_entry`, `armada_drain_audit_queue` |
| Write | `armada_enqueue_merge`, `armada_record_audit_verdict` |
| Execute | `armada_process_merge_entry`, `armada_process_merge_queue` |
| Interrupt | `armada_cancel_merge` |
| Destructive | `armada_delete_merge`, `armada_purge_merge_queue`, `armada_purge_merge_entry`, `armada_purge_merge_entries` |

### 8.9 Docks, Signals, And Events

| Risk | Tools |
| --- | --- |
| Read | `armada_get_dock`, `armada_list_papercuts` |
| Write | `armada_send_signal`, `armada_nudge_voyage`, `armada_mark_signal_read` |
| Destructive | `armada_delete_dock`, `armada_purge_dock`, `armada_delete_docks`, `armada_delete_signals`, `armada_delete_event`, `armada_delete_events` |

#### Coordination Board (Chatroom)

The coordination board is a shared room where concurrent operator sessions and
the dashboard post short notes about what they are doing, so no session is
surprised by a voyage another session dispatched. Unlike signals, notes reach
every reader immediately; they are never injected into captain briefs.

- `armada_coordination_post` — post a note. Claim work before you start it;
  report outcomes when you finish.
- `armada_coordination_read` — read recent notes plus who is active. Read this
  before dispatching voyages or touching incidents so you do not duplicate a
  peer session's work.
- `armada_coordination_heartbeat` — refresh your presence while working. The
  heartbeat and read responses carry `UnreadWakes` when notes are addressed to
  your participant key: PAUSE and address those before continuing, then
  acknowledge each with `armada_mark_signal_read`. This is how a session inside
  a blocking loop learns it was handed work at the next tool boundary.

#### Identify your session so wakes reach you on any tool

MCP has no channel that can interrupt a running agent. Armada's MCP transport
is stateless, so the server cannot push, and no client turns an inbound
notification into a model turn. A tool result is the only content a session is
certain to read, so a pending wake rides back on one.

Send the caller's participant key on every MCP request:

```
X-Armada-Participant: <participantKey>
```

Armada then appends an `[ARMADA WAKE]` block to the result of whichever tool
the session calls next, so `armada_status` or `armada_voyage_status` delivers
mail just as `armada_coordination_read` does. Without the header a session is
anonymous and receives no wake — that is deliberate, because the server would
otherwise have to guess whose mail to hand out.

Configure it per client:

- SSH stdio bridge: set `ARMADA_PARTICIPANT_KEY` in the MCP server's `env`.
  `scripts/mcp-ssh-http-bridge.mjs` turns it into the header.
- Direct HTTP clients: add the header to the server entry's `headers` object.
- Bounded helpers: `scripts/autonomy/spawn-helper.sh` writes the header into the
  generated per-helper config. An `AUTONOMY_CLAUDE_MCP_CONFIG` you supply
  yourself must carry the header, or the helper gets no wakes.

Delivery is not acknowledgement. The banner repeats on every tool result until
the session calls `armada_mark_signal_read`; a wake that stopped appearing
before it was read would be a lost wake. The two coordination tools above are
excluded, because they already return the same wakes in their own payload.

Who may acknowledge a Wake follows who it woke. A session that sends the
participant header may mark read a Wake addressed to it (`[to=<its key>]`), and
the effective AgentWake participant (`armada_agentwake_status`) may also mark
read an UNADDRESSED Wake — a mission-outcome or critical wake prefixed
`[vsl=...]` or `[CRITICAL]` — because that is the session such a wake starts.
Any other authenticated key is refused with the owner named; an anonymous
caller (no header) remains unrestricted. Mail and Nudge signals are consumed by
the stage handoff and are never acknowledged through this tool.

#### Do not wait by polling. Subscribe.

The banner is reliable but not immediate: a session that calls no tool sees
nothing until it does. The instinct is to close that gap with a blocking poll --
a shell `while` loop inside one `ssh_exec` call. Do not. That shape was measured
on one operator session (2026-08-23 23:19Z to 01:50Z) and it costs more than it
looks:

- 68 assistant messages, of which only FIVE carried visible text. The operator
  saw nothing for seven minutes at a stretch, and fourteen minutes before the
  final handoff.
- Three of five turns ended `state=interrupted, stopReason=tool_use`, each within
  four seconds of a failed `ssh_exec`. Two of those restarted the underlying
  session, which is why the run finished one item and wrote a handoff.
- While the loop runs the session makes no MCP calls, so a directed board note
  cannot reach it either. A helper waiting on an answer times out against a lead
  that is technically alive.

The Admiral's WebSocket hub already broadcasts every voyage, mission, incident
and board change. Subscribe to it instead, and let each change arrive as an
event:

```sh
ssh <server> 'node <armada-checkout>/scripts/autonomy/watch-armada.mjs \
    --voyage <voyage-id> --participant <your-key> --exit-on-terminal'
```

Drive it with the harness's Monitor tool, so every line becomes a notification.
Each mission line is a stage boundary, which is the only window where a
correction still reaches the next brief. `--exit-on-terminal` ends the watch when
the voyage finishes. `--all-notes` widens it to the whole board; `--quiet-captains`
drops the stall lines.

The watcher notifies and nothing more. It never reads, acknowledges, or consumes
a wake, so the banner and `armada_mark_signal_read` remain the delivery and
acknowledgement path.

### 4.11 Autonomous lead cycles

The objective scheduler dispatches eligible objectives on its own. It does not
land work, close incidents, refill campaign lanes, or answer a helper. That
operator layer is `scripts/autonomy/lead-cycle.sh`, which runs ONE bounded pass
and exits.

```sh
scripts/autonomy/lead-cycle.sh run      # one cycle now; refuses if one is running
scripts/autonomy/lead-cycle.sh status   # running? and the last result
scripts/autonomy/lead-cycle.sh kill     # stop the running cycle
```

Two things start a cycle, and they are complementary:

- **The timer**, `scripts/autonomy/systemd/armada-lead-cycle.timer`, every hour.
  This catches work that arrives quietly, such as new objectives added while the
  fleet was idle and no event fired.
- **AgentWake**, when a mission outcome or a note addressed to the lead's key
  arrives. Set `remoteTrigger.agentWake.command` to
  `scripts/autonomy/lead-wake.sh`. That shim exists because Armada starts the
  configured command with the runtime's own flags in argv, including
  `--strict-mcp-config` with no `--mcp-config` -- which would give the woken
  process zero Armada tools -- and `--continue`, which resumes an unrelated
  session. The shim ignores argv, keeps only the wake text from stdin, and hands
  it to `lead-cycle.sh`.

`lead-cycle.sh` is single-flight. A timer tick arriving while a wake-started
cycle is running is refused, not queued, so one participant key never gets two
process owners.

**The lead runs only when nobody is watching.** `armada_lead_cycle_begin`
refuses with `operator-present: <keys> seen within N minutes` while any board
participant other than the lead itself (or a `helper-*` it started) has
heartbeated within `grokLead.operatorPresenceMinutes` (default 30; 0 disables
the gate). An interactive session, a dashboard viewer, and an Armada helper
session all count as an operator. The launcher records the refusal as
`skipped server-lease-refused` and exits, so a cycle that finds an operator
present costs one tool call. Measured before the gate: 131 cycles in 24 hours
while an operator session was live, 93% of their tool calls reads, and every
landing, closure and dispatch that mattered made by the operator.

Prefer `remoteTrigger.agentWake.deliveryMode = StoredWake` for the lead: a
directed note or mission outcome then waits on the board for the next timed
cycle instead of starting one. Process delivery (`Both`) started four cycles
for every timer tick and most of them re-triaged work an operator had already
closed.

**Cursor captains need `--approve-mcps`.** cursor-agent discovers a workspace
`.cursor/mcp.json` but leaves its servers "not loaded (needs approval)" in a
non-interactive `--print` run; `--trust` covers the workspace only. The runtime
passes `--approve-mcps` so the dock's Armada server loads. Proof is a Research
smoke mission whose report lists the Armada server by name; the file on disk
proves nothing by itself.

**One board, whatever the key says.** Every coordination tool resolves a blank
room key, `fleet`, and the literal word `default` to the one shared room
(`CoordinationRoom.NormalizeKey`). A client that reads "omit for the default
room" and sends the word `default` no longer creates a second room, which
split the board in two and hid the lead's handoffs from the completion gate.

The launcher also acquires Armada's durable `autonomy:lead-cycle` lease. This
prevents overlap with an external Grok lead. The systemd service requests
standby fallback. In `GrokPrimary` mode, Armada refuses that request until the
configured Grok inactivity period expires. The default is 130 minutes. In
`LegacyPrimary` mode, the existing lead runs normally.

The timer checks fallback eligibility once per hour. Therefore, a timer-only
fallback can start after the 130-minute threshold, not exactly at that time.
An AgentWake start uses the same shared check.

The Grok listener and its shared cycle controls are disabled by default. See
[Grok Bot Lead Integration](autonomy/grok-bot-lead.md) before you enable them.

The shared lifecycle tools are:

- `armada_lead_cycle_status` reads the current mode and lease;
- `armada_lead_cycle_begin` requests one bounded cycle;
- `armada_lead_cycle_heartbeat` renews the active lease;
- `armada_lead_cycle_complete` verifies that every claim is released, posts the
  handoff to the shared board itself when the lead has not already posted the
  same text, records completion, and releases the lease. It never refuses on a
  wording or room mismatch: that refusal made the lead re-post and retry until
  one copy matched, which is where duplicate handoff notes came from. The lead
  passes its handoff to this tool and posts nothing itself;
- `armada_lead_cycle_fail` records an early stop or failure and releases the
  lease.

The timer and AgentWake must use the same state directory. The default is
`$HOME/.armada/autonomy-lead`. The Admiral container can write this bind mount,
and the host timer can read the same lock and log files. Do not use
`$HOME/autonomy-lead`: that host path is not mounted in the Admiral container,
so an AgentWake process cannot create it.

**Give the lead its own participant key.** `armada-lead` by default, and never an
interactive operator's key. Two process owners on one key duplicate dispatch and
cannot be told apart on the board.

An unattended cycle cannot ask a question. The prompt tells it to post an owner
decision to the board as a named item and carry on, rather than block. Read those
on your next session; they are the cycle's questions to you.

**Leave the timer running across a redeploy.** A tick that lands while the Admiral
is rebuilding preflights the MCP endpoint, records `admiral-unreachable`, and
exits without starting a cycle. Stopping the timer for a deploy needs somebody to
start it again afterwards, and that step gets missed: the lead sat idle for an
hour because a redeploy left it stopped. The skip is what makes the manual step
unnecessary. Stop it deliberately with `lead-cycle.sh kill`, or by disabling the
timer, and say so on the board because nothing else will notice.

**The timer is wall-clock and persistent.** `OnCalendar=hourly` with
`Persistent=true`, so the next run does not depend on the unit's activation
history, and a tick missed while the host was down runs at the next start. An
earlier monotonic schedule carried `Persistent=true` where it has no effect, so a
missed run was silently never caught up.

**The model is pinned.** The default runtime is Claude Code. It uses
`claude-fable-5` through the same Anthropic-compatible Vilao route as the Fable
judge captains. Claude Code and the provider control prompt caching for this
route.

Before you install the service, create the provider key file:

```sh
install -o armada -g armada -d -m 700 /home/armada/.armada/secrets
install -o armada -g armada -m 600 <secure-vilao-key-source> \
  /home/armada/.armada/secrets/autonomy-lead-vilao.key
```

The key file must contain only the Vilao API key. Do not put the key in the unit,
the repository, or the generated event log. The Claude Code launcher reads the
file and removes an inherited `ANTHROPIC_AUTH_TOKEN` before it starts.

**Each cycle leaves two files** under the lead's log directory:
`cycle-<stamp>.jsonl`, the whole event stream, and `cycle-<stamp>.log`, a rendered
digest of it. Read the digest; it lists what the cycle said, every tool it called,
whether each call worked, and how the run ended. A stream with no result event is
reported as `INCOMPLETE`, which is what a timeout looks like. `--print` alone emits
only the closing paragraph, which is how one eight-minute cycle left a 73-byte log
claiming it had nothing to report.

**The default cap is 30 minutes.** The prompt tells the cycle to reserve the last
three for its handoff and cleanup. Raise it with `AUTONOMY_LEAD_TIMEOUT_MIN`, and
keep the systemd unit's `TimeoutStartSec` above it as the outer backstop.

**The permission policy is the real boundary.** A headless run has nobody to
answer a permission prompt, so `lead-cycle.sh` writes the runtime policy before
it starts the model. It allows the primary agent to use Armada and ordinary file
and shell tools. It denies fleet-destructive and purge tools, deployment and
release tools, check resolution, the fleet-wide dispatch hold, AgentWake
registration, force push, Docker Compose, and systemd. Deny wins over allow. The
optional OpenCode agents have an additional read-only policy. Widen the policy
only for a named need.

**Send the Claude prompt on stdin.** `claude --mcp-config` is variadic,
so a positional prompt after it is consumed as a second config path and the run
dies with `MCP config file not found: <the entire prompt>`. OpenCode accepts the
prompt as its `run` argument.

### 8.10 Incidents

| Risk | Tools |
| --- | --- |
| Read | `armada_list_incidents`, `armada_get_incident` |
| Write | `armada_create_incident`, `armada_update_incident`, `armada_close_incident` |
| Destructive | `armada_delete_incident` |

### 8.11 Releases

| Risk | Tools |
| --- | --- |
| Read | `get_release` |
| Write | `create_release`, `update_release`, `armada_update_release`, `test_release_webhook` |

`armada_update_release` is a compatibility alias for `update_release`.

`test_release_webhook` is registered only when the `cdWebhook` setting is
configured. It POSTs a synthetic `release.shipped` payload to the configured
endpoint and returns the delivery outcome, so you can verify reachability and
authentication before approving a real release.

### 8.12 Deployments

| Risk | Tools |
| --- | --- |
| Read | `get_deployment` |
| Write | `create_deployment`, `update_deployment`, `armada_update_deployment` |
| Execute | `approve_deployment`, `verify_deployment`, `rollback_deployment` |

`armada_update_deployment` is a compatibility alias for
`update_deployment`.

### 8.13 Runbooks

| Risk | Tools |
| --- | --- |
| Read | `list_runbooks`, `get_runbook`, `list_runbook_executions`, `get_runbook_execution` |
| Write | `create_runbook`, `update_runbook`, `update_runbook_execution` |
| Execute | `start_runbook_execution` |
| Destructive | `delete_runbook`, `delete_runbook_execution` |

### 8.14 Workflow Profiles And Environments

| Risk | Tools |
| --- | --- |
| Read | `list_workflow_profiles`, `get_workflow_profile`, `validate_workflow_profile`, `preview_workflow_profile`, `list_environments`, `get_environment`, `armada_audit_operational_assets` |
| Write | `create_workflow_profile`, `update_workflow_profile`, `create_environment`, `update_environment` |
| Destructive | `delete_workflow_profile`, `delete_environment` |

### 8.15 Personas, Pipelines, Playbooks, And Templates

| Risk | Tools |
| --- | --- |
| Read | `get_persona`, `get_pipeline`, `get_playbook`, `list_prompt_templates`, `get_prompt_template` |
| Write | `create_persona`, `update_persona`, `create_pipeline`, `update_pipeline`, `create_playbook`, `update_playbook`, `create_prompt_template`, `update_prompt_template`, `reset_prompt_template` |
| Destructive | `delete_persona`, `delete_pipeline`, `delete_playbook` |

### 8.16 Code Index, Context Packs, And Graphs

| Risk | Tools |
| --- | --- |
| Read | `armada_index_status`, `armada_code_search`, `armada_context_pack`, `armada_fleet_code_search`, `armada_fleet_context_pack`, `armada_graph_search_symbols`, `armada_graph_get_callers`, `armada_graph_get_callees`, `armada_graph_get_impact`, `armada_graph_suggest_affected_tests`, `armada_graph_get_node`, `armada_graph_get_files`, `armada_graph_explore` |
| Execute | `armada_index_update` |

### 8.17 Reflection Memory

| Risk | Tools |
| --- | --- |
| Read | `armada_check_stale_memory` |
| Execute | `armada_consolidate_memory` |
| Write | `armada_accept_memory_proposal`, `armada_reject_memory_proposal` |

### 8.18 AgentWake

| Risk | Tools |
| --- | --- |
| Read | `armada_agentwake_status` |
| Write | `armada_register_agentwake_session` |

### 8.19 Dispatch Hold

| Risk | Tools |
| --- | --- |
| Write | `armada_dispatch_hold` |

The hold is fleet-wide: while it is engaged every new dispatch is refused,
whichever session or scheduler asks, and in-flight voyages continue. Engage it
with your session name and a reason before an Admiral rebuild; a successful
restart clears it by design. The autonomous lead is denied this tool and never
clears a hold, stale or otherwise.

AgentWake is a process-delivery transport, not the work queue or the source of
truth. Put the stable lead key in `remoteTrigger.agentWake.participantKey` when
addressed process wakes must work after an Admiral restart. A registration with
its own `participantKey` temporarily overrides the configured key and remains
useful for a controlled probe or a resumable Claude or Codex session. An
addressed board note always creates a Wake signal. With delivery mode
`SpawnProcess` or `Both`, a note for the effective participant key also starts
the runtime process. With `StoredWake`, or when no key matches, the signal
remains for the next heartbeat or read. `armada_agentwake_status` reports the
configured key, effective key, delivery mode, runtime, and transient session.

Nothing is pushed under any delivery mode. The row waits until the session next
calls a tool, and the participant header above is what lets that call carry it.
This mode was called `McpNotification`, which promised a push the transport
cannot carry; it is now `StoredWake`. Settings files using the old spelling keep
loading unchanged, and Armada writes the new one.

OpenCode does not resume an earlier conversation for AgentWake. It starts a
fresh session by design. Put the complete task in the addressed note and make
the bootstrap prompt reconstruct state from the coordination board and durable
memory. Never give the same participant key to a resident process and an
AgentWake registration.

### 8.19 Backup, Restore, And Server Control

| Risk | Tools |
| --- | --- |
| Write | `armada_backup` |
| Destructive | `armada_restore`, `armada_stop_server` |

### 8.20 Disk Lifecycle

`armada_disk_lifecycle` reports and, when explicitly enabled in settings,
reclaims Armada-owned disk storage. Run `action=scan` for the dry-run report
before anything destructive: it returns bytes per owned category (docks, bare
repos, mission logs, diffs, instruction snapshots, dock metadata, integration
and merge-queue worktrees, temp artifacts, backups) plus reclaimable counts.
`action=reconcile` additionally purges stale sibling-worktree leases and, only
when `diskLifecycle.enabled` is true and `diskLifecycle.dryRun` is false,
deletes eligible items. Reclamation fails closed: only paths under the allowed
roots, not symlinks, past their grace period, and not referenced by active
docks, missions, or merge-queue entries are ever touched. Docker image and
build-cache pruning stays an explicit host-side operator action
(`docker builder prune` with the current and rollback images protected), never
a container-triggered deletion.

| Risk | Tools |
| --- | --- |
| Read | `armada_disk_lifecycle` (action `scan`) |
| Destructive | `armada_disk_lifecycle` (action `reconcile`; gated by `diskLifecycle.enabled` and `diskLifecycle.dryRun`) |

### 8.21 Token Usage

`token_usage_summary` summarizes model token usage over a time window: time
buckets with a per-model breakdown, a whole-window per-model aggregate ordered
most-used first, and grand totals for input, output, cached, and total tokens.
Narrow it with `model`, `runtime`, `source` (mission, chat, or planning),
`vesselId`, or `captainId`, and set the window with `sinceHours` or an explicit
`fromUtc`/`toUtc` pair. `bucketMinutes` accepts fractional values.

Read the `estimatedCount` before comparing models. Counts are real only where
the runtime reports usage, and estimated otherwise, so a window mixing runtimes
mixes measured and inferred numbers in one total. Several runtimes report no
usage at all; for those, the admiral-side prompt-byte accounting is the figure
that is comparable across runtimes.

| Risk | Tools |
| --- | --- |
| Read | `token_usage_summary` |

## 9. Safety Rules

- Read before write.
- Confirm the exact ID before a cancel, delete, purge, restore, rollback, or
  server stop.
- Do not use bulk deletion when a specific record is sufficient.
- Do not retry a dispatch until you know whether the first request created a
  voyage.
- Do not call `resolve_check` to turn an unknown or failed result green.
- Do not accept a reflection proposal without reading the proposed content.
- Do not change vessel context or shared instructions from a captain mission.
- Do not push, deploy, release, or roll back without the applicable operator
  authority.
- Never put credentials or private operational identifiers in public docs,
  prompts, logs, examples, or commit messages.

## 10. Verification Checklist

Before you report completion:

- The objective state matches the evidence.
- The voyage and each mission have the expected terminal state.
- Required Checks passed with real output.
- The target branch contains the expected commit.
- Release and deployment records match what shipped.
- Incidents have evidence for their final state.
- Deferred work has its own record.
- No sensitive value or private operational identifier entered a public
  artifact.
