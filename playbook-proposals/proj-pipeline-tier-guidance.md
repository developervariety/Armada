---
name: proj-pipeline-tier-guidance
description: pipeline catalogue, model tier semantics, default picking heuristics, and specialist personas for Architect dispatch
type: vessel
---

# Pipeline, tier, and persona guidance (project fleet)

Distilled reference for choosing `pipeline` and `preferredModel` tier when decomposing voyages. Captains cannot read `project/`-level ops docs from a dock; this playbook carries the decision surface so Architect missions can drop `armada-ops.md` from `prestagedFiles`.

Confirm the live list with `armada_enumerate(entityType="pipelines", pageSize=30)`; built-in names below match `PersonaSeedService` in Armada.

## 1. Pipeline catalogue

| Pipeline | Stages | When to pick | Anti-patterns |
| --- | --- | --- | --- |
| `WorkerOnly` | Worker | Tiny smoke-only changes where you accept no TestEngineer/Judge gate | Default choice (bypasses normal persona routing on legacy path); avoid for normal product work |
| `Reviewed` | Worker -> Judge | Docs-only or trivial edits; Judge reviews evidence without a separate TestEngineer stage | Production logic changes that need targeted test ownership |
| `Tested` | Worker -> TestEngineer -> Judge | Default execution path: implement, validate/tests, final review | Skipping TestEngineer when failure would be expensive |
| `FullPipeline` | Architect -> Worker -> TestEngineer -> Judge | Host has not approved a detailed mission graph and Architect must run inside the same execution chain | Use when spec already contains the full graph (wastes Architect stage) |
| `DiagnosticProtocolTested` | Worker -> DiagnosticProtocolReviewer -> TestEngineer -> Judge | J1939, UDS, OEM seed-key, diagnostic framing, UDS 0x34 guardrails | General app features unrelated to diagnostics |
| `TenantSecurityTested` | Worker -> TenantSecurityReviewer -> TestEngineer -> Judge | Authn/authz, tenant isolation, secrets, audit surfaces | Pure UI polish with no security boundary |
| `MigrationDataTested` | Worker -> MigrationDataReviewer -> TestEngineer -> Judge | Schema/migration parity, backfills, rollback safety | Flyway-only comments with no schema motion |
| `PerformanceMemoryTested` | Worker -> PerformanceMemoryReviewer -> TestEngineer -> Judge | Memory growth, retained graphs, log/OOM risk, DB materialization | One-line perf tweaks without systemic risk |
| `ReferencePortingTested` | Worker -> PortingReferenceAnalyst -> TestEngineer -> Judge | Porting backed by reference exports, traces, semantic parity evidence | Greenfield design with no reference artifacts |
| `FrontendWorkflowTested` | Worker -> FrontendWorkflowReviewer -> TestEngineer -> Judge | UX flows, a11y, responsive/i18n, error states | Headless services with no UI |
| `Reflections` | MemoryConsolidator | Consolidate learned facts into playbook proposals; orchestrator reviews output | Normal code ship; needs Worker implementation |
| `ArchitectOnly` (custom) | Defined per fleet (often single Architect stage) | Decomposition checkpoint: `[ARMADA:MISSION]` markers without immediate Worker execution | Creating when `armada_decompose_plan` or an approved graph already suffices |

Specialist `*Tested` pipelines pin the specialist stage to **high** tier preference; Worker still receives the dispatch `preferredModel` tier.

## 2. Tier semantics

| Tier | Typical work shape | Peer pool (illustrative; admiral picks idle eligible captain) |
| --- | --- | --- |
| `low` | Small diffs, zero judgement, established pattern (typos, single guard, one test add, doc link fix). Tight acceptance bullets. | Quick worker pool (example: Kimi captains in current fleet docs) |
| `mid` | Up to moderate refactors, new abstraction in a known pattern, richer tests. Multi-step but bounded. | Sonnet / Composer / Gemini style workers |
| `high` | Architecture, novel protocol, security-sensitive design. Acceptance reads "design X such that Y, Z." | Opus / Codex-tier workers and specialists |

**Anti-bias rule:** Orchestrator passes `preferredModel: "low" | "mid" | "high"` only, never a concrete vendor model string, unless using an explicit escape hatch documented for operators. Admiral maps tier to an idle eligible peer; if the requested tier is saturated it may upgrade `low -> mid -> high`, never downgrade.

## 3. Default pipeline picking heuristic (nine-step loop)

Use the numbered loop from project ops for concurrent TODO shipping; it embeds the default execution pipeline choice:

1. Pick a TODO chunk (about 5-15 items when working from a backlog).
2. Classify each item parallel-safe vs real dependency (shared files, migrations, contract ordering).
3. Dispatch parallel-safe items in one multi-mission voyage per repo with **`Tested`** unless a catalogue row above overrides. Use `dependsOnMissionId` for foundation-first ordering.
4. Dispatch dependency chains via Architect (`armada_decompose_plan` or Architect-stage voyages) when the graph is not already manual.
5. Watch for `WorkProduced` and merge-queue entries (`armada_enumerate` with `entityType=merge_queue` as needed).
6. Review each diff before landing (mission diff, merge entry metadata).
7. Approve with per-entry merge processing when tests/integration pass.
8. Reject with cancel/restart when feedback needs a captain rerun (not mid-flight signal after exit).
9. Close planning records in the same session as landing when project TODO is reachable policy applies.

**Shortcut reminders:** crisp single change -> default **`Tested`**; crisp with many coupled missions -> Architect first, then **`Tested`** voyages; mechanical Low-tier fixes often still use **`Tested`** so TestEngineer + Judge remain in the loop unless policy explicitly chooses `Reviewed` or `WorkerOnly`.

## 4. Sensitive-file dual-Judge brief callout

When a Worker brief touches **security-sensitive paths**, dispatch **two Judge missions in parallel** (vendor split: Opus + Codex in current fleet policy) after Worker finishes. Require **both PASS** before enqueueing merge. TestEngineer remains primary author of command results; Judges re-review the same diff and evidence.

**otrbuddy:** `**/Features/Auth/**`, `**/Features/Pairing/**`, `**/Security/**`, `**/Hubs/**`, crypto/token/signing filenames (`Paseto`, `Hmac`, `Crypto`, `Token`, `Secret`, `Signing`), `RlsTenantInterceptor`, `TenantContext`, new auth scheme registrations.

**j1939mitm:** per-OEM `SeedKey` / `Crypto` under manufacturers, `J1939Mitm.Core/Cummins/**`, RSA/password-table primitives, new OEM seed-key signer code.

**JproDeobfuscator / OtrPerformanceDeobfuscator:** extractor outputs are not treated as security dual-Judge triggers (hand-edit rules still forbid touching generated trees).

**armada:** `src/Armada.Core/Authorization/**`, credential DB methods, `src/Armada.Server/Mcp/**`, `src/Armada.Server/Authentication/**`, new destructive MCP tools.

Pipeline expansion does **not** auto-create the second Judge; orchestrator adds that mission after Worker `WorkProduced` until a native pipeline exists.

## 5. Specialist personas glossary

| Persona | Role |
| --- | --- |
| `Worker` | Standard executor: code changes, tests, commits |
| `Architect` | Plans voyages; decomposes work; emits mission graphs |
| `Judge` | Final diff review, verdict, follow-ups |
| `TestEngineer` | Targeted validation, test additions, command results between Worker and Judge |
| `DiagnosticProtocolReviewer` | J1939, UDS, J1708, K-line, OEM seed/security access, timing, banned reflash checks |
| `TenantSecurityReviewer` | Multi-tenant authz/authn, isolation, secrets, auditability, leak risk |
| `MigrationDataReviewer` | Migrations, provider parity, indexes, backfills, rollback/restart safety |
| `PerformanceMemoryReviewer` | Memory/allocations, retained graphs, log growth, DB materialization, throughput |
| `PortingReferenceAnalyst` | Approved reference material, traces, parity evidence for ports |
| `FrontendWorkflowReviewer` | UX/a11y/responsive/i18n/errors/design consistency |
| `MemoryConsolidator` | Curates learned-facts playbook proposals from evidence; read-only on sources |

## 6. What this playbook is NOT

- Admiral install, restart, port, health-check, or process-tree troubleshooting
- Copy-paste recipes for MCP tools beyond naming `armada_enumerate` for fresh pipeline rows
- Fleet-specific captain names, vessel IDs, or absolute paths to local checkouts
- Merge-queue land semantics, AutoLand predicate tuning, or audit drain procedures
- Code-index/context-pack workflows (see separate playbooks or in-brief pointers when allowed)

Use `proj-corerules-summary` (and the target vessel `CLAUDE.md`) for legal, logging, testing, and commit-message rules.
