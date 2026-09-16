# Context-Retrieval Coverage Census

This census produces objective numbers that gate whether the orchestrator and
captain briefs can be slimmed onto the progressive-disclosure context system
(`docs/design/context-system.md`). It measures the built context index and the
retrieval service; it renders no opinion. It is the gate for the later
brief-wiring step: brief wiring proceeds only when the required criteria pass.

The harness lives in `Armada.Core/Context/Census/ContextCoverageCensus.cs`. The
safety-recall invariant is a registered unit test
(`test/Armada.Test.Unit/Suites/Context/ContextCoverageCensusTests.cs`). The two
database-sampling parts run by hand.

## Decision criteria and result

| Criterion | Requirement | Measured | Verdict |
| --- | --- | --- | --- |
| Safety recall | 100% (required) | 100% (11 core chunks and every domain-matched must_retrieve chunk, over 10 representative requests) | PASS |
| Failure-replay regressions among mapped cases | 0 (required) | 5 of 14 at the realistic 16 KB leaf budget (3 at a generous 64 KB) | FAIL |
| read_when leaf recall | >= 90% (target) | 93.6% (44 of 47 leaves) | PASS |
| Byte reduction | reported | min 87.8%, median 92.2%, max 95.3%; median 5 retrieved leaves | reported |

**Overall: BLOCKED.** One required criterion fails: failure replay shows
regressions. The always-on core and the EcuLink must_retrieve safety path are
proven and ready. The orchestrator brief and the non-EcuLink vessel briefs are
NOT safe to slim yet, because several load-bearing rules live in oversized
whole-file leaves that no realistic leaf budget retrieves. The remedy is in the
synthesis below and is a prerequisite of the brief-wiring step.

The index at the measured commit holds 110 chunks: 11 core (about 21.3 KB
total) and 99 leaves. 47 leaves carry a read_when trigger. 14 leaves are larger
than the 16 KB realistic leaf budget.

## Method and reproduction

The census builds the index over the live AI-Memory tree and the repository
`docs/` tree with `ContextIndexGenerator`, then drives `ContextRetrievalService`.

- Parts 1 and 2 are deterministic: no model, no clock, no randomness. A fixed
  index yields a fixed report.
- Parts 3 and 4 sample the production database read-only, so their inputs are a
  point-in-time snapshot; the computation over a fixed sample is deterministic.
  The numbers below are from one run over 55 recent task descriptions and 35
  recent papercut signals.

Reproduce the deterministic parts (safety recall is also a gate test):

```
dotnet run --project test/Armada.Test.Unit -- --suite ContextCoverageCensus
```

Reproduce the sampling parts with exported samples:

```
ARMADA_CENSUS_REPORT=1 \
  ARMADA_CENSUS_TASKS_FILE=<tasks-per-line> \
  ARMADA_CENSUS_MAPPINGS_FILE=<label,vessel,query,topic TSV> \
  ARMADA_CENSUS_UNMAPPED=<n> \
  dotnet run --project test/Armada.Test.Unit -- --suite ContextCoverageCensus
```

Budgets used: read_when recall ran at a generous 32 KB leaf budget; byte
reduction and failure replay ran at a realistic 16 KB leaf budget. The core
and every matching must_retrieve leaf are budget-exempt in all parts.

## Part 1 — Safety recall (invariant, must be 100%)

For each representative request the retrieval result must carry EVERY tier=core
chunk and EVERY must_retrieve chunk whose domain matches the request. This is a
guarantee, not a sample.

Representative requests (10): one per managed vessel with a memory chunk
(EcuLink, OtrBuddy, JproDeobfuscator, SourceGlossary), one per persona
(Architect, Worker, TestEngineer, Judge), one orchestrator request, and one
no-query request. Every request ran at a zero leaf budget, so the core and the
must_retrieve set prove they are budget-exempt.

Result: **PASS.** All 11 core chunks were returned in every request. The
EcuLink case returned the EcuLink must_retrieve safety chunk
(`memory.repos.eculink.readme`) every time, including at a zero budget. The
invariant is pinned by the registered unit test, which fails the build if any
core or must_retrieve chunk is ever dropped by budget, filter, ranking, or
error.

## Part 2 — read_when recall (deterministic)

For each leaf that carries a read_when trigger, the census synthesizes a query
from that trigger text, runs retrieval under the leaf's own applies_to scope at
a generous 32 KB leaf budget, and checks the leaf is returned.

Result: **44 of 47 = 93.6%** (target 90% met). The 3 misses:

| Missed leaf | Cause |
| --- | --- |
| `docs.armada-ops` | 183 KB single leaf; larger than any realistic budget, so it can never be retrieved whole. |
| `memory.repos.armada.session-workflow` | 38.8 KB single leaf; larger than the 32 KB budget. |
| `docs.testing` | 15 KB leaf; starved when a larger leaf ranked above it halts the budget fill. |

Every miss is structural, not a safety gap: the trigger ranks the leaf highly,
but the leaf either does not fit the budget or is starved by a larger leaf
ranked above it. Sub-chunking the large sources (design Phase 0) removes all
three.

## Part 3 — Byte reduction

For each of 55 recent task descriptions, the census compares the slimmed load
(core plus matching must_retrieve plus the ranked leaves at the 16 KB budget)
against the eager baseline an orchestrator reads today.

- Eager baseline: 463,269 bytes (AI-Memory imports 170,085 + the curated docs
  the orchestrator is told to read 247,219 + README.md and CLAUDE.md 45,965).
  This matches the design's measured ~462 KB.
- Core (the fixed part of every slimmed load): 21,808 bytes.

| Statistic | Value |
| --- | --- |
| Reduction, minimum | 87.8% |
| Reduction, median | 92.2% |
| Reduction, maximum | 95.3% |
| Retrieved leaves, median | 5 |

Every sampled task cut the eager load by at least 87.8%. The median slimmed
load is about 36 KB against the 463 KB baseline. A task with no leaf match
loads only the 21.8 KB core (95.3%).

## Part 4 — Failure replay (best-effort, real)

The census sampled the 35 most recent distinct papercut signals from the
production database. Each was mapped, conservatively and by hand, to the memory
or docs chunk that encodes the rule the failure needed; a signal that could not
be confidently mapped was counted as unmapped, not forced. The census then
checked whether the slimmed brief for that task's domain would carry the chunk
(core, or retrieved for the domain within the realistic budget).

Result at the 16 KB realistic budget: **mapped 14, regressions 5, unmapped 21.**

Mapping summary by class:

| Signal class mapped | Chunk | Domain | Carried |
| --- | --- | --- | --- |
| 5 EcuLink source-fidelity contradictions/gaps | `memory.repos.eculink.readme` | EcuLink | yes (all 5; must_retrieve, budget-exempt) |
| 2 SourceGlossary sibling-tree failures | `memory.repos.source-glossary.readme` | SourceGlossary | 1 of 2 |
| 3 stage-handoff / rescue-brief truncations | `...readme.briefs-and-captain-instructions` | orchestrator | 2 of 3 |
| 3 stale-premise / stale-git-anchor contradictions | `memory.repos.armada.session-workflow` | orchestrator | 1 of 3 |
| 1 stale-checkout / dock-sibling | `...readme.host-and-container-facts` | orchestrator | yes |

The 21 unmapped signals are mostly dock and environment provisioning failures
on vessels that have no memory chunk; no existing rule prevents them, so
slimming cannot regress them.

Budget sensitivity of the regression count:

| Leaf budget | Regressions among 14 mapped |
| --- | --- |
| 16 KB (realistic) | 5 |
| 32 KB (generous) | 4 |
| 64 KB | 3 (floor) |

The floor of 3 persists at any realistic budget. All 5 regressions are the same
mechanism: a load-bearing rule lives in an oversized whole-file leaf, and either
(a) the leaf is larger than the budget, or (b) a still-larger leaf ranked above
it halts the budget fill before the needed leaf is reached (the retrieval fill
stops at the first over-budget leaf). At the 64 KB budget the 3 that remain
include a 2.4 KB target chunk that is starved by a larger leaf ranked above it,
which shows the fill behavior, not the target size, is the deeper cause.

The EcuLink source-fidelity path had zero regressions at every budget, because
its rule is a must_retrieve leaf and is budget-exempt.

## Synthesis and recommended fixes

- The safety guarantee holds. The core and the EcuLink must_retrieve safety
  chunk are always delivered. Byte reduction is large (about 92% median).
- The read_when misses (3) and the failure-replay regressions (5) share one
  root cause: a few oversized whole-file leaves — chiefly `docs.armada-ops`
  (183 KB) and `memory.repos.armada.session-workflow` (38.8 KB) — that no
  realistic leaf budget can retrieve and that starve smaller relevant leaves
  ranked below them.
- Prerequisites of the brief-wiring step, in order:
  1. Sub-chunk the large orchestrator sources into section leaves with
     front-matter (design Phase 0), so the specific needed rule is a small,
     retrievable leaf. This alone removes every read_when miss and every
     orchestrator regression.
  2. Consider tagging each per-vessel fidelity readme `must_retrieve` for its
     own domain, as EcuLink already is. The EcuLink path proves the mechanism
     is the reliable one; SourceGlossary, without it, regressed.
  3. Consider changing the retrieval leaf fill to skip an over-budget leaf and
     keep filling, rather than stop at the first over-budget leaf. This is a
     retrieval-service change, outside this census.

Until at least the first fix lands, brief slimming is safe only for the
always-on core and the EcuLink must_retrieve path, not for the orchestrator or
the non-EcuLink vessel briefs.
