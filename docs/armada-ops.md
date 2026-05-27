# Armada Operator Guide

This document describes operational workflows for the Armada multi-agent orchestration system.

---

## Current Operator Notes

Last upstream sync: upstream `e9e3021f` via merge `cb9030bd` on 2026-05-24. The fork now includes upstream v0.8.0 delivery-management surfaces plus fork orchestration features.

For non-trivial work, start from an objective/backlog item and keep the evidence there. Use objective refinement or Planning for fuzzy scope, Workspace/context packs for file-grounded scope, and structured check runs for repeatable validation. Dispatch missions with objective IDs, selected playbooks, expected checks, and explicit file boundaries.

Armada should be the system of record for autonomous operation. Do not build parallel TODO/spec/memory-note workflows when an Armada surface exists:

- Objectives and Backlog are the durable work queue, including nightly or unattended continuation candidates.
- Planning Sessions replace ad-hoc markdown specs for interactive refinement and plan-to-dispatch handoff.
- Workflow Profiles define commands; Pending Checks define gates; check execution provides the evidence.
- Releases, Deployments, Incidents, and Runbooks own operational progress and recovery.
- History and Requests own replay/audit evidence.
- External resume mechanisms such as AgentWake are transports, not the work queue. Autonomous rescue and continuation loops should read Armada records first.
- Autonomous mission recovery is server-side Armada policy. Failed and landing-failed missions are classified, linked to an Incident, and recorded through the system `Autonomous Mission Recovery` runbook. Recoverable non-landing mission failures receive a bounded linked rescue mission. Cancellation is authoritative: cancelled parent voyages suppress new rescue work and cancel active linked rescues. Landing/merge failures stay owned by landing and merge recovery workflows. Auth/quota/review/protected-path/dependency/recovery-exhausted failures stay as open incidents for human review.
- Incident lifecycle is also server-side Armada policy. Failed checks open incidents with the failed `chk_` link attached. Later passing matching check evidence, successful rescue missions, shipped releases, successful verified deployments, completed rollbacks, or superseding cancellation/shipping evidence move incidents through `Mitigated`, `RolledBack`, and `Closed` after the quiet window. New matching failures reopen mitigated incidents and raise severity rather than hiding regressions.

Use these upstream surfaces when they fit:

- `list_objectives`, `create_objective`, `update_objective`, and backlog aliases for durable scope, priority, acceptance criteria, rollout constraints, and evidence links.
- `run_check`, `get_check_run`, and `retry_check_run` for build/test/deploy validation records instead of only mission logs. Pending check records are durable gates; the server heartbeat auto-runs eligible non-deployment gates when linked voyages/missions complete, releases become ready, or vessels are idle. Deployment-linked gates are resolved by `DeploymentService` during deploy, verify, rollback, and rollout-monitoring actions.
- `create_release`, `create_deployment`, deployment approval/verify/rollback tools, and runbook tools for release and operations work.
- Workspace, request history/API Explorer, history timeline, GitHub evidence, and captain tool visibility from the dashboard when investigating or resuming work.
- Pipeline review gates for human checkpoints; approve or deny via mission detail or `/api/v1/missions/{id}/review/*` before merge queue/audit/PR fallback.

### Autonomous Recovery Policy

The server heartbeat and mission-outcome hook run `AutonomousRecoveryOrchestrator` when `settings.autonomousRecovery.enabled` is true. The default policy is intentionally bounded:

- Live captains that go quiet before the hard stall threshold receive a throttled `Mail` nudge with an `ARMADA_AUTO_NUDGE` marker. Mail is only for live work; terminal failed missions get rescue records instead.
- Terminal `Failed` and `LandingFailed` missions create or update an Incident with vessel/mission/voyage context.
- Armada creates a runbook execution against the system `Autonomous Mission Recovery` runbook so the decision is visible in Runbooks and History.
- Recoverable non-landing failures dispatch standalone rescue missions with `ParentMissionId` set to the failed mission. The default budget is one rescue; raising `maxMissionRecoveryAttempts` allows another rescue only after prior rescue missions have reached a retryable terminal failure.
- Cancelled missions and voyages do not create new rescue missions. If a linked rescue is still active when the parent voyage is cancelled, Armada cancels the rescue too.
- A captain marked Working on an active mission with no recorded process id is treated as a fail-loud recovery case. Armada fails the mission with an explicit missing-PID reason, halts the voyage, releases the captain, and records incident evidence instead of treating the missing process as success.
- Landing and merge failures do not receive generic rescue missions; those remain under landing, merge-queue, and merge-recovery ownership.
- Serious blockers do not dispatch rescue work. They remain open incidents for human action.

Tune this with `autonomousRecovery.dispatchRescueMissions`, `maxMissionRecoveryAttempts`, `failedMissionLookbackHours`, `sendStallMailNudges`, `stallMailNudgeThresholdRatio`, and `stallMailNudgeCooldownMinutes`.

### Incident Lifecycle Policy

The server heartbeat and mission-outcome hook run `IncidentLifecycleOrchestrator` when `settings.incidentLifecycle.enabled` is true. The policy is evidence-gated:

- Failed automatic checks create incidents linked to the exact failed `CheckRunId`.
- A newer matching passed check for the same vessel/context/type mitigates the incident.
- A completed linked rescue mission mitigates the failed-mission incident that spawned it.
- A verified successful deployment or shipped release mitigates linked delivery incidents.
- A completed rollback moves linked incidents to `RolledBack`.
- A later matching passing check for the same vessel can close stale infrastructure-blocked check incidents, including stale Docker/setup failures that were superseded by later shipped work.
- Cancelled/superseded mission evidence can close stale rescue and landing incidents once the linked work is no longer actionable.
- Mitigated incidents close automatically only after `closeQuietPeriodMinutes` and no newer linked failure evidence.
- Newer matching failures reopen `Mitigated`/`Monitoring` incidents to `Open` and raise severity to at least `High`.

Tune this with `incidentLifecycle.enabled`, `autoMitigate`, `autoClose`, `closeQuietPeriodMinutes`, and `maxIncidentsPerSweep`. Do not manually close incidents just because a mission says "done"; close them by producing the linked check, rescue, release, deployment, or rollback evidence Armada can read.

## Structured-First Operating Contract

Treat the dashboard sections as first-class records with ownership boundaries:

| Surface | Use it for | Do not pack this into |
|---------|------------|-----------------------|
| Backlog/Objectives | Scope, acceptance criteria, priority, rollout constraints, evidence links | Mission prose, release notes |
| Planning | Fuzzy discovery, transcript-backed decomposition, dispatch handoff | Ad-hoc chat summaries |
| Workflow Profiles | Build/test/package/release/deploy/rollback/verify command definitions | Per-mission shell instructions |
| Checks | Repeatable validation runs, logs, artifacts, parsed test/coverage summaries, pending gates auto-resolved from lifecycle state | Mission final comments |
| Environments | Named rollout targets, URLs, approval policy, access/deployment rules | Deployment notes only |
| Releases | Candidate/shipped work, version/tag, linked voyages/missions/checks/artifacts | Voyage descriptions |
| Deployments | Rollout approval, execution, verification, monitoring, rollback | Incident notes only |
| Incidents | Impact, root cause, recovery, rollback, hotfix handoff, postmortem | Dispatch prompts |
| Runbooks | Repeatable operational steps and execution history | Freeform operator memory |
| History/Requests | Cross-entity chronology and API/request evidence | Manual status summaries |

Default flow for meaningful work:

1. Find or create an objective/backlog item.
2. Scope with Planning, objective refinement, Workspace, context packs, and graph tools.
3. Confirm or create the workflow profile and environments needed for checks, releases, and deployments.
4. Dispatch with `objectiveId`, pipeline/playbook selections, expected checks, and tight file boundaries.
5. Record validation with `run_check`/Checks, not just mission logs. Create Pending gates early; Armada resolves eligible non-destructive gates automatically, and deployment actions consume matching Pending deploy/verify/rollback checks.
6. Create release and deployment records when work is a candidate for shipping or has rolled out anywhere.
7. Create incidents from affected deployments/environments when regressions occur.
8. Start runbook executions for repeatable release, deploy, rollback, migration, or incident procedures.
9. Link every produced check, release, deployment, incident, and final mission result back to the objective before closing it.

Only skip a structured record when the work is genuinely ephemeral and has no expected future audit, release, deployment, incident, or repeatable validation value.

### Dispatch Preflight And Code Indexing

`armada_dispatch` is durable-first. It returns after the voyage and mission rows are persisted; assignment, dock provisioning, worktree setup, and captain launch run asynchronously. If the response is successful but missions remain `Pending`, check `armada_voyage_status`, vessel serialization (`AllowConcurrentMissions=false`), captain persona/model filters, and active code-index updates before redispatching.

Code indexing is incremental where possible. Post-land refreshes reuse unchanged chunk embeddings and graph sidecars, batch embedding-provider calls, and keep lexical search as the reliable fallback even when semantic search is disabled. Dispatch blocks only while a refresh is currently in progress and returns `code_index_update_in_progress` without creating a voyage. Poll `armada_index_status` and retry once `updateInProgress` is false.

### Diagnosing Long-Running or Stalled Captains

When a mission appears stuck -- no new commits, no visible progress, or a watchdog heartbeat gap -- use `armada_captain_diagnostics` before considering a restart:

```
armada_captain_diagnostics captainId: <cpt_...>
```

The tool returns:

| Field | What it tells you |
|-------|-------------------|
| `state` | Captain state: `Working`, `Idle`, `Planning`, `Refining`, `Stalled`, `Stopping` |
| `activeMissionId` / `activeMissionTitle` | Currently assigned mission |
| `elapsedMinutes` | Minutes since mission start (uses `startedUtc`, falls back to `createdUtc`) |
| `dockPath` | Worktree path on disk |
| `dockGitStatus` | Compact `git status --short` output -- lists modified, staged, and untracked files |
| `hasUncommittedDockChanges` | True when the dock has dirty or untracked files (even if no commits yet) |
| `dockGitStatusError` | Set if git is unavailable or the dock path does not exist |
| `codeIndex.freshness` | `Fresh`, `Stale`, `Missing`, `Error`, or `Updating` |
| `codeIndex.isStale` | True when the index commit differs from the current HEAD -- new missions may search stale context |
| `codeIndex.indexedCommitSha` / `currentCommitSha` | Pinpoints exactly how far behind the index is |

**Common patterns:**

- **`hasUncommittedDockChanges: true` with no recent mission events** -- the agent has written files but not committed yet. This is normal for long tasks; the agent is likely still running. Check `elapsedMinutes` and compare to expected task duration.
- **`dockGitStatus` shows only untracked generated files (e.g. `?? protocol-classes/`)** -- the agent may be waiting on a shell sub-process or watching a stale output file. This is the pattern seen when an agent watches an older Claude temp output file for a completion marker while a newer file already contains it. No Armada action is needed; nudge the captain via `armada_signal` if it has been many hours.
- **`codeIndex.isStale: true`** -- the vessel index is behind HEAD. Future missions dispatched to this vessel will search against stale code. Kick off `armada_index_update` for the vessel after the current mission lands; do not interrupt the current captain.
- **`state: Idle` with a non-null `activeMissionId`** -- the captain process exited without updating its state. Check `armada_get_captain_log` for the last output lines. If the mission is still `InProgress`, use `armada_mark_mission_failed` or let autonomous recovery handle the rescue.
- **`dockGitStatusError: "dock path does not exist"`** -- the worktree was reclaimed or never fully provisioned. Check `armada_voyage_status` for the mission status.

This tool is read-only and safe to call at any time. It does not interrupt, cancel, or modify any running captain or dock.

## Pipeline Selection

Choose the default pipeline per vessel based on the repository's dominant risk profile, then override per voyage when the work calls for a different review path.

| Pipeline | Use when |
|----------|----------|
| `WorkerOnly` | Tiny, low-risk operational edits where review would add more queue time than value. |
| `Reviewed` | Narrow implementation work that needs a final Judge but not a dedicated test-writing stage. |
| `Tested` | Default for most backend/library changes: Worker, TestEngineer, Judge. |
| `FullPipeline` | Ambiguous engineering work that benefits from Architect decomposition before implementation. |
| `ProductDevelopment` | Product-facing features, dashboard/workflow changes, onboarding/setup flows, or anything where user value, UX, and acceptance criteria are still fuzzy. Stages: Product Manager, Architect, Worker, Usability Engineer, TestEngineer, Judge. |
| `DiagnosticProtocolTested` | J1939, UDS, J1708, K-line, seed-key/security-access, diagnostic timing/framing, or banned reflash boundary work. |
| `TenantSecurityTested` | Authn/authz, tenant isolation, secrets, auditability, or cross-tenant data exposure risk. |
| `MigrationDataTested` | Schema migrations, provider parity, backfills, indexes, retention, data loss, or restart safety. |
| `PerformanceMemoryTested` | Memory growth, output/log retention, large DB materialization, throughput, disposal, timers, or repeated background work. |
| `ReferencePortingTested` | Porting from decompiler output, traces, vendor references, protocol captures, or source-bundle evidence. |
| `FrontendWorkflowTested` | UI workflow, accessibility, responsive/i18n states, validation/error surfaces, or design-system consistency. |
| `Reflections` / `ReflectionsDualJudge` | Memory consolidation only; do not use for normal code delivery. |

Personas are not interchangeable labels. `Product Manager` clarifies product intent before architecture; `Usability Engineer` reviews the resulting experience after implementation; specialist reviewers are high-tier focused reviewers for known risk domains; `TestEngineer` owns test coverage; `Judge` is final review. If a vessel is mostly product/UI work, set its default pipeline to `ProductDevelopment` or `FrontendWorkflowTested`; if it is mostly library/protocol/security/data work, set the matching specialist tested pipeline as the vessel default. Use `WorkerOnly` only when the blast radius is deliberately small.

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

## Playbook-Curate Operator Workflow

Use `playbook-curate` when a captain had to search because the learned
playbook did not contain enough repo-specific guidance. It is a manual
reflection mode; it writes through the same `armada_accept_memory_proposal`
review path as `consolidate`, but its brief is tuned to mission briefs plus
Grep/Glob/`armada_code_search` evidence.

1. **Single vessel:** call `armada_consolidate_memory` with
   `vesselId` and `mode: "playbook-curate"`. Add `instructions` when you
   want it to focus on a known search gap.

2. **Active-vessel sweep:** call with `vesselId: null` and
   `mode: "playbook-curate"`. Vessels without terminal missions in the
   playbook-curate window are skipped with `no_playbook_gap_evidence`.

3. **Review candidate carefully:** accept only durable facts that should
   reduce future blind searching: repo locations, conventions, validation
   commands, known pitfalls, or stable cross-file relationships. Reject or
   edit one-off implementation detail.

4. **Do not confuse stores:** use `pack-curate` for context-pack hint rows
   and `playbook-curate` for vessel learned-playbook markdown.

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

---

## Codebase index and context packs

The Admiral-owned per-vessel code index supports hybrid lexical + semantic search, cross-vessel fleet queries, supported-language symbol graph sidecars, graph-aware search boosts, and file-level signatures. The dashboard's **Code Index** page exposes vessel status/update controls plus search, symbol, file, and graph-explore views. The default embedding endpoint is DeepSeek-compatible (`EmbeddingModel = "deepseek-embedding"`), and settings can point the embedding/inference clients at another compatible service. Embeddings live inline with each chunk in `~/.armada/code-index/<vesselId>/chunks.jsonl`; graph sidecars live beside them as `symbols.jsonl` and `edges.jsonl`. The index is for repo discovery/evidence -- playbooks, vessel `CLAUDE.md`, and project `CLAUDE.md` win on conflict.

### How to use

**Dispatch-time auto-attach.** `armada_dispatch` defaults `codeContextMode` to `auto`. Every dock gets `_briefing/context-pack.md` staged automatically; captains are instructed (via `proj-corerules-summary` playbook) to read it before any `Grep`/`Glob`. Before pipeline resolution or context-pack generation, MCP dispatch checks the vessel's code-index status. If a post-land or manual update is still running, dispatch returns `Code = "code_index_update_in_progress"` and includes `codeIndex.updateInProgress`, `updateStartedUtc`, `updateHeartbeatUtc`, `updateStage`, progress fields, freshness, vessel id, and vessel name so the orchestrator can explain the wait and retry after the index is current. Mode catalog:

- `auto` -- generate pack from title + description (default).
- `force` -- regenerate even if a captain returned an empty pack; useful when the first pack was thin.
- `off` -- skip entirely. Reserve for intentionally code-blind missions (pure docs, pure ops).

Automatic dispatch-time context generation is bounded. In `auto` mode, a slow context-pack build logs a warning and dispatch continues without the pack so a voyage record is still created. In `force` mode, the same timeout returns a clear pre-dispatch error. The default timeout is 45 seconds and can be overridden with `ARMADA_CODE_CONTEXT_TIMEOUT_MS`.

**`armada_code_search`** -- inspect the index directly when you want raw results, cross-vessel discovery, or to verify a captain's evidence. Filters narrow scope: `pathPrefix` clips to a directory, `language` filters by detected language (`csharp`, `markdown`, etc.). `Score` interpretation:

- Lexical-only baseline clusters in the 60-200 range.
- Hybrid (semantic + lexical) lifts conceptually relevant hits into the 300-700+ range.
- Search records may include `EmbeddingVector` when semantic indexing is enabled; keep `limit` low and prefer context packs for larger evidence transfer until the response-shaping follow-up redacts vectors by default.

Phrase queries by intent (`"seed/key challenge response algorithm"`) rather than single common tokens (`"voyage"` gets swamped by domain noise -- "Voyage" is the orchestrator's dispatch-batch concept and dominates a lexical search).

**Post-land refresh.** When a voyage lands through the local merge path or PR reconciler, Admiral starts a background `armada_index_update` for that vessel. This is intentionally asynchronous so landing is not held hostage by embedding/graph work, but it is treated as a dispatch pre-flight gate: do not dispatch more work for that vessel while `armada_index_status.updateInProgress` is true or `freshness` is `Stale`. The gate protects future missions from stale `armada_code_search` hits and context packs that miss the newly landed code.

**`armada_context_pack`** -- build a dispatch-ready markdown briefing for a specific goal. Returns `Markdown`, `MaterializedPath`, and a `prestagedFiles` entry pointing at `_briefing/context-pack.md`. Pass the entry straight into `armada_dispatch` to override the auto pack with a tighter goal. Always list `_briefing/context-pack.md` in the mission's **Reads** section so the captain knows it is authoritative repo evidence. If pack generation exceeds `timeoutMs` (or `ARMADA_CODE_CONTEXT_TIMEOUT_MS`, default 120 seconds for direct pack tools), the tool returns `code_context_timeout`; use focused `armada_code_search` or retry with a smaller token budget/result count.

Context-pack responses include a `metrics` object: `resultCount`, `includedFileCount`, `includedFiles`, `matchedHintCount`, `matchedHintIds`, `graphExpansionUsed`, `warningCount`, `isSummarized`, `prestagedFileCount`, and `estimatedTokens`. When graph sidecars resolve symbols from the goal or search results, `armada_context_pack` appends a `Symbol Graph Context` section with caller/callee neighborhoods, affected-test candidates, and `graphIncludedFiles`; in that case `graphExpansionUsed` is `true`. Fleet packs aggregate the same metric shape across vessels and prefix included files with `<vesselId>:`. Use these fields to compare pack breadth and hint usefulness before dispatching; use later pack-curate evidence (`filesReadFromPack`, `filesIgnoredFromPack`, `filesGrepDiscovered`, `filesEdited`) to judge whether the pack actually covered what the captain used.

**Graph tools** -- after `armada_index_update`, supported source files are scanned into symbol and edge sidecars. The extractor covers C#, TypeScript/JavaScript, Python, Java/Kotlin, Go, and Rust symbols plus common ASP.NET, Express/router, FastAPI, Spring, and Go HTTP endpoint patterns. Use these when the question is symbol-oriented rather than full-text-oriented:

- `armada_graph_search_symbols` finds symbols by simple or qualified name, with optional `kind` and `pathPrefix` filters.
- `armada_graph_get_callers` / `armada_graph_get_callees` return direct neighbors for a seed symbol.
- `armada_graph_get_node` returns resolved symbols, direct callers/callees, and an optional source excerpt for one symbol.
- `armada_graph_get_files` returns indexed files grouped with their graph symbols.
- `armada_graph_explore` returns a bounded graph neighborhood grouped by file, including relationships and optional source sections.
- `armada_graph_get_impact` traverses callers, callees, or both with bounded depth.
- `armada_graph_suggest_affected_tests` ranks likely test files using graph traversal plus path/name convention fallback.

REST equivalents live under `/api/v1/vessels/{vesselId}/code-index/...`: `status`, `update`, `search`, `search-symbols`, `callers`, `callees`, `node`, `files`, `explore`, `impact`, and `affected-tests`. Graph responses include sidecar freshness warnings; refresh the index when the sidecars are missing, empty, stale, or commit-mismatched. Current scope remains a dependency-free lexical/regex extractor rather than a full AST parser; the extractor handles common endpoint-to-handler edges, simple import aliases, and known local call target qualification, while unsupported languages and complex dynamic call paths still rely on lexical/semantic search.

**Semantic vs lexical activation.** Semantic ranking only kicks in when **both** `CodeIndex.UseSemanticSearch = true` and `CodeIndex.EmbeddingApiKey` are populated in settings. Without both, search silently degrades to lexical-only -- check this first when semantic results look weak.

**Captain-side note.** Newly provisioned docks are seeded with dock-local Armada MCP config (`.mcp.json`, `.cursor/mcp.json`, `.codex/config.toml`, and `.gemini/settings.json`) that points compatible clients at the local Admiral MCP endpoint. Captains should still start with the pre-attached `_briefing/context-pack.md`; direct `armada_code_search`, `armada_context_pack`, and graph-tool use depends on the captain runtime loading project MCP config and Admiral staying available. Existing docks must be reprovisioned before they receive the generated config. Mission instructions include a required final `Pack:` line, and Armada emits `mission.context_pack_usage` after completion to record whether the pack was read before search, search happened first, the pack was missing, or usage could not be observed.

### Operator gotchas

These are the operationally-painful lessons encountered in the field. Treat them as the checklist when something feels wrong.

1. **`Armada.Server.exe` survives `dotnet` kill.** Stopping the orchestrator with `Get-Process -Name dotnet | Stop-Process` leaves the published binary running on port 7890. Always also run `Get-Process -Name "Armada.Server" -ErrorAction SilentlyContinue | Stop-Process -Force`. If the port stays bound, the Admin restart fails with a binding error.
2. **MCP HTTP host: `localhost`, not `127.0.0.1`.** Use `http://localhost:7891`. HTTP.sys URL ACLs are registered only for `localhost`; the loopback IP returns `400 Invalid Hostname`.
3. **`UseSemanticSearch=false` is a silent-fallback footgun.** Embeddings are generated only when the flag is `true`. If the flag is false, `armada_index_update` writes `chunks.jsonl` with `embeddingVector=null` and burns no API quota -- but search silently drops to lexical-only with no warning. Check the flag first when semantic results look wrong.
4. **MCP client timeout vs Admin progress.** An MCP client (e.g. Claude Code) can hit its own response timeout on a long `armada_index_update` call and report failure, while the Admin keeps running the indexing job to completion. Don't re-dispatch on timeout -- re-check `armada_index_status` first; `updateHeartbeatUtc`, `updateStage`, and `updateProgressPercent` show whether the job is still moving.
5. **Captain dock MCP config is only seeded for new docks.** If a running captain cannot see `armada_code_search`, `armada_context_pack`, or graph tools, check whether the dock predates the seeding change, whether the runtime loads project MCP config (`.mcp.json`, `.cursor/mcp.json`, `.codex/config.toml`, or `.gemini/settings.json`), and whether Admiral's MCP endpoint is running on the configured port.
6. **Dispatch blocked by index update is expected.** If MCP returns `code_index_update_in_progress`, do not bypass with `codeContextMode=off` unless the user explicitly wants code-blind work. Poll `armada_index_status` until `updateInProgress` is false, then retry the same dispatch so the auto context pack is generated from the latest landed code.
7. **Stale memory checks are read-only.** Use `armada_check_stale_memory` to inspect accepted reflection anchors for missing files or missing source missions. The same warnings are fed into future vessel reflection briefs so MemoryConsolidator can propose disable/merge/rewrite updates through normal review; the diagnostic itself never edits playbooks or events.

### CodeGraph implementation status

The CodeGraph-inspired implementation is complete for Armada's current indexing/search scope: supported-language sidecars, graph query APIs, graph-expanded context packs, dock-local MCP config seeding, framework endpoint symbols, endpoint-handler and import-alias call edges, known local call target qualification, configurable graph-aware search boosts, vessel-scoped REST code-index routes, and the dashboard Code Index page are all shipped. Future parser swaps should be driven by concrete misses in the feedback log rather than treated as required remaining work.

Notable misses, wins, and miss-class taxonomy live in `project/docs/armada-code-index-feedback.md`. When `armada_code_search` or a context pack misses badly enough to change the workflow, append an entry there per the templates at the top of that doc; captain-sourced feedback (the `[CONTEXT-PACK-FEEDBACK]` block in a final report) is transcribed by the orchestrator into the same log.

### Semantic index, fleet tools, and V2 settings surface

The V2 index surface (shipped in M1-M5) adds semantic search, cross-vessel fleet queries, and file-level signatures to the original lexical index.

#### V2 CodeIndex settings

All settings live under `CodeIndex` in `~/.armada/settings.json`:

| Setting | Default | Range | Description |
|---------|---------|-------|-------------|
| `UseSemanticSearch` | `false` | boolean | Enable embedding-based semantic search. When `false`, search is lexical-only (V1 behavior). |
| `EmbeddingModel` | `"deepseek-embedding"` | string | Model name passed to the embedding endpoint. |
| `EmbeddingApiBaseUrl` | `"https://api.deepseek.com"` | string | Base URL for embedding API calls. |
| `EmbeddingApiKey` | `""` | string | API key for embedding calls. Store the secret here; never commit real keys to git. |
| `EmbeddingBatchSize` | `32` | 1-256 | Number of chunks sent per embedding request during index updates. |
| `EmbeddingProgressLogInterval` | `200` | 50-2000 | Number of embedded chunks between progress log entries during index updates. |
| `SemanticWeight` | `0.7` | 0.0-1.0 | Weight applied to semantic cosine similarity in hybrid scoring. |
| `LexicalWeight` | `0.3` | 0.0-1.0 | Weight applied to lexical substring/term-occurrence score in hybrid scoring. |
| `PostLandRefreshDebounceSeconds` | `30` | 0-3600 | Debounce window for coalescing post-land index refreshes per vessel. |
| `UseSummarizer` | `false` | boolean | Enable context pack summarization via inference client. |
| `SummarizerModel` | `"deepseek-chat"` | string | Model name for summarization calls. |
| `SummarizerApiBaseUrl` | `""` | string | Falls back to `EmbeddingApiBaseUrl` when empty. |
| `SummarizerApiKey` | `""` | string | Falls back to `EmbeddingApiKey` when empty. |
| `MaxSummaryOutputTokens` | `2048` | 256-8192 | Maximum tokens the summarizer may emit. |
| `UseFileSignatures` | `false` | boolean | Generate per-file natural-language signatures and embed them for file-level relevance boosting. |
| `SignatureModel` | `""` | string | Model for signature generation. Falls back to `SummarizerModel` when empty. |
| `FileSignatureBoostWeight` | `0.2` | 0.0-1.0 | Additive boost applied to chunk scores when a file's signature matches the query. |
| `UseGraphSearchBoosts` | `true` | boolean | Apply graph-derived additive boosts when fresh sidecars can resolve query or result symbols. |
| `GraphSeedBoost` | `18.0` | 0.0-100.0 | Boost applied to direct seed-symbol result files. |
| `GraphNeighborBoost` | `8.0` | 0.0-100.0 | Boost applied to caller/callee neighbor files. |
| `GraphEndpointBoost` | `12.0` | 0.0-100.0 | Boost applied to framework endpoint symbol files. |
| `GraphFrameworkBoost` | `10.0` | 0.0-100.0 | Boost applied when framework-derived graph symbols match. |
| `GraphTagBoost` | `6.0` | 0.0-100.0 | Boost applied to graph tag/path classification matches. |

Each weight is clamped to 0.0-1.0 individually at config-load; weights are not renormalized at runtime, so the blended score magnitude scales with their sum.

#### Fleet MCP tools

Two fleet-scoped tools fan out across all vessels in a fleet and merge results:

**armada_fleet_code_search**

```json
{
  "fleetId": "flt_abc123def456ghi789jk",
  "query": "tenant filter interceptor implementation",
  "limit": 10,
  "pathPrefix": "src/",
  "language": "csharp",
  "includeContent": false,
  "includeReferenceOnly": false
}
```

Response contains `Results` with `VesselId` and `VesselName` on every hit, sorted by score across the fleet. `Limit` is capped at 50.

**armada_fleet_context_pack**

```json
{
  "fleetId": "flt_abc123def456ghi789jk",
  "goal": "How do I add a new vessel to an existing fleet?",
  "tokenBudget": 8000,
  "maxResultsPerVessel": 3
}
```

Response contains combined markdown from all vessels with `## Vessel: {VesselName}` headings. When `UseSummarizer` is enabled, the combined pack is summarized once more at the fleet level.

#### Summarizer behavior

When `UseSummarizer` is enabled, context-pack chunks are compressed through the inference client before the pack is materialized for dispatch. `ContextPackResponse.Markdown` keeps the raw markdown, while `ContextPackResponse.SummarizedMarkdown` carries the compressed version when summarization succeeds. `prestagedFiles` points at the summarized materialized file when `SummarizedMarkdown` is present, otherwise it falls back to the raw markdown file. Operators can opt out by leaving `UseSummarizer` set to `false` in settings before making the request.

#### OpenCode Server inference mode

Armada can route summarizer and file-signature inference through a local OpenCode daemon instead of direct HTTP chat completions. Set:

- `CodeIndex.InferenceClient = "OpenCodeServer"`
- `CodeIndex.OpenCodeServer.AutoLaunch = true` (default)

When enabled, Armada probes `GET {BaseUrl}/global/health` and either:

1. Attaches to an already-running daemon when healthy, or
2. Launches `opencode serve --port {Port} --hostname {Hostname}` and waits for health.

OpenCode credentials are managed by the CLI, not by Armada. On Linux/macOS the auth state is stored in `~/.local/share/opencode/auth.json` (platform equivalents apply on Windows).

To disable daemon auto-launch but keep OpenCode inference available, set:

- `CodeIndex.OpenCodeServer.AutoLaunch = false`

In that mode, operators start `opencode serve` manually before issuing index summarization/signature workflows.

#### File signature behavior

When `UseFileSignatures` is enabled:

1. **Index time**: `UpdateAsync` generates a 1-2 sentence natural-language signature for each unique file using the inference client, embeds it via the embedding client, and stores it in `signatures.jsonl` alongside `chunks.jsonl`.
2. **Search time**: After chunk-level scoring, file signature vectors are compared to the query vector. All chunks belonging to a file with high signature similarity receive an additive boost proportional to `FileSignatureBoostWeight * signatureSimilarity`.
3. **Boost semantics**: The boost is additive post-scoring; it never overrides a strong chunk hit, only lifts file-level relevance on otherwise weaker chunks.

Performance note: signature generation costs one inference call plus one embedding call per file at index time. A full fleet index of ~10,000-15,000 files therefore requires ~20,000-30,000 API calls during the initial build.

#### Sample settings.json

```json
{
  "admiralPort": 7890,
  "mcpPort": 7891,
  "codeIndex": {
    "useSemanticSearch": true,
    "embeddingModel": "deepseek-embedding",
    "embeddingApiBaseUrl": "https://api.deepseek.com",
    "embeddingApiKey": "sk-your-embedding-key",
    "semanticWeight": 0.7,
    "lexicalWeight": 0.3,
    "useSummarizer": true,
    "summarizerModel": "deepseek-chat",
    "summarizerApiBaseUrl": "",
    "summarizerApiKey": "",
    "maxSummaryOutputTokens": 2048,
    "useFileSignatures": true,
    "signatureModel": "",
    "fileSignatureBoostWeight": 0.2,
    "useGraphSearchBoosts": true,
    "graphSeedBoost": 18.0,
    "graphNeighborBoost": 8.0,
    "graphEndpointBoost": 12.0,
    "graphFrameworkBoost": 10.0,
    "graphTagBoost": 6.0
  }
}
```

Replace placeholder keys with actual secrets. `EmbeddingApiKey` is the sensitive field; keep it out of version control.
