# Armada Operator Guide

This document describes operational workflows for the Armada multi-agent orchestration system.

---

## Reflection Workflow (Memory Consolidation)

### Overview

Reflections is Armada's mechanism for mining completed-mission evidence into a per-vessel learned playbook. The system periodically analyzes terminal missions (Complete, Failed, Cancelled) and proposes updates to the vessel's `vessel-<repo>-learned` playbook, which auto-attaches to every dispatch on that vessel.

### Operational Flow

#### 1. Audit Drain with Auto-Dispatch

Run `armada_drain_audit_queue` as part of your normal dispatch-kernel pre-flight:

```json
// Request
{
  "limit": 10
}

// Response
{
  "entries": [
    {
      "entryId": "mrg_abc123def456ghi789jk",
      "missionId": "msn_abc123def456ghi789jk",
      "vesselId": "vsl_abc123def456ghi789jk",
      "branchName": "armada/cursor-kimi-1/msn_abc123",
      "auditLane": "standard",
      "auditCriticalTrigger": false,
      "auditConventionNotes": null,
      "isCalibration": false
    }
  ],
  "reflectionsDispatched": [
    {
      "vesselId": "vsl_abc123def456ghi789jk",
      "missionId": "msn_def456ghi789jkl012mn"
    }
  ]
}
```

The `reflectionsDispatched` array indicates vessels that exceeded their reflection threshold. Only **active vessels** are considered; inactive vessels are automatically skipped.

#### 2. Check for Completed Reflections

Query for completed reflection missions on vessels of interest:

```json
// armada_enumerate
{
  "entityType": "missions",
  "vesselId": "vsl_abc123def456ghi789jk",
  "status": "Complete"
}
```

Identify missions with `Persona = "MemoryConsolidator"` that haven't been reviewed yet.

#### 3. Review the Proposal

For a completed reflection mission:

1. **Get mission status:**
   ```json
   // armada_mission_status
   { "missionId": "msn_def456ghi789jkl012mn" }
   ```

2. **Read the AgentOutput:** Extract the `reflections-candidate` and `reflections-diff` fenced blocks.

3. **Render the diff** for fast scanning. The diff block contains:
   - `added`: New entries proposed
   - `removed`: Entries to remove
   - `merged`: Entries being combined
   - `unchangedCount`: Number of untouched entries
   - `evidenceConfidence`: "high", "mixed", or "low"
   - `notes`: Summary of what changed and why

4. **For non-trivial diffs:** Read the candidate verbatim and sanity-check against the current playbook.

#### 4. Accept or Reject

**Accept Verbatim:**
```json
// armada_accept_memory_proposal
{ "missionId": "msn_def456ghi789jkl012mn" }
```

**Accept with Edits:**
```json
// armada_accept_memory_proposal
{
  "missionId": "msn_def456ghi789jkl012mn",
  "editsMarkdown": "# vessel-myrepo-learned\n\n[Your edited content here]"
}
```

The orchestrator-edited version is applied, NOT the captain's version. This mirrors the surgical-fix pattern from standard mission workflows.

**Reject:**
```json
// armada_reject_memory_proposal
{
  "missionId": "msn_def456ghi789jkl012mn",
  "reason": "The proposed pattern is too specific to the otrbuddy vessel and doesn't generalize"
}
```

The rejection reason is recorded and fed into the next reflection's brief so the consolidator doesn't re-propose the same thing.

#### 5. Review in Same Operator Pass

**CRITICAL:** Reflection proposals get reviewed in the **same pass** as the audit queue. When `armada_drain_audit_queue` returns `reflectionsDispatched` entries OR a previously-dispatched reflection mission has reached `Complete`, review and accept/reject the proposal in the **same session** that drained the audit queue -- never deferred to "later".

A reflection sitting un-reviewed gates the next reflection on that vessel (concurrency rule), so deferring breaks the feedback loop.

### Threshold Configuration

| Setting | Default | How to Configure |
|---------|---------|----------------|
| `DefaultReflectionThreshold` | 15 missions | Admiral options (env / config file) |
| `Vessel.ReflectionThreshold` | NULL (use default) | `armada_update_vessel` |

**Configure per-vessel threshold:**
```json
// armada_update_vessel
{
  "vesselId": "vsl_abc123def456ghi789jk",
  "reflectionThreshold": 10
}
```

Set to a lower value for high-activity vessels, higher for quiet repositories.

### Manual Trigger

For immediate consolidation (outside the audit-drain flow):

```json
// armada_consolidate_memory
{
  "vesselId": "vsl_abc123def456ghi789jk",
  "instructions": "Focus on test patterns discovered in recent missions",
  "tokenBudget": 300000
}
```

Returns immediately with the dispatched mission ID. The mission runs asynchronously; poll `armada_mission_status` to detect completion.

### Concurrency Behavior

- **Per-vessel limit:** At most ONE pending/running reflection mission per vessel.
- **Duplicate calls:** If `armada_consolidate_memory` is called while one is in-flight, returns the existing mission ID.
- **Mission visibility:** The consolidator only sees terminal missions (`Complete`, `Failed`, `Cancelled`). In-progress missions are excluded from the evidence bundle.
- **Vessel activity:** Auto-dispatch only considers active vessels. Inactive vessels are skipped even if they exceed the threshold.

### Common Operations

**Check vessel reflection state:**
```json
// armada_get_vessel
{ "vesselId": "vsl_abc123def456ghi789jk" }

// Look for:
// - reflectionThreshold (custom or null)
// - lastReflectionMissionId (pointer to most recent accepted reflection)
```

**List recent reflections on a vessel:**
```json
// armada_enumerate
{
  "entityType": "missions",
  "vesselId": "vsl_abc123def456ghi789jk",
  "pageSize": 10
}
// Filter for Persona = "MemoryConsolidator" in results
```

**Force re-consolidation from a specific point:**
```json
// armada_consolidate_memory
{
  "vesselId": "vsl_abc123def456ghi789jk",
  "sinceMissionId": "msn_olderabc123def456ghi",
  "instructions": "Re-analyze from March checkpoint"
}
```

### Error Handling

| Scenario | Response | Action |
|----------|----------|--------|
| Proposal already accepted/rejected | `proposal_already_processed` | No action needed; already handled |
| Mission not a reflection | `mission_not_a_reflection` | Verify mission ID; may be a regular mission |
| Output contract violation | `output_contract_violation` | Reject the proposal; next drain will re-dispatch |
| No evidence available | `no_evidence_available` | Vessel has no terminal missions since last reflection |
| Reflection already in flight | `reflection_already_in_flight` | Wait for existing mission; ID returned in payload |

### Best Practices

1. **Review promptly:** Don't let completed reflections sit un-reviewed. They block new reflections on the same vessel.

2. **Edit surgically:** When accepting with edits, only change what's necessary. Preserve the captain's structure and formatting.

3. **Explain rejections:** Provide clear, specific rejection reasons. They directly improve the next reflection attempt.

4. **Monitor thresholds:** Adjust `ReflectionThreshold` per vessel based on activity. High-traffic repositories may benefit from more frequent, smaller consolidations.

5. **Check confidence:** Review the `evidenceConfidence` field in the diff. "low" confidence indicates the consolidator was uncertain -- consider manual review or rejection with guidance.

6. **Inactive vessel handling:** If you need reflections on an inactive vessel, manually trigger with `armada_consolidate_memory` rather than relying on auto-dispatch.

## Identity-Curate Operator Workflow (Reflections v2-F2)

Persona-curate and captain-curate add cross-vessel identity-pinned
learned-notes playbooks. Operator workflow extends the v1 review loop:

1. **Audit-drain triggers** identity-curate auto-dispatches alongside
   the per-vessel triggers. Look for `mode: "persona-curate"` and
   `mode: "captain-curate"` entries in `reflectionsDispatched[]`. Each
   carries `personaName` or `captainId` instead of `vesselId`.

2. **Manual dispatch** for re-evaluation without new evidence: call
   `armada_consolidate_memory` with `personaName` (or `captainId`) and
   `mode`. Manual dispatches bypass the audit-drain anti-thrash gate;
   the brief still surfaces existing notes plus rejection history.

3. **Captain-curate fan-out** is gated by
   `ArmadaSettings.AllowCaptainCurateFanOut` (default false). Enabling
   it dispatches one captain-curate per active captain on a single
   `armada_consolidate_memory(captainId: null)` call -- this can be
   expensive with a 25+ captain pool and risks correlated bias, so
   prefer single-target dispatches in normal operation.

4. **Counter-evidence checks at accept time** are the bias-correction
   loop. When the prior identity playbook had confidence-tagged notes,
   the new candidate MUST disable or weaken any contradicted notes;
   otherwise accept fails with
   `persona_curate_ignored_counter_evidence` (or the captain variant).
   Reject with a specific reason rather than `editsMarkdown`-overriding
   so the next cycle's brief surfaces the gap.

5. **Vessel conflict warnings** are non-blocking: identity notes
   resembling vessel-learned content surface as
   `identity_note_conflicts_vessel` warnings. Decide whether the
   identity override is intentional (accept), redundant (reject), or
   needs editing (`editsMarkdown`). The Jaccard threshold and vessel
   sample size are tunable in `ArmadaSettings`.

6. **Identity playbooks attach automatically** through the three-way
   merge in `AdmiralService.PersistMissionPlaybooksAsync`
   (vessel -> persona -> captain). Persona-pinned content lights up
   on every mission running that persona; captain-pinned content
   lights up the next time the captain is assigned. No manual
   `selectedPlaybooks` editing is required.

7. **Threshold tuning:** start with `Persona.CurateThreshold = 30`
   for high-throughput personas (Architect, Worker) and leave others
   null. Captain thresholds should be sparse -- enable per-captain
   only when behavior signal is strong enough to justify the dispatch
   cost.

## Fleet-Curate Operator Workflow (Reflections v2-F3)

Fleet-curate is the seventh `armada_consolidate_memory` mode and the
last link in the v1 -> F4 -> F1 -> F2 -> F3 reflections chain. It
promotes facts shared across vessels in a fleet to a fleet-pinned
learned playbook with explicit `disableFromVessels` ripples.

1. **Audit-drain auto-trigger** runs after the F2 captain-curate pass.
   For each active fleet with a non-null `Fleet.CurateThreshold`, the
   drain counts terminal missions across ACTIVE vessels in the fleet
   since the last accepted fleet-curate; the anti-thrash gate ALSO
   requires at least one consolidate / consolidate-and-reorganize
   accept event on a member vessel since that timestamp (without fresh
   vessel-learned acceptances, promotion candidates haven't moved).
   Successful auto-dispatches surface as
   `{fleetId, missionId, mode:"fleet-curate"}` in
   `reflectionsDispatched[]`.

2. **Manual dispatch** when anti-thrash suppresses the auto-trigger or
   you want a fresh re-evaluation: call
   `armada_consolidate_memory(fleetId, mode: "fleet-curate")`. Manual
   dispatches bypass anti-thrash. Pass `fleetId: null` for cross-fleet
   fan-out across active fleets.

3. **Vessel-fleet conflict detection is BLOCKING** at accept time per
   spec Q3. When a candidate fleet entry's 3-gram Jaccard similarity
   to existing vessel-learned content exceeds
   `FleetVesselConflictThreshold` (default 0.7) AND sentiment polarity
   disagrees, accept fails with `fleet_curate_vessel_conflict`. The
   `editsMarkdown` override is the safety valve when the consolidator
   is right and the vessel content is stale; otherwise REJECT with a
   reason and let the next cycle's brief surface the gap.

4. **Promotion gates are strict.** Each new fleet entry MUST list at
   least 2 contributing vessels (else
   `fleet_promotion_insufficient_vessels`) and at least 3 supporting
   missions across those vessels (else
   `fleet_promotion_insufficient_missions`). Single-vessel facts
   belong in vessel-curate (consolidate); reject the proposal and run
   `armada_consolidate_memory(vesselId, mode: "consolidate")` instead.

5. **Ripple disables** retire the original vessel-scope copy when a
   fact is promoted. The candidate's JSON sidecar lists
   `disableFromVessels: [{vesselId, noteRef, reason}]` per ripple;
   accept applies them transactionally as `[disabled: <reason>]`
   marker prefixes on the matching vessel-learned note line. The
   marker preserves history so future fleet-curate cycles see the
   disable trail. `noteRef` shape is `<Section>:<index>` (e.g.
   `Project conventions:0`).

6. **Cross-vessel suggestions in vessel-curate briefs** are passive
   nudges. When a vessel-curate (consolidate) brief is assembled, the
   dispatcher scans sibling vessels' learned playbooks and surfaces
   facts whose 3-gram Jaccard similarity exceeds
   `CrossVesselSuggestionThreshold` (default 0.5) as a
   "Cross-vessel suggestion" hint section. The captain may mention
   the suggestion in its diff `notes` paragraph or include a
   `[CLAUDE.MD-PROPOSAL]` block; vessel-curate accept semantics are
   unchanged. The actual promotion path is fleet-curate.

7. **Four-way merge attaches fleet content automatically.**
   `AdmiralService.PersistMissionPlaybooksAsync` layers
   `fleet -> vessel -> persona -> captain` with fleet appearing first
   (least specific). No manual `selectedPlaybooks` editing required.

8. **Threshold tuning:** for the typical multi-repo project setup
   (3-5 vessels in one fleet), start with `Fleet.CurateThreshold = 50`
   so the auto-trigger fires after roughly two vessel-curate cycles
   per member vessel. Leave the threshold null for fleets where
   cross-vessel sharing is unlikely (single-repo projects, isolated
   experimental repos).

9. **Cross-fleet fan-out** is supported via
   `armada_consolidate_memory(fleetId: null, mode: "fleet-curate")`
   for multi-fleet admiral instances. Single-fleet setups (the common
   case) see this dispatch as a single fleet-curate. The
   `dual_judge_fan_out_starvation_risk` warning emits at N > 3 fleets
   with `dualJudge=true`.

10. **`armada_get_fleet` and `armada_update_fleet` extensions** carry
    the F3 fields: `defaultPlaybooks`, `learnedPlaybookId`, and
    `curateThreshold`. The `learnedPlaybookId` is read-only through
    the update tool -- the accept path lazy-creates the playbook on
    the first successful fleet-curate and writes the FK back to the
    fleet row.
