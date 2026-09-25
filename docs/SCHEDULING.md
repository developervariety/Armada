# Mission Scheduling

This document explains how Armada decides which mission to assign to which captain and in what order.

## Priority

Every mission has a **priority** field -- an integer that defaults to **100**. Lower numbers mean higher priority. When multiple missions are in the `Pending` state, the Admiral picks the one with the lowest priority number first.

| Priority | Typical Use |
|----------|-------------|
| 1-10 | Urgent / jump the queue |
| 50 | High importance |
| 100 | Default |
| 200+ | Low importance / background work |

## Voyage Association

Missions that belong to an **active or in-progress voyage** are prioritized over standalone (voyageless) missions. This ensures that batch work dispatched as a voyage is completed cohesively before the Admiral picks up unrelated standalone missions at the same priority level.

### How Voyage Missions Interleave with Standalone Missions

Consider the following pending missions:

| Mission | Priority | Voyage |
|---------|----------|--------|
| msn_A | 100 | vyg_sprint1 (in-progress) |
| msn_B | 100 | *(none)* |
| msn_C | 50 | *(none)* |

Assignment order:

1. **msn_C** -- lowest priority number (50), picked first regardless of voyage status.
2. **msn_A** -- same priority as msn_B (100), but belongs to an active voyage, so it wins.
3. **msn_B** -- standalone mission, assigned last.

Priority always takes precedence over voyage association. Voyage association is a tiebreaker within the same priority level.

## FIFO Within Same Priority

When multiple pending missions share the same priority level (and the same voyage status), they are assigned in **creation order** -- first in, first out. The mission that was created earliest is assigned first.

## Captain Assignment

When a captain becomes idle -- either by finishing a mission or by being newly registered -- the Admiral checks the pending mission queue on the **next heartbeat cycle** and assigns the highest-priority unassigned mission.

Assignment commits through two conditional writes, and each failure point stores its undo:

1. The captain claim (**TryClaim**) is a compare-and-set on an `Idle` captain, so one captain never serves two missions.
2. The mission then records its dock only while its stored status is still `Assigned`, so a mission is never assigned twice and a cancellation made during provisioning wins.

If the claim fails because another mission took the captain first, the stored mission returns to `Pending` with no captain, dock or new branch, its assignment state reads `WaitingForIdleCaptain`, the log names `captain_claim_lost`, and the provisioned dock is reclaimed and deleted. If the mission changed status instead, that status stays, the captain is released only while it still records this mission, and the dock is deleted. The next assignment pass retries the mission.

### What Happens When All Captains Are Busy

Missions stay in the `Pending` state until a captain finishes its current work and becomes idle. On the next heartbeat cycle after a captain frees up, the Admiral assigns the highest-priority pending mission to that captain. No missions are lost or dropped -- they simply wait in the queue.

## Heartbeat Cycle

The Admiral runs a health-check loop on a configurable interval controlled by `HeartbeatIntervalSeconds` (default: **30 seconds**). On each cycle the Admiral:

1. **Detects idle captains** -- captains that have finished their current mission.
2. **Assigns pending missions** -- matches idle captains with the highest-priority unassigned missions.
3. **Checks for stalled captains** -- captains that have not reported progress within the `StallThresholdMinutes` window (default: 10 minutes). A confirmed stall stops the captain's process and, within `MaxRecoveryAttempts`, relaunches the agent in the same dock. The stopped process is registered as superseded before the stop, so its exit is never handled as the mission's failure or as an out-of-memory kill; an exit from any process that is no longer the captain's recorded process is ignored and recorded as `captain.process_exit_ignored`. The mission's streamed output, final-message artifact, terminal marker and heartbeat throttle belong to the launch of its current process: each launch starts them empty, and a process exit releases them only while that process's launch still owns them, so a stopped process's late exit never clears the relaunched process's output or terminal marker. The relaunch is written back only while the mission keeps the status and captain it was decided on; if another writer moved the mission meanwhile, the relaunched process is stopped, the captain is released, and `captain.recovery_abandoned` is recorded. While the dispatch hold is engaged, a stalled captain is stopped and not relaunched: its mission returns to `Pending` and waits for the hold to clear (`docs/MCP_TOOL_CATALOG.md`, "Dispatch Hold").
4. **Runs escalation rules** -- triggers recovery or alerts for stalled or failed missions.

After those steps the Admiral runs its periodic maintenance, each step on its own cadence in health-loop cycles: objective dispatch attempt reconciliation and stale background-job reaping every cycle, log rotation and planning-session maintenance every 10, data expiry every 100 (completed records after `dataRetentionDays`, except the newest snapshot of each incident and of each runbook execution, objective deletion tombstones and typed-decision reversals, which are the only record of what they describe and are kept whatever their age, production facts after `productionFactRetentionDays`, and captured request history with its detail after `requestHistoryRetentionDays`, default 30; `0` keeps each), disk lifecycle reconciliation every `diskLifecycle.reconcileIntervalCycles`, captain log screening every cycle (the screen self-guards to `captainLogScreening.intervalSeconds` and returns immediately while it is off), the code-index staleness sweep every `codeIndex.stalenessSweepIntervalCycles` (it refreshes only vessels that are already indexed), and the branch cleanup sweep every `branchCleanupSweepIntervalCycles` (default 200). `branchCleanupSweepIntervalCycles` and the sweep's `branchCleanupPreservedRefRetentionDays` hot-reload, so an edited value applies on the next cycle without a restart. `codeIndex.stalenessSweepIntervalCycles` hot-reloads the same way. Each step runs in isolation. A failing step logs `<step> failed: <reason>` and the steps after it still run.

The health check itself runs the same way. Each of its sub-steps (quarantine restore, working-captain checks, orphaned-mission and stage-watchdog recovery, voyage completion, pull-request and merge-entry reconciliation, audit-queue notification, orphaned-dock reclaim, pending-mission dispatch, captain-pool maintenance, escalation) is its own step. A sub-step that throws logs `health check step <name> failed (<n> failures since start): <reason>`, and the admiral counts the failures per step name. The sub-steps after it still run, so one bad record cannot stop dispatch. The four background sweep triggers (automatic Check runs, autonomous recovery, incident lifecycle, objective scheduler) run as separate steps after the health check, so each sweep is triggered on every cycle even when an earlier step fails. A failing step does not stop the cycle count, so maintenance keeps its cadence.

The model-endpoint health loop runs beside the heartbeat loop and waits `HeartbeatIntervalSeconds` between sweeps. It reads the interval after each sweep, so a reloaded heartbeat interval applies to both loops without a restart.

## Captain Log Screening

The Admiral can screen the log each in-progress mission is writing, read-only, on a cadence. A
sweep reads a bounded tail of the mission's live log, runs every registered screening pass over it,
and on a finding posts one voyage-tagged coordination-board note naming the rule classes with one
line of evidence each, plus one `captain.log_screen` event. The voyage tag is what carries the note
into that voyage's next stage brief.

The screen never cancels, pauses, mails, re-dispatches, kills or steers a mission. Its only writes
are the board note and the event.

The shipped pass is deterministic: it matches mechanical shapes with no model call and no provider
key. Its rule classes are `unproved_fix` (a success claim whose evidence is a tool's own success
message, with no re-run of the failing command in the tail), `pipe_gated_build` (a build piped into
a text search that then chains a test run, so the suite runs whenever the search matched),
`silent_skip` (added content with an empty catch, or a skip with no stated reason), `plan_label`
(a plan-block label in added content) and `boundary_token` (a line holding one of the
operator-configured boundary patterns).

Settings live under `captainLogScreening`:

| Key | Default | Meaning |
| --- | --- | --- |
| `enabled` | `false` | Whether the screen runs. Off ships no log read at all. |
| `intervalSeconds` | `300` | Minimum seconds between sweeps; clamped to at least 30. |
| `tailLines` | `200` | Trailing lines read per mission per sweep; clamped to 20-2000. |
| `cooldownMinutes` | `30` | Minutes a flagged mission is not flagged again; clamped to at least 1. |
| `boundaryPatterns` | `[]` | Operator configuration for `boundary_token`. Empty by default, so that rule is inert until a deployment supplies its own terms. |

The whole section hot-reloads: it is merged in place, so an edit to the settings file reaches the
running screen without a restart.

### Reading the counts

Every screen that runs writes exactly one `captain.log_screen` event, whether or not anything was
found, so a clean screen is distinguishable from a screen that never ran (which writes nothing). The
payload carries `outcome` (`flagged` or `clean`), `counts` (findings per rule class), the pass names,
and the tail's SHA-256 and byte count. The tail itself is never stored. Per-class counts over a date
range are the sum of the `counts` maps on that event type in the window, read through the existing
event query.

A sweep that did no work names its reason rather than reporting an empty success: screening
disabled, the interval not elapsed, no passes registered, or no in-progress missions.

## Manual Priority Override

You can set mission priority at creation time or update it later to reprioritize work.

### At Creation Time

Using the CLI:

```bash
armada go "Fix critical login bug" --priority 1
```

Using MCP tools:

- `armada_create_mission` with the `priority` parameter
- `armada_dispatch` with the `priority` parameter

### After Creation

Using MCP tools:

- `armada_update_mission` with the `priority` parameter to change the priority of an existing pending mission

## Practical Examples

### Making a Mission Jump the Queue

A critical bug is reported while several missions are already queued. Set the priority to a low number to ensure it is picked up next:

```bash
armada go "Fix: users cannot log in after password reset" --priority 1
```

If the mission already exists, update its priority via MCP:

```
armada_update_mission(id: "msn_abc123", priority: 1)
```

The mission will be assigned to the next captain that becomes idle, ahead of all default-priority (100) missions.

### Dispatching Low-Priority Background Work

Queue up non-urgent tasks that should only run when nothing more important is waiting:

```bash
armada go "Add XML doc comments to all public methods" --priority 200
```

These missions will sit in the queue and only be assigned when no higher-priority missions are pending.

## Persona-Aware Routing

When a mission has a `Persona` field set (from a pipeline stage), the Admiral considers captain persona capabilities during assignment:

1. **Filter by AllowedPersonas:** If a captain has `AllowedPersonas` set (JSON array), only assign if the mission's persona is in the list. If `AllowedPersonas` is null, the captain can fill any role.
2. **Prefer PreferredPersona:** Among eligible captains, prefer one whose `PreferredPersona` matches the mission's persona.
3. **Fallback:** If no persona-matching captain is available, assign to any idle captain (soft constraint).

This allows dedicating specific captains to specific roles (e.g., an Opus-backed captain for Architect work, Sonnet-backed captains for Worker tasks).

### Full Scheduling Scenario

Suppose you have two captains and dispatch the following work:

| Order | Mission | Priority | Voyage |
|-------|---------|----------|--------|
| 1 | Add unit tests | 100 | vyg_testing |
| 2 | Fix typos in docs | 200 | *(none)* |
| 3 | Add rate limiting | 100 | *(none)* |
| 4 | Fix login crash | 1 | *(none)* |
| 5 | Add integration tests | 100 | vyg_testing |

Both captains are idle. Assignment proceeds as follows:

1. **Captain 1** gets "Fix login crash" (priority 1 -- lowest number wins).
2. **Captain 2** gets "Add unit tests" (priority 100, but belongs to active voyage vyg_testing, so it beats the standalone "Add rate limiting" at the same priority).
3. When a captain finishes, the next pickup is "Add integration tests" (priority 100, active voyage).
4. Then "Add rate limiting" (priority 100, standalone, created before "Fix typos").
5. Finally "Fix typos in docs" (priority 200 -- lowest priority, assigned last).

---

## Autonomous Objective Scheduler

Everything above governs **mission-level** scheduling (which pending mission an idle captain picks up next). A separate, higher-level **autonomous objective scheduler** (`AutonomousObjectiveScheduler`) governs **which objectives get dispatched into voyages at all**. It runs a periodic safety sweep and also requests a debounced immediate refill when an objective becomes ready, a dependency completes, or terminal mission or voyage work can release a lane. Event refills bypass the periodic interval and coalesce bursts. The scheduler dispatches eligible objectives up to a concurrency cap.

### Control tools (MCP)

| Tool | Purpose |
|------|---------|
| `armada_objective_scheduler_status` | Return the scheduler's runtime state: `enabled`, `paused`, limits, fair-share cursor, active voyage count, event-refill count, skip reason, sweep start/completion times, in-progress state, candidate progress, dispatch progress, bound state, and last error. No arguments. |
| `armada_objective_scheduler_set` | Enable/disable/pause or adjust the sweep. All fields optional; omitted fields are left unchanged. `enabled` (bool), `paused` (bool -- suspend without clearing `enabled`), `intervalMinutes` (int, clamped 1-1440), `maxConcurrentVoyages` (fleet-wide, clamped 1-50), `maxConcurrentVoyagesPerVessel` (default 1, clamped 1-50), `fairShareWithinPriorityBands` (bool, default false). Returns the same status snapshot. |
| `armada_mark_objective_auto_dispatchable` | Per-objective opt-in. `objectiveId` (required), `enabled` (required bool -- sets the objective's `AutoDispatchEnabled` flag), `blockedByObjectiveIds` (optional array -- objectives that must reach `Completed` before this one is eligible; omit to leave existing blockers unchanged). |
| `preview_objective_dispatch` | Run the shared read-only objective preflight. It returns all target, pipeline, captain, Check, repository, brief, and dependency findings. Optional vessel, pipeline, and captain overrides use the same evaluator as dispatch. |

### Eligibility and ordering

The sweep dispatches an objective only when it is `AutoDispatchEnabled` and the shared dispatch preview is ready. It previews candidates in dispatch order and dispatches a ready candidate before it examines later candidates. A pass stops when capacity is full, after 100 examined candidates, or after 20 seconds of candidate work. A bounded pass keeps a process-local cursor so the next pass continues after the last examined objective. A cooperative slow preview is canceled at the elapsed-time limit. The preview requires every objective in `BlockedByObjectiveIds` to reach `Completed`. It returns the complete typed dependency graph and up to 100 diagnostic paths, and it states when more paths exist. It reports a dependency cycle without hanging. Objective create and update requests reject a new cycle and name its closed path. `blockedByObjectiveIds` is the declarative, objective-level equivalent of wiring `dependsOnMissionId` by hand at dispatch time -- prefer it when you want an unattended objective graph to unblock and dispatch itself in dependency order. The scheduler will not exceed the fleet-wide `maxConcurrentVoyages` ceiling or the per-vessel `maxConcurrentVoyagesPerVessel` ceiling. Operator-dispatched linked voyages count toward both limits.

A sweep remains single-flight. A periodic or event trigger that arrives during
an active sweep requests one debounced follow-up instead of starting parallel
work. The status snapshot reports `sweepInProgress`, `lastSweepStartedUtc`,
`lastSweepCompletedUtc`, `sweepCandidateCount`, `sweepCandidatesExamined`,
`sweepDispatchedCount`, `lastSweepBoundReached`, and `lastSweepError` so a long
preflight does not look like an idle scheduler.

A row is considered only while its `Status` is `Scoped` or `Planned`. Linking a voyage promotes the row to `InProgress`, so a `Scoped` or `Planned` row that still carries `VoyageIds` is one an operator has **requeued** after those voyages ended. The sweep does not hold such a row: when every linked voyage has ended (`Complete`, `Failed`, `Cancelled`) it dispatches a new voyage and records an `objective_scheduler.requeue_after_ended_voyages` event; the old ids stay on the row as history. When a linked voyage is still `Open` or `InProgress` the row is skipped and the reason is named as `active_voyage` in `lastSkipReason`. A requeue therefore needs only the status reset — clearing `VoyageIds` by hand is not required and loses the history.

`fairShareWithinPriorityBands` is off by default. When it is on, it applies only
to objective trees whose root has a `campaign:<name>` tag. It keeps P0 ahead of
P1, P1 ahead of P2, and P2 ahead of P3. Within one priority band, it takes one
ready slice from each campaign before it takes a second slice from a campaign.
Rank and ID order stay authoritative inside each campaign. Plain objectives use
one shared default group and keep their rank and ID order. The scheduler advances
a band's cursor only after a successful dispatch. Status reports the process-local
cursor in `lastServedCampaignByPriority`; a restart begins from deterministic rank
order again.

`maxConcurrentVoyages` is a safety ceiling, not a throughput target. A lead must
keep enough verified objectives auto-enabled to use the ceiling. Prefer
independent vessels and lanes. Keep `maxConcurrentVoyagesPerVessel=1` unless
the vessel's suite and worktree policy are proven safe for concurrency.
Treat two repositories as one lane when either suite builds or tests the other
through a sibling-project reference. Do not auto-enable hardware-dependent work,
operator-only cleanup, two changes that overlap the same files, or work whose
captains would run unsafe concurrent dock-side suites. The host interlock
serializes Armada-owned Checks, but it cannot serialize a suite that a captain
runs directly in its dock.

A normal autonomous fleet can use two or three concurrent voyages when it has
that many independent writable lanes. One global voyage is a deliberate
throttle, not Armada's required operating model. Inspect both
`maxConcurrentVoyages` and the count of eligible auto-enabled objectives when
the fleet appears idle.

### Relationship to the manual loop

The objective scheduler is the server-side alternative to an operator polling
`armada_status` and dispatching unblocked work every tick. Enable it for stable
objective graphs where hands-off continuation is the goal; keep it disabled
(the default) when you want an operator to review each dispatch. Changes made
with `armada_objective_scheduler_set` are persisted to the loaded settings file
and survive an Admiral restart.

The scheduler holds no copy of its settings. It reads and writes the live
`autonomousObjectiveScheduler` settings section on every use. An edit to that
section in the settings file (picked up by the settings-file watcher or by
`POST /api/v1/settings/reload`) takes effect on the next sweep: `enabled`,
`paused` with its `pausedBy`, `pausedUtc` and `pauseReason`, `intervalMinutes`,
both concurrency ceilings and `fairShareWithinPriorityBands`. A later
`armada_objective_scheduler_set` or stale-pause clear writes the live section
back, so it keeps the edited values and changes only the fields it sets. A pause
set through the tool is written to the file, so a reload of that file and a
restart both keep the pause and its attribution. Turning
`fairShareWithinPriorityBands` off by either route drops the rotation cursor.

An objective row whose stored data cannot be read is left out of the sweep and
named in an Admiral warning. The sweep continues with the other rows. An
objective that lists the skipped row as a blocker stays blocked, because a
missing blocker counts as incomplete.

Each scheduler-dispatched voyage passes through the same check-arming service
as an operator dispatch. When its workflow profile defines them, Build and
UnitTest Checks are attached in `Pending` state and run against the produced
stage commit before a Judge PASS can land. `armada_dispatch_hold` stops new
scheduler and operator dispatches and automatic Check runs, while leaving
in-flight voyages running.

Operators maintain campaign quality, verify objectives, and delegate bounded
read-only helpers. The built-in objective scheduler and generic AgentWake are available.

Once a voyage exists, mission-level priority, voyage association, and FIFO
(above) still decide which mission a captain picks up next.
