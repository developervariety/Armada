# Design — Progressive-Disclosure Context System

Status: design only. This document describes a context-delivery design. It
wires no production code, changes no test, and adds no migration. Each phase is
a later row, opened only after the owner reviews this design. New typed
decision id: `context_route` (D27). Default mode: **Off**.

## 1. The measured problem

An orchestrator loads a large fixed block of context before it does any work.
The block is eager. Nothing in it is fetched on demand.

| Source | Size |
| --- | --- |
| AI-Memory imports (workspace loaders) | ~170 KB |
| `docs/armada-ops.md` (11 chapters) | ~183 KB |
| `README.md` | ~37 KB |
| `docs/MCP_API.md` | ~30 KB |
| `docs/MERGING.md` | ~13 KB |
| `docs/OPERATIONAL_ASSETS.md` | ~11 KB |
| `docs/DELIVERY_OPERATIONS.md` | ~10 KB |
| `CLAUDE.md` | ~8 KB |
| **Total** | **~462 KB (~115k tokens)** |

A captain pays a second, different eager cost. Its brief pins a persona
template (2-8 KB), mission scaffolding templates, attached playbooks (10-20
KB), skills, and project rules. The brief also tells the captain to read all of
`AI-Memory/shared/` (~27 KB) and its repository memory folder. Most of that
context does not touch the mission in front of it.

The cost is real on both sides. An orchestrator spends ~115k tokens before it
reads one record. A captain spends its instruction budget on rules that its
mission never reaches. Neither side can drop the load, because part of it is a
safety rule that must always apply.

The goal is progressive disclosure. Keep a small always-on core. Fetch the rest
by topic, on demand. Apply this to orchestrators and to every captain persona.
Never make a safety, boundary, or non-negotiable rule optional.

## 2. The two-tier model

### 2a. Always-on core

The core is small, curated, and never subject to retrieval. It ships inline in
every orchestrator session and every captain brief. It holds:

- The safety and boundary non-negotiables: the repository leak-prevention
  rules, the domain guardrails, the destructive-operation limits, tenant
  isolation, and the protected-path rule.
- The proof rule (a report is not evidence) and the land-then-sync rule.
- The index or map: what topics exist and where they live.
- The retrieval instruction: how to fetch more.

The core is defined by an explicit allowlist. A chunk is core only when the
owner marks it `tier: core`. Nothing is promoted to core automatically. Nothing
is demoted from core by a model.

### 2b. Retrievable leaves

Everything else is a leaf. A leaf is fetched by topic, on demand, when the task
needs it. The dispatch mechanics, the Check gate table, the merge-queue traps,
a persona's deep procedure, a vessel's port-fidelity rules: all leaves.

### 2c. Why a safety rule can never be retrieval-gated

Retrieval can miss. A keyword search can rank the wrong chunk first. A model
router can score a relevant chunk low. For a leaf, a miss costs one extra
fetch or a slightly worse answer.

For a safety rule, a miss is different. A missed retrieval means the captain
acts without the rule. It does not know the rule exists, so it does not know to
fetch it. That is the silent-skip failure: the captain proceeds, the guard
never fires, and nothing reports that the guard was absent. A silent skip reads
as a healthy run.

So the core is not retrieved. It is always present. Retrieval only ever widens
what is available beyond the core. Retrieval never decides whether a core rule
loads.

## 3. The chunk format

Each doc or memory chunk is a small, single-topic file (or a single section
inside a topic file). Each chunk carries front-matter.

Fields:

- `topic` — a stable dotted id, unique across the manifest.
- `summary` — one line. What the chunk holds.
- `read_when` — the trigger. The plain-language condition under which a reader
  needs this chunk. The retrieval engine and the reader both use it.
- `applies_to` — a list. `orchestrator`, a persona (`persona:Judge`), a vessel
  (`vessel:ExampleVessel`), or `all`.
- `tier` — `core` or `leaf`.

Example:

```yaml
---
topic: ops.standard-workflow.checks
summary: The Judge PASS real-signal gate; arming, staleness, and resolving every failed Check.
read_when: You are creating Checks, reading a held or rejected Judge PASS, or resolving a failed Check.
applies_to: [orchestrator]
tier: leaf
---
```

A large source (one chapter of `armada-ops.md`) becomes one topic file split
into sub-topic sections, each with its own front-matter. The worked example
shows this shape.

## 4. The machine index (manifest)

One generated file maps every topic to its location and metadata. The retrieval
tool reads it. The loaders read it. It is generated from the chunk front-matter
and is never hand-edited.

Shape (JSON):

```json
{
  "version": 1,
  "generated_from": "docs/ops/*.md, docs/**/*.md, AI-Memory/**/*.md front-matter",
  "chunks": [
    {
      "topic": "ops.standard-workflow.checks",
      "path": "docs/ops/standard-workflow.md#checks",
      "summary": "The Judge PASS real-signal gate; arming, staleness, and resolving every failed Check.",
      "read_when": "You are creating Checks, reading a held or rejected Judge PASS, or resolving a failed Check.",
      "applies_to": ["orchestrator"],
      "tier": "leaf",
      "bytes": 6120
    },
    {
      "topic": "core.boundary.leak-prevention",
      "path": "AI-Memory/shared/repository-boundary-and-leak-prevention.md",
      "summary": "What must never reach a repository; the pre-push scan list; the owner approval gate.",
      "read_when": "Always. Core rule; never retrieval-gated.",
      "applies_to": ["all"],
      "tier": "core",
      "bytes": 4980
    }
  ]
}
```

`bytes` lets a caller budget before it fetches. A core entry is data, not a
retrieval target: a caller loads every `tier: core` chunk unconditionally and
uses the manifest only to find them.

## 5. The retrieval tool contract

Extend the existing `armada_context_pack` tool. Armada already has a
code-context stack: `armada_context_pack`, `armada_code_search`, the
`armada_graph_*` family, and the `armada_index_*` family. Add a docs-and-memory
mode to the same tool rather than a new one, so the caller learns one tool.

Input:

- A task description or a topic set.
- The requesting persona and vessel (`applies_to` filter).
- A token budget for the leaf set.
- A `sources` list: `code`, `docs`, `memory`, or a combination. The default
  keeps the current code-only behavior, so no existing caller changes.

Output:

- The full set of `tier: core` chunks. Always. First. Not counted against the
  leaf budget.
- The ranked, relevant leaf chunks, within the budget, filtered by
  `applies_to`.
- The manifest metadata for each returned chunk, so the reader can fetch a
  neighbor.

Determinism:

- The engine is keyword and semantic retrieval over the manifest text
  (`topic`, `summary`, `read_when`). This is the deterministic floor. A
  fixed input gives a fixed ranking. Ties break by `topic` id.
- The engine produces two sets: a FLOOR set (high-confidence matches, always
  included) and a CANDIDATE set (borderline matches).

Fail-safe:

- The tool never returns zero core chunks. If the manifest cannot be read or
  retrieval throws, the tool returns every core chunk plus a conservative
  superset of leaves for the persona, and it says it degraded.
- The tool over-includes the core. It never drops a `tier: core` chunk for any
  reason: budget, ranking, persona filter, or error.
- The leaf budget can push a low-ranked leaf out. The core budget cannot.

## 6. Jev's bounded role

Jev is a typed relevance-router. It sits on top of the deterministic retrieval,
never under it. Decision id `context_route` (D27). It reuses the existing typed
decision infrastructure: `ITypedDecisionClient` and `TypedDecisionAdapterBase`,
through a new `TypedContextRouteAdapter`.

What Jev decides:

- Given the task text and the CANDIDATE leaf set (topics and summaries), Jev
  chooses which candidates to ADD to the floor set, and it orders the combined
  leaf set by relevance. Each choice carries a confidence.

What Jev never decides:

- Jev never decides whether a `tier: core` chunk loads. Core is added after the
  router runs, unconditionally. Jev never sees core as an option.
- Jev never removes a FLOOR leaf. It only widens (adds candidates) and orders.
- Jev writes nothing, fetches nothing, and lands nothing.

This matches the typed-decision non-negotiable: the model may only be more
conservative than the rule. For context, more conservative means include more,
never less. Jev can add a borderline chunk and can reorder. It cannot subtract.

Lifecycle (the shared adapter skeleton):

- Off — no call. The deterministic ranking stands.
- Shadow, or Gate below threshold — record the model reading, keep the
  deterministic ranking.
- Gate at or above threshold — combine: the floor set and core always stand;
  the model's added candidates and order apply.
- Unavailable (timeout, non-2xx, parse error) — return the deterministic
  ranking, record `typed_decision.unavailable`. A slow router is unavailable,
  not late.

Rules and recording:

- Off by default. Ships Off, then moves to Gate after the four-week review, per
  the typed-decision programme.
- Every call is recorded: both the deterministic ranking and the model reading,
  as `typed_decision.*` events, with the state hash and byte count, never the
  state.
- Redacted. The task text passes through the same `DecisionStateRedactor` as
  every other decision. Topic ids and summaries are already public doc
  metadata; the task text is not, so it is redacted.
- The key is `ARMADA_TYPESAFE_KEY` in the admiral environment or the server's
  key file under the data directory, and nowhere else.

## 7. Per-persona brief slimming

Today a captain brief pins a persona template, playbooks, skills, and project
rules, and tells the captain to read all of `AI-Memory/shared/` and its
repository memory folder. Replace the read-everything instruction with three
parts:

1. The core ships inline, as it does now. Small. Always. Never gated.
2. A retrieval call delivers only the mission-relevant leaves for this persona
   and vessel, within the brief budget.
3. A pointer tells the captain how to fetch more on demand, by topic.

The non-negotiable core still ships inline in every brief. The change is only
to the optional set: the captain no longer carries all of shared memory in
every mission.

Per-persona view. The personas with the largest templates and the widest memory
reads benefit most:

| Persona | Template | Today's memory instruction | After |
| --- | --- | --- | --- |
| Architect | ~8 KB | Read all shared + repo folder | Core inline + planning leaves for the vessel |
| Judge | ~7 KB | Read all shared + repo folder | Core inline + review and proof leaves |
| Worker | 2-4 KB | Read all shared + repo folder | Core inline + the vessel's fidelity leaves |
| TestEngineer | 2-4 KB | Read all shared + repo folder | Core inline + the vessel's test leaves |

A reviewer persona (Judge, Architect) reads broadly and reasons over the whole
brief, so trimming its optional set gives the largest token saving without
touching a rule it must apply.

## 8. Migration and the sole-memory-source contract

AI-Memory stays the authoritative source of every rule. Chunking is a
presentation and retrieval layer over the same content. The manifest is
generated from AI-Memory and the docs; it never becomes a second copy of a
rule.

The sole-memory-source contract lists four loaders that must change together
when the active memory set changes: `shared/INDEX.md`, the workspace
`CLAUDE.md`, the workspace `AGENTS.md`, and `opencode.json`. This design adds a
FIFTH generated artifact and its generator to that list:

5. The context manifest, regenerated from the chunk front-matter.

What changes together:

- A chunk added, removed, or moved — regenerate the manifest.
- A chunk's `tier` changed to or from `core` — regenerate the manifest AND
  update the core bundle that ships inline. A core change is an owner-reviewed
  change, like any loader change.
- The active memory set changed — the four loaders stay in sync as today, and
  the manifest regenerates in the same commit.

Retrieval is added alongside the always-on core. It never replaces the core.
Turn retrieval off and the core still ships, so a captain or orchestrator with
no retrieval still has every safety rule.

## 9. Rollout and measurement

### Phased rollout

- Phase 0 — Tag the existing docs and memory with front-matter. Build the
  manifest generator. No behavior change. Measure the baseline.
- Phase 1 — Add the docs-and-memory mode to `armada_context_pack`,
  deterministic only, Jev off. Orchestrators opt in.
- Phase 2 — Slim one persona brief (Judge or Architect) behind a flag. Ship
  core inline plus a retrieval call. Measure against the baseline.
- Phase 3 — Define and owner-review the core allowlist. Ship core-inline plus
  retrieval for every persona.
- Phase 4 — Enable `context_route` (Jev) in Shadow, then Gate after the
  four-week review.

Each phase is reversible. A flag turns the phase off and the eager load
returns.

### Measurement plan

Measure two numbers, before and after each phase.

- Context bytes per orchestrator session: the always-on load an orchestrator
  reads before its first record. Baseline ~462 KB (~115k tokens). Target: the
  core plus the first retrieval, on demand.
- Context bytes per persona brief: template plus playbooks plus the memory the
  brief pins. The `mission.prompt_budget` event already records the brief bytes
  written. Add three fields to it: core bytes, leaf bytes, and retrieved bytes,
  so a review can read the split per mission.

Report a before-and-after table per persona and per orchestrator runtime. The
success signal is a smaller always-on load with no increase in
`BriefContradiction` papercuts (a rule that a captain needed but did not
receive shows up there).

## 10. Non-negotiables (summary)

1. The core is never retrieved. It ships inline, always, on both sides.
2. Retrieval only ever widens beyond the core. It never gates a core rule.
3. Jev orders and widens the optional set. It never drops a floor leaf and
   never touches core. It is Off by default, recorded, and redacted.
4. AI-Memory stays authoritative. The manifest is generated, never a second
   copy of a rule.
5. A degraded retrieval fails safe: every core chunk, plus a conservative
   superset of leaves.

## 11. Open questions for the owner

1. The exact core allowlist. Which rules are non-negotiable core, and which are
   operational leaves? The boundary needs an owner ruling per rule.
2. The retrieval engine. Keyword-only at first, or an embedding index on the
   server? An embedding index adds a dependency and a non-determinism surface.
3. A captain-facing fetch-more tool. Operator-control tools stay out of mission
   scope. A read-only docs-retrieval tool looks safe, but it needs the owner's
   yes before a captain gets it.
4. Where the AI-Memory chunks are generated. Into the AI-Memory repository, or
   at admiral startup from AI-Memory? The choice touches the sole-memory-source
   contract.

## Approved core allowlist (owner-approved 2026-09-16)

The bar for CORE: a rule is core (always inline, never retrieved) only if its
ABSENCE on a task could cause a leak, an unsafe or unauthorized outward action,
an unproven success claim, a destructive remote/git operation, or dispatching
Armada work that must be direct-edit. If the harm cannot be named, it is a leaf.

CORE (always-on, ~11 rules, ~18-25 KB total):

1. Repository boundary and leak-prevention, in full: the scan list and the
   owner approval gate. (`shared/repository-boundary-and-leak-prevention.md`)
2. Never write keys, seeds, passwords, or tokens into memory or a repository;
   API keys live only in the admiral environment.
3. Land-then-sync HARD LIMITS: never push upstream, never force-push, never
   auto-push mission branches or open PRs. (`shared/land-then-sync.md`)
4. Stop before shared-state or outward actions; present options and wait for
   owner authority. (`shared/unified-project-memory.md`)
5. Proving a fix: reproduce the symptom; a self-reported success is not
   evidence. (`shared/unified-project-memory.md`)
6. Domain scope and hard guardrails: the owner's domain work is authorized (do
   not false-refuse); a destructive firmware-write path is banned.
7. Armada is direct-edit only: never dispatch Armada voyages or rescues for
   Armada bugs. (`repos/armada/README.md`)
8. Typed-decision non-negotiables: the model never approves, lands, dispatches,
   deletes, or silences. (`repos/armada/typed-decisions.md`)
9. ASD-STE100 reporting style. (`shared/unified-project-memory.md`)
10. Sole-memory-source pointer: AI-Memory is canonical; the loaders change
    together. (`shared/sole-memory-source.md`)
11. The index/map plus how to retrieve more.

MUST_RETRIEVE leaves (safety-shaped, per-repo): rules that only apply to one
repository but whose absence is costly stay LEAF, tagged `must_retrieve` for
their domain, so retrieval always includes them when the task is in that domain
rather than only on a keyword match. First set: a protocol vessel's hang-escalation rule (a
source-defect hang is a denial of service in a bench tool) and its
source-fidelity rules (ground-truth vectors, reproduce-do-not-correct).

Everything else is a plain LEAF, fetched by topic on demand: deploy procedure,
platform failure taxonomy, model tiering and captain roster, dispatch-preflight
detail, asset and playbook rules, the decision-corpus capture rule, audit
methods, the other per-repo porting rules, and the host notes.
