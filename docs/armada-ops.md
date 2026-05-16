# Armada Operator Guide

This document describes operational workflows for the Armada multi-agent orchestration system.

---

## Current Operator Notes

Last upstream sync: upstream `e5fe494d` via merge `9fcfe995` on 2026-05-15. The fork now includes upstream v0.8.0 delivery-management surfaces plus fork orchestration features.

For non-trivial work, start from an objective/backlog item and keep the evidence there. Use objective refinement or Planning for fuzzy scope, Workspace/context packs for file-grounded scope, and structured check runs for repeatable validation. Dispatch missions with objective IDs, selected playbooks, expected checks, and explicit file boundaries.

Use these upstream surfaces when they fit:

- `list_objectives`, `create_objective`, `update_objective`, and backlog aliases for durable scope, priority, acceptance criteria, rollout constraints, and evidence links.
- `run_check`, `get_check_run`, and `retry_check_run` for build/test/deploy validation records instead of only mission logs.
- `create_release`, `create_deployment`, deployment approval/verify/rollback tools, and runbook tools for release and operations work.
- Workspace, request history/API Explorer, history timeline, GitHub evidence, and captain tool visibility from the dashboard when investigating or resuming work.
- Pipeline review gates for human checkpoints; approve or deny via mission detail or `/api/v1/missions/{id}/review/*` before merge queue/audit/PR fallback.

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

---

## Codebase index and context packs

The Admiral-owned per-vessel code index supports hybrid lexical + semantic search (Voyage AI `voyage-code-3` embeddings, 1024-dim), cross-vessel fleet queries, and file-level signatures. Embeddings live inline with each chunk in `~/.armada/code-index/<vesselId>/chunks.jsonl`. The index is for repo discovery/evidence -- playbooks, vessel `CLAUDE.md`, and project `CLAUDE.md` win on conflict.

### How to use

**Dispatch-time auto-attach.** `armada_dispatch` defaults `codeContextMode` to `auto`. Every dock gets `_briefing/context-pack.md` staged automatically; captains are instructed (via `proj-corerules-summary` playbook) to read it before any `Grep`/`Glob`. Mode catalog:

- `auto` -- generate pack from title + description (default).
- `force` -- regenerate even if a captain returned an empty pack; useful when the first pack was thin.
- `off` -- skip entirely. Reserve for intentionally code-blind missions (pure docs, pure ops).

**`armada_code_search`** -- inspect the index directly when you want raw results, cross-vessel discovery, or to verify a captain's evidence. Filters narrow scope: `pathPrefix` clips to a directory, `language` filters by detected language (`csharp`, `markdown`, etc.). `Score` interpretation:

- Lexical-only baseline clusters in the 60-200 range.
- Hybrid (semantic + lexical) lifts conceptually relevant hits into the 300-700+ range.
- `EmbeddingVector` is redacted from JSON responses (vectors stay on disk).

Phrase queries by intent (`"seed/key challenge response algorithm"`) rather than single common tokens (`"voyage"` gets swamped by domain noise -- "Voyage" is the orchestrator's dispatch-batch concept and dominates a lexical search).

**`armada_context_pack`** -- build a dispatch-ready markdown briefing for a specific goal. Returns `Markdown`, `MaterializedPath`, and a `prestagedFiles` entry pointing at `_briefing/context-pack.md`. Pass the entry straight into `armada_dispatch` to override the auto pack with a tighter goal. Always list `_briefing/context-pack.md` in the mission's **Reads** section so the captain knows it is authoritative repo evidence.

**Semantic vs lexical activation.** Semantic ranking only kicks in when **both** `CodeIndex.UseSemanticSearch = true` and `CodeIndex.EmbeddingApiKey` are populated in settings. Without both, search silently degrades to lexical-only -- check this first when semantic results look weak.

**Captain-side note.** Captains running inside dock worktrees consume the pre-attached `_briefing/context-pack.md` but do not currently have direct access to `armada_code_search` / `armada_context_pack` MCP tools themselves. Orchestrate pack quality at dispatch time; if a captain reports a thin pack, re-dispatch with `codeContextMode=force` or build a custom pack with a tighter goal.

### Operator gotchas

These are the operationally-painful lessons encountered in the field. Treat them as the checklist when something feels wrong.

1. **`Armada.Server.exe` survives `dotnet` kill.** Stopping the orchestrator with `Get-Process -Name dotnet | Stop-Process` leaves the published binary running on port 7890. Always also run `Get-Process -Name "Armada.Server" -ErrorAction SilentlyContinue | Stop-Process -Force`. If the port stays bound, the Admin restart fails with a binding error.
2. **MCP HTTP host: `localhost`, not `127.0.0.1`.** Use `http://localhost:7891`. HTTP.sys URL ACLs are registered only for `localhost`; the loopback IP returns `400 Invalid Hostname`.
3. **`UseSemanticSearch=false` is a silent-fallback footgun.** Embeddings are generated only when the flag is `true`. If the flag is false, `armada_index_update` writes `chunks.jsonl` with `embeddingVector=null` and burns no API quota -- but search silently drops to lexical-only with no warning. Check the flag first when semantic results look wrong.
4. **MCP client timeout vs Admin progress.** An MCP client (e.g. Claude Code) can hit its own response timeout on a long `armada_index_update` call and report failure, while the Admin keeps running the indexing job to completion. Don't re-dispatch on timeout -- re-check `armada_index_status` first; the job may already be done.
5. **Captain dock MCP surface is narrower than the orchestrator's.** `armada_code_search` and `armada_context_pack` are orchestrator-side only; captains read the prestaged pack. Don't ask a captain to "search for X" -- generate the pack with a tighter goal yourself and re-stage.

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
| `SemanticWeight` | `0.7` | 0.0-1.0 | Weight applied to semantic cosine similarity in hybrid scoring. |
| `LexicalWeight` | `0.3` | 0.0-1.0 | Weight applied to lexical substring/term-occurrence score in hybrid scoring. |
| `UseSummarizer` | `false` | boolean | Enable context pack summarization via inference client. |
| `SummarizerModel` | `"deepseek-chat"` | string | Model name for summarization calls. |
| `SummarizerApiBaseUrl` | `""` | string | Falls back to `EmbeddingApiBaseUrl` when empty. |
| `SummarizerApiKey` | `""` | string | Falls back to `EmbeddingApiKey` when empty. |
| `MaxSummaryOutputTokens` | `2048` | 256-8192 | Maximum tokens the summarizer may emit. |
| `UseFileSignatures` | `false` | boolean | Generate per-file natural-language signatures and embed them for file-level relevance boosting. |
| `SignatureModel` | `""` | string | Model for signature generation. Falls back to `SummarizerModel` when empty. |
| `FileSignatureBoostWeight` | `0.2` | 0.0-1.0 | Additive boost applied to chunk scores when a file's signature matches the query. |

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
    "fileSignatureBoostWeight": 0.2
  }
}
```

Replace placeholder keys with actual secrets. `EmbeddingApiKey` is the sensitive field; keep it out of version control.
