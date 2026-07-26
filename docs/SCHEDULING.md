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

Assignment uses an atomic **TryClaim** operation to prevent race conditions when multiple captains become idle simultaneously. Only one captain can claim a given mission; if the claim fails (another captain claimed it first), the Admiral tries the next pending mission.

### What Happens When All Captains Are Busy

Missions stay in the `Pending` state until a captain finishes its current work and becomes idle. On the next heartbeat cycle after a captain frees up, the Admiral assigns the highest-priority pending mission to that captain. No missions are lost or dropped -- they simply wait in the queue.

## Heartbeat Cycle

The Admiral runs a health-check loop on a configurable interval controlled by `HeartbeatIntervalSeconds` (default: **30 seconds**). On each cycle the Admiral:

1. **Detects idle captains** -- captains that have finished their current mission.
2. **Assigns pending missions** -- matches idle captains with the highest-priority unassigned missions.
3. **Checks for stalled captains** -- captains that have not reported progress within the `StallThresholdMinutes` window (default: 10 minutes).
4. **Runs escalation rules** -- triggers recovery or alerts for stalled or failed missions.

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

Everything above governs **mission-level** scheduling (which pending mission an idle captain picks up next). A separate, higher-level **autonomous objective scheduler** (`AutonomousObjectiveScheduler`) governs **which objectives get dispatched into voyages at all** -- on a timer, without an operator in the loop. It runs a periodic sweep and dispatches eligible objectives up to a concurrency cap.

### Control tools (MCP)

| Tool | Purpose |
|------|---------|
| `armada_objective_scheduler_status` | Return the scheduler's runtime state: `enabled`, `paused`, `intervalMinutes`, `maxConcurrentVoyages`, `lastTickUtc`, `activeDispatchedCount`, `lastSkipReason`. No arguments. |
| `armada_objective_scheduler_set` | Enable/disable/pause or adjust the sweep. All fields optional; omitted fields are left unchanged. `enabled` (bool), `paused` (bool -- suspend without clearing `enabled`), `intervalMinutes` (int, clamped 1-1440), `maxConcurrentVoyages` (int, clamped 1-50). Returns the same status snapshot. |
| `armada_mark_objective_auto_dispatchable` | Per-objective opt-in. `objectiveId` (required), `enabled` (required bool -- sets the objective's `AutoDispatchEnabled` flag), `blockedByObjectiveIds` (optional array -- objectives that must reach `Completed` before this one is eligible; omit to leave existing blockers unchanged). |

### Eligibility and ordering

The sweep dispatches an objective only when it is `AutoDispatchEnabled` **and** every objective in its `BlockedByObjectiveIds` has reached `Completed`. `blockedByObjectiveIds` is the declarative, objective-level equivalent of wiring `dependsOnMissionId` by hand at dispatch time -- prefer it when you want an unattended objective graph to unblock and dispatch itself in dependency order. The scheduler will not exceed `maxConcurrentVoyages` simultaneously-active scheduler-dispatched voyages; when it is saturated or nothing is eligible, `lastSkipReason` records why the last tick dispatched nothing.

### Relationship to the manual loop

The objective scheduler is the server-side alternative to an operator polling `armada_status` and dispatching unblocked work every tick. Enable it for stable objective graphs where hands-off continuation is the goal; keep it disabled (the default) when you want an operator to review each dispatch. It does not change mission-level scheduling once a voyage exists -- priority, voyage association, and FIFO (above) still decide which mission a captain picks up next.
