# Armada Capabilities

This document describes the high-level capabilities of the Armada multi-agent orchestration system.

---

## Reflections v1 (Memory Consolidation)

**Status:** Shipped

Reflections mines completed-mission evidence into a per-vessel `vessel-<repo>-learned` playbook that auto-attaches to every dispatch on that vessel. This replaces manual playbook curation with a periodic async pipeline that proposes the next version of the playbook for orchestrator review.

### Overview

Every Armada mission brief restates the same things: codebase facts captains keep rediscovering, lessons from prior mission outcomes flagged by reviewers, and audit-queue follow-ups. Reflections automatically consolidates this accumulated session evidence into a maintained playbook that improves future briefs without manual editing.

### Key Components

| Component | Description |
|-----------|-------------|
| `MemoryConsolidator` persona | New reviewer-class persona with read-only access to mission logs/diffs and write-only access to its own AgentOutput. Cannot edit code or CLAUDE.md. |
| `Reflections` pipeline | Single-stage pipeline (MemoryConsolidator only) that outputs the proposal for orchestrator review. |
| `armada_consolidate_memory` | Manual trigger to dispatch a consolidation mission. |
| `armada_accept_memory_proposal` | Apply the captain's proposed playbook content (with optional orchestrator edits). |
| `armada_reject_memory_proposal` | Reject the proposal with a reason that feeds into the next reflection. |
| `armada_drain_audit_queue` | Extended to auto-dispatch reflections when vessels exceed their mission threshold. |
| Per-vessel learned playbook | Auto-registered in `DefaultPlaybooks` on bootstrap. Only `armada_accept_memory_proposal` writes to it. |

### Trigger Flow

**Manual Trigger:**
```
orchestrator -> armada_consolidate_memory(vesselId, sinceMissionId?, instructions?, tokenBudget?)
             -> returns { missionId, voyageId }
             -> consolidator captain runs (minutes)
             -> mission completes; AgentOutput holds candidate + diff
             -> orchestrator reviews diff
             -> armada_accept_memory_proposal(missionId, editsMarkdown?) OR
               armada_reject_memory_proposal(missionId, reason)
```

**Audit-Queue Auto-Dispatch:**
```
orchestrator -> armada_drain_audit_queue
             -> admiral processes audit entries
             -> admiral checks each vessel: missionsSinceLastReflection >= threshold
             -> if yes, dispatches consolidation missions automatically
             -> drain response includes:
                {
                  entries: [{entryId, missionId, vesselId, branchName, auditLane, auditCriticalTrigger, auditConventionNotes, isCalibration}, ...],
                  reflectionsDispatched: [{vesselId, missionId}, ...]
                }
```

### Threshold Settings

| Setting | Default | Configurable Via |
|---------|---------|------------------|
| `DefaultReflectionThreshold` | 15 missions | Admiral options (env / config) |
| `Vessel.ReflectionThreshold` | NULL (uses default) | `armada_update_vessel` |
| `InitialReflectionWindow` | 100 missions | Admiral options |
| `tokenBudget` | 400k tokens | Per-call MCP arg |

### Concurrency Rules

- **At most ONE** pending/running reflection mission per vessel.
- If `armada_consolidate_memory` is called while one is in-flight, returns the existing mission ID rather than dispatching a duplicate.
- The consolidator only sees missions whose status is terminal (`Complete`, `Failed`, `Cancelled`). Missions still `InProgress` are excluded from the evidence bundle.
- Audit-drain auto-dispatch only considers **active vessels**. Inactive vessels are skipped.

### Output Contract

The consolidation mission's `AgentOutput` MUST contain exactly two fenced blocks:

```reflections-candidate
<full proposed playbook content, ready to drop into the playbook table -- markdown, no front-matter>
```

```reflections-diff
{
  "added": ["entry-key-or-bullet-summary", ...],
  "removed": ["entry-key-or-bullet-summary", ...],
  "merged": [{"from": ["..."], "to": "..."}, ...],
  "unchangedCount": N,
  "evidenceConfidence": "high" | "mixed" | "low",
  "notes": "free-form one-paragraph summary of what changed and why"
}
```

Text outside the two fenced blocks is ignored. Multiple blocks of the same name are treated as malformed.

### Per-Vessel Learned Playbook

On vessel bootstrap, Armada creates a `vessel-<repo>-learned` playbook and appends it to the vessel's `DefaultPlaybooks`. This playbook:

- Starts with template content indicating no accepted reflections yet.
- Is updated only via `armada_accept_memory_proposal`.
- Auto-attaches to every dispatch on that vessel (via DefaultPlaybooks merge).
- Captains never edit it directly (they cannot write to playbook records).

### Schema Additions

The following vessel properties support reflections:

| Property | Type | Description |
|----------|------|-------------|
| `LastReflectionMissionId` | string | Pointer to the most recently accepted reflection mission. Drives "missions since last reflection" queries. |
| `ReflectionThreshold` | integer | Per-vessel override of the default mission-count threshold. NULL means use admiral default. |

### Non-Goals (v1 Scope)

The following are explicitly out of scope for Reflections v1:

- No CLAUDE.md edits from the consolidator (stays in `[CLAUDE.MD-PROPOSAL]` flow).
- No code changes from the consolidator (output is playbook content only).
- No cross-vessel reflection (each vessel's playbook is independent).
- No persona-/captain-scoped memory.
- No context-pack curation.
- No multi-model dual-reflection.
- No auto-pruning by playbook size.
- No reflection-of-reflections.

### Failure Modes

| Failure | Handling |
|---------|----------|
| Consolidator produces garbage / misses output contract | `armada_accept_memory_proposal` returns `output_contract_violation`. Orchestrator rejects; next drain re-dispatches. |
| Consolidator hallucinates a fact not in evidence | Caught at orchestrator review (read the diff). Rejection reason feeds forward. |
| Two reflections accepted in close succession | Each updates `LastReflectionMissionId`; second only sees missions after the first. No double-counting. |
| Zero new missions when threshold check runs | Auto-trigger no-ops; manual trigger returns `no_evidence_available`. |
| Reflection mission times out / crashes | Standard mission-failure path; orchestrator can restart or wait for next drain. |
| Concurrent dispatches race with reflection | Reflection has its own dock; dispatch concurrency rules apply. Other missions can run concurrently on the same vessel. |

### Core Rule

Reflection proposals get reviewed in the same pass as the audit queue. When `armada_drain_audit_queue` returns `reflectionsDispatched` entries OR a previously-dispatched reflection mission has reached `Complete`, the orchestrator reviews and accepts/rejects the proposal in the SAME session that drained the audit queue -- never deferred to "later". A reflection sitting un-reviewed gates the next reflection on that vessel (concurrency rule), so deferring breaks the feedback loop.
