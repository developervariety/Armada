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

---

## Reflections v2-F4 (Reorganize Mode)

**Status:** Shipped

F4 extends v1's MCP surface with a `mode` parameter, optional dual-Judge review, cross-vessel fan-out, audit-drain reorganize auto-trigger, and quality-metric event payloads. v1's persona, pipeline, and output contract are unchanged.

### Three Modes

| Mode | Purpose | Brief shape | Default tokenBudget |
|------|---------|-------------|---------------------|
| `consolidate` (default) | v1 behavior: distill new evidence into the playbook. New facts allowed. | Full v1 evidence bundle. | 400000 |
| `reorganize` | Restructure the playbook (group, merge near-duplicates, drop stale entries, reorder, reword). New facts forbidden. | Current playbook + last 20 commit subjects + reorganize-mode rejection feedback + reorganize-specific constraints. No evidence bundle. | 30000 |
| `consolidate-and-reorganize` | Combined: mine new facts AND restructure in one mission. | Full v1 evidence bundle + reorganize instructions + recent commit subjects. | 400000 |

A missing or unrecognized `mode` returns the new error `invalid_mode`.

### Optional Dual-Judge

Pass `dualJudge: true` to dispatch on the `ReflectionsDualJudge` pipeline (Worker -> two parallel Judge siblings) instead of `Reflections`. At accept time the orchestrator's `armada_accept_memory_proposal` requires both Judge sibling missions to have produced PASS verdicts; otherwise it returns `dual_judge_not_passed` with the verdict details. `editsMarkdown` overrides this gate just as it overrides the parser.

`dualJudge: false` (default) keeps the v1 single-stage `Reflections` pipeline.

### Cross-Vessel Fan-Out

`armada_consolidate_memory(vesselId: null, mode: "reorganize")` enumerates active vessels and dispatches a reorganize mission per vessel that has a populated `vessel-<repo>-learned` playbook with no in-flight MemoryConsolidator. Skipped vessels appear in the response under `skipped[]` with reasons `in_flight`, `no_playbook`, or `too_small`. Fan-out is only valid with `mode: "reorganize"` -- calling with `null` vesselId on `consolidate` or `consolidate-and-reorganize` returns `vesselId_required`. `dualJudge` propagates to every dispatched mission.

### Audit-Drain Auto-Trigger and Anti-Thrash

When a vessel sets `Vessel.ReorganizeThreshold` (a token-count threshold; null disables), `armada_drain_audit_queue` evaluates the current learned playbook size against that threshold after the existing consolidate-threshold check. If exceeded and the vessel has no in-flight MemoryConsolidator, the drain auto-fires a reorganize mission. To prevent thrash, the trigger skips when the most recent reorganize-mode `reflection.accepted` payload's `appliedContentLength` has not grown by `ReorganizeAntiThrashGrowthRatio` (default 0.10) AND has not gained `ReorganizeAntiThrashMinNewEntries` worth of new content (default 5).

`reflectionsDispatched[]` entries now carry a `mode` field (`"consolidate"` or `"reorganize"`); existing callers ignoring the field continue to work.

### Quality Metrics

Every `reflection.accepted` event payload now includes:

| Field | Always populated? | Notes |
|-------|------------------|-------|
| `mode` | Yes | Wire string of the mission mode. |
| `dualJudge` | Yes | Whether dual-Judge was requested at dispatch. |
| `entriesBefore` / `entriesAfter` | Yes | Bullet count in the pre/post playbook. |
| `tokensBefore` / `tokensAfter` | Yes | Approx 4-chars-per-token estimate. |
| `removed` / `merged` | Reorganize/combined | Counts from the diff JSON. 0 in pure consolidate. |
| `addedFromReorganize` | Reorganize/combined | Reorganize-mode count from diff `added`; -1 in combined mode (not attributable). |
| `judgeVerdicts` | dualJudge=true | Per-Judge-sibling list of `{missionId, captainId, verdict}`. |

Surface these via `armada_enumerate(entityType: "events", eventType: "reflection.accepted")`.

### F4 MCP Errors

In addition to v1's errors, F4 introduces:

| Error | When |
|-------|------|
| `invalid_mode` | `mode` value not in the enum. |
| `vesselId_required` | `vesselId: null` with non-reorganize mode. |
| `playbook_empty` | Single-vessel reorganize on a vessel whose learned playbook still contains the bootstrap "No accepted reflection facts yet" template. |
| `playbook_too_small` | Single-vessel reorganize on a populated playbook below `ArmadaSettings.ReorganizePlaybookMinCharacters` (default 200). |
| `reorganize_added_facts` | Accept-time soft validation: reorganize diff `added` array contains a non-structural entry. Details list the offending entries. Override with `editsMarkdown`. |
| `reorganize_invalid_merge_source` | Accept-time soft validation: a `merged.from` entry was not present in the pre-update playbook. |
| `reorganize_invalid_remove` | Accept-time soft validation: a `removed` entry was not present in the pre-update playbook. |
| `dual_judge_not_passed` | Accept-time gate: at least one Judge sibling did not emit `[ARMADA:VERDICT] PASS`. Details echo the verdicts. |

### Settings

| Setting | Default | Where |
|---------|---------|-------|
| `DefaultReorganizeTokenBudget` | 30000 | `ArmadaSettings` |
| `ReorganizePlaybookMinCharacters` | 200 | `ArmadaSettings` |
| `ReorganizeAntiThrashGrowthRatio` | 0.10 | `ArmadaSettings` |
| `ReorganizeAntiThrashMinNewEntries` | 5 | `ArmadaSettings` |
| `Vessel.ReorganizeThreshold` | NULL (auto-trigger disabled) | `armada_update_vessel` |

### Non-Goals (F4 Scope)

- No schedule-based reorganize (cron/time-of-day triggers); audit-drain auto-trigger is the only scheduled path.
- No reorganize on vessels other than `vessel-<repo>-learned`.
- No cross-playbook restructuring.
- No auto-accept of reorganize proposals -- the orchestrator remains the final decision authority even when dual-Judge passes.

## Reflections v2-F1 (Pack-Curate Mode)

F1 extends `armada_consolidate_memory` with a fourth mode (`pack-curate`)
that mines completed-mission captain logs for pack-usage signals
(prestaged-files Read / ignored / grep-discovered / Edited) and
proposes deltas to a new `vessel_pack_hints` table. `armada_context_pack`
consults the table at dispatch time as a hard pre-selection pass.

### Modes (post-F1)

| Mode | Behavior |
|------|----------|
| `consolidate` | v1: mine evidence, propose updated learned-facts playbook. |
| `reorganize` | v2-F4: restructure learned playbook without adding facts. |
| `consolidate-and-reorganize` | v2-F4: combined evidence+restructure pass. |
| `pack-curate` | v2-F1: mine pack-usage evidence; propose `vessel_pack_hints` deltas. **No playbook edits.** |

### Hint Structure

`vessel_pack_hints` rows:
- `id` (`vph_` prefix)
- `vessel_id`
- `goal_pattern`: case-insensitive regex applied to the dispatch goal text.
- `must_include`: JSON array of glob paths (Microsoft.Extensions.FileSystemGlobbing).
- `must_exclude`: JSON array of glob paths.
- `priority`: integer; higher applied first. Equal-priority conflicts resolve to **exclude wins**.
- `confidence`: `high` | `medium` | `low`.
- `source_mission_ids`: JSON array (traceability).
- `justification`: free-text rationale.
- `active`: soft-disable flag.

### Cross-Vessel Fan-Out

`armada_consolidate_memory(vesselId: null, mode: "pack-curate")` mirrors
F4's reorganize fan-out. Skips vessels with reason `in_flight` (existing
MemoryConsolidator mission), `no_pack_evidence` (no terminal-mission
evidence in window). When `dualJudge: true` AND fan-out dispatches more
than `PackCurateDualJudgeFanOutWarnThreshold` (default 3) vessels, the
response includes a `dual_judge_fan_out_starvation_risk` warning string.

### Pack-Time Application

`armada_context_pack` reads matching active hints (regex match on goal
text), applies hard `mustInclude` and `mustExclude` before lexical
ranking, and returns `matchedHintIds` + `warnings` in the response.
Empty `vessel_pack_hints` table preserves prior behavior.

Conflict resolution: higher priority wins; equal priority => exclude wins.

### Validation Pipeline (Accept Time)

`armada_accept_memory_proposal` runs (in this order, all bypassed by
`editsMarkdown` override):

1. JSON parse of reflections-candidate as `PackCurateCandidate`.
2. Anti-pattern checks: `pack_hint_pattern_too_broad` (`.*`, empty,
   < 3 chars), `pack_hint_invalid_regex`, `pack_hint_invalid_path`,
   `pack_hint_id_not_found` (modify/disable references unknown id).
3. Dual-Judge gate when `dualJudge=true`.
4. Path-existence validation: best-effort `git ls-tree -r <default-branch>`
   against `Vessel.LocalPath`. Unmatched globs surface as **non-blocking**
   `pack_hint_no_matches` warnings.
5. Conflict detection: hint pairs with overlapping pattern shape AND
   overlapping mustInclude/mustExclude on the same path surface as
   **non-blocking** `pack_hint_conflict` warnings.
6. Apply add/modify/disable in sequence (each operation is independently
   idempotent on retry).

### Audit-Drain Auto-Trigger

`armada_drain_audit_queue` evaluates `Vessel.PackCurateThreshold` after
consolidate-threshold and reorganize-threshold checks. Fires when:
- threshold is set and > 0,
- terminal-mission count since last accepted pack-curate exceeds threshold,
- no MemoryConsolidator is in-flight,
- anti-thrash gate passes (at least one terminal mission since last accept
  has non-empty `filesGrepDiscovered`).

`reflectionsDispatched[]` entries carry `mode: "pack-curate"`.

### F1 MCP Errors

| Error | Trigger |
|-------|---------|
| `no_pack_evidence_available` | Single-vessel pack-curate with zero terminal missions in the window. |
| `pack_hint_pattern_too_broad` | `goalPattern` is `.*`, empty, whitespace, or < 3 chars. |
| `pack_hint_invalid_regex` | `goalPattern` fails to compile. |
| `pack_hint_id_not_found` | `modifyHints` or `disableHints` references an unknown id on the vessel. |
| `pack_hint_invalid_path` | `mustInclude` / `mustExclude` entry is null/whitespace. |

### F1 Settings

| Setting | Default | Where |
|---------|---------|-------|
| `DefaultPackCurateTokenBudget` | 400000 | `ArmadaSettings` |
| `PackCurateInitialWindow` | 25 | `ArmadaSettings` |
| `PackHintConflictPriorityMargin` | 50 | `ArmadaSettings` |
| `PackCurateDualJudgeFanOutWarnThreshold` | 3 | `ArmadaSettings` |
| `Vessel.PackCurateThreshold` | NULL (auto-trigger disabled) | `armada_update_vessel` |

### Non-Goals (F1 Scope)

- No A/B testing / shadow mode for hints.
- No cross-vessel hint promotion (F3 territory).
- No dedicated hint-editing MCP tool -- all mutations go through the
  reflection -> orchestrator review path.
- No auto-disable of stale hints without orchestrator review.
