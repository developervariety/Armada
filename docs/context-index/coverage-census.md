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

This run measures the tree AFTER the coverage-remediation change (large-leaf
sub-chunking in the generator, the over-budget-leaf skip in retrieval, and the
per-vessel `must_retrieve` tags in the sidecar). The earlier BLOCKED run is
kept in git history.

## Decision criteria and result

| Criterion | Requirement | Measured | Verdict |
| --- | --- | --- | --- |
| Safety recall | 100% (required) | 100% (11 core chunks and every domain-matched must_retrieve chunk, over 10 representative requests) | PASS |
| Failure-replay regressions among mapped cases | 0 (required) | 0 of 15 at the realistic 16 KB leaf budget | PASS |
| read_when leaf recall | >= 90% (target) | 100% (62 of 62 leaves) | PASS |
| Byte reduction | reported | min 78.4%, median 86.2%, max 86.2%; median 16 retrieved leaves | reported |

**Overall: PASS.** Every required criterion passes and the target criterion is
met. The always-on core, the per-vessel `must_retrieve` safety paths, and the
sub-chunked orchestrator sources are all proven. The brief-wiring step is
unblocked.

The index at the measured commit holds 746 chunks: 11 core (about 21.8 KB
total) and 735 leaves. 62 leaves carry a read_when trigger. Three leaves are
larger than the 16 KB realistic leaf budget; none is a core, a must_retrieve,
or a mapped-failure chunk, so none can regress a mapped case (they are large
reference sections in low-traffic docs whose own headings do not sub-divide
them further).

## What the remediation changed

The earlier run was BLOCKED because several load-bearing rules lived in
oversized whole-file leaves that no realistic leaf budget could retrieve, and a
budget fill that stopped at the first over-budget leaf starved smaller relevant
leaves ranked below it. Three changes remove that root cause:

1. **Generator sub-chunking.** Any leaf whose body exceeds a named threshold
   (`LeafSubChunkThresholdBytes`, 8 KB) is split at its section headings into
   per-section leaf chunks, and a section still over the threshold is split
   again at the next-deeper heading level. A single load-bearing rule is now a
   small, individually retrievable leaf. Small files and core chunks are never
   sub-chunked, so the core allowlist and the core bundle are unchanged.
2. **Retrieval over-budget skip.** The leaf fill now SKIPS a leaf that would
   exceed the remaining budget and keeps filling, so a smaller relevant leaf
   ranked below a large one is still reached. Ranked order and the budget cap
   are preserved, so retrieval stays deterministic.
3. **Per-vessel must_retrieve tags.** The sidecar tags each per-vessel
   source-fidelity / safety section `must_retrieve` for its own vessel domain,
   as EcuLink already was. A safety-shaped rule for a vessel is therefore
   budget-exempt and always delivered for that vessel's tasks.

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

Result: **PASS.** All 11 core chunks were returned in every request. Each
vessel case returned its vessel's must_retrieve safety chunks at a zero budget.
The invariant is pinned by the registered unit test, which fails the build if
any core or must_retrieve chunk is ever dropped by budget, filter, ranking, or
error.

## Part 2 — read_when recall (deterministic)

For each leaf that carries a read_when trigger, the census synthesizes a query
from that trigger text, runs retrieval under the leaf's own applies_to scope at
a generous 32 KB leaf budget, and checks the leaf is returned.

Result: **62 of 62 = 100%** (target 90% met, no misses). Sub-chunking the large
sources removed every structural miss the earlier run reported: the rule a
trigger names is now a small leaf that fits the budget, and the over-budget
skip stops a large leaf ranked above it from starving it.

## Part 3 — Byte reduction

For each of 55 recent task descriptions, the census compares the slimmed load
(core plus matching must_retrieve plus the ranked leaves at the 16 KB budget)
against the eager baseline an orchestrator reads today.

- Eager baseline: 276,044 bytes (AI-Memory imports 170,091 + the curated docs
  the orchestrator is told to read 66,861 + README.md and CLAUDE.md 39,092).
  The curated-docs figure is much smaller than the earlier run because
  `armada-ops.md` is now a thin index and its content moved to the sub-chunked
  `docs/ops/` chapters.
- Core (the fixed part of every slimmed load): 21,808 bytes.

| Statistic | Value |
| --- | --- |
| Reduction, minimum | 78.4% |
| Reduction, median | 86.2% |
| Reduction, maximum | 86.2% |
| Retrieved leaves, median | 16 |

Every sampled task cut the eager load by at least 78.4%. The median retrieved-
leaf count is higher than the earlier run because the over-budget skip now
fills the budget with several small section leaves instead of stopping early;
the slimmed load is a set of small, precisely relevant leaves rather than one
or two big ones, and the reduction stays large.

## Part 4 — Failure replay (best-effort, real)

The census sampled the 35 most recent distinct papercut signals from the
production database. Each was mapped, conservatively and by hand, to the memory
or docs chunk that encodes the rule the failure needed; a signal that could not
be confidently mapped was counted as unmapped, not forced. The census then
checked whether the slimmed brief for that task's domain would carry the chunk
(core, or retrieved for the domain within the realistic budget).

Result at the 16 KB realistic budget: **mapped 15, regressions 0, unmapped 20.**

Mapping summary by class (aggregate; no signal text or identifiers recorded):

| Signal class mapped | Chunk domain | Carried |
| --- | --- | --- |
| EcuLink source-fidelity / citation-shape contradictions | EcuLink source-fidelity sections (must_retrieve) | yes (all) |
| SourceGlossary sibling-tree / root-resolution failures | SourceGlossary ledger-gate section (must_retrieve) | yes (all) |
| Brief-premise / stale-git-anchor contradictions | session-workflow capture-and-dispatch sub-leaves (orchestrator) | yes (all) |
| Stage-handoff / rescue-brief truncations | armada README briefs-and-captain-instructions (orchestrator) | yes (all) |
| Doc-vs-source and green-gate-ran-nothing lessons | armada README doc-comment / build-and-test (orchestrator) | yes (all) |
| Stale-checkout / dock-sibling | armada README host-and-container-facts (orchestrator) | yes |

The 20 unmapped signals are mostly dock and environment provisioning failures
on vessels that have no memory chunk; no existing rule prevents them, so
slimming cannot regress them.

Budget sensitivity: the regression count is 0 at the 16 KB realistic budget and
stays 0 at wider budgets. The two mechanisms that caused the earlier floor of 3
are both removed: the needed rules are now small sub-chunked leaves, and the
per-vessel safety rules are budget-exempt must_retrieve leaves.

## Synthesis

- The safety guarantee holds. The core and every per-vessel must_retrieve
  safety chunk are always delivered. Byte reduction stays large (about 86%
  median) while the slimmed load is now a set of small, precisely relevant
  leaves.
- The read_when misses and the failure-replay regressions of the earlier run
  are both resolved by the three remediation changes above. No required
  criterion fails.
- The brief-wiring step is unblocked for the orchestrator and for every managed
  vessel, not only the always-on core and the EcuLink path.
