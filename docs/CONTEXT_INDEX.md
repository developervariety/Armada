# Context Index

The admiral generates a **context index** at startup. It is the first, additive
piece of the progressive-disclosure context system (design:
`docs/design/context-system.md`). This piece builds the generator and its
startup wiring only. Retrieval, per-persona brief slimming, and any loader
change are later work. Nothing here changes how memory loads today.

## What it produces

At startup the admiral reads two source trees and writes two artifacts.

Sources (read only):

- The AI-Memory tree under the configured `aiMemoryRoot`: `shared/`, `repos/`,
  and `machine-notes/`. The live root is mounted read-only; the generator only
  reads it.
- The Armada `docs/` tree.

Artifacts, written under `<dataDir>/context-index/`:

- `manifest.json` — the machine index. An array of chunk entries, each with
  `id`, `topic`, `path`, `summary`, `read_when`, `applies_to`, `tier`
  (`core` or `leaf`), `must_retrieve`, and `bytes`. Paths are logical and
  root-relative (for example `AI-Memory/shared/land-then-sync.md` or
  `docs/armada-ops.md#build-and-test`), never host-absolute.
- `context-core.md` — the derived **core bundle**: the concatenation of every
  `tier: core` chunk under a short header. This is the always-on text the later
  phases ship inline in every session and brief.

## How a chunk is formed

For each source file:

1. If the file carries chunk **front-matter** (a leading `---` block with
   `topic`, `summary`, `read_when`, `applies_to`, `tier`, and optional
   `must_retrieve`), that front-matter defines one chunk. Today's files carry
   no front-matter; this is the seam for later sub-chunked files.
2. Otherwise the **tier configuration** decides:
   - A whole-file-core source becomes one `core` chunk.
   - A section-anchored (mixed) source is split at its `##` headings; only the
     configured sections are `core`, and the rest of the file is `leaf`.
   - Any other source becomes one `leaf` chunk.

Output is deterministic: text is normalized to LF, chunks are ordered by topic,
the core bundle is ordered by the allowlist sequence then topic, and neither
artifact carries a timestamp or a host path. Two runs over the same input are
byte-identical.

## What is core

Core is an explicit, owner-approved allowlist (see `docs/design/context-system.md`,
"Approved core allowlist"). A model never promotes or demotes a chunk; only the
tier configuration does, and only the owner edits it. A rule is core only when
its absence on a task could cause a leak, an unsafe or unauthorized outward
action, an unproven success claim, a destructive remote or git operation, or
dispatching Armada work that must be direct-edit. The v1 core is eleven chunks:

1. Repository boundary and leak-prevention (whole file).
2. Land-then-sync, including its hard limits (whole file).
3. Typed-decision non-negotiables (whole file).
4. Unified-memory Boundaries (keys stay in the environment; stop before
   outward actions).
5. Unified-memory Proving-a-fix.
6. Unified-memory Domain-scope (the UDS `0x34` reflash ban).
7. The Armada direct-edit rule (README "Where Armada runs").
8. Unified-memory Reporting-style (ASD-STE100).
9. The sole-memory-source pointer.
10. The sole-memory-source four-loaders rule.
11. A synthesized index-and-retrieval chunk describing the map and how to fetch
    more.

The core bundle over-includes a safety file rather than under-include a rule:
land-then-sync and typed-decisions ship in full in v1. The tier configuration
already supports narrowing either to a named section later.

## Startup behavior

Generation runs once, late in startup, after settings and services load. It is
fail-open: any failure logs a warning and startup continues. It logs a
one-line summary: chunk count, core count, core bytes, and total bytes. The
docs root is resolved from `ARMADA_DOCS_ROOT`, or by probing for a `docs`
folder that holds `armada-ops.md`; when no docs root is found the generator
indexes AI-Memory alone, which still yields the whole core bundle because every
core rule is an AI-Memory rule.

## Regeneration and the sole-memory-source contract

The manifest is generated from AI-Memory and the docs; it is never a second
copy of a rule, and it is never hand-edited. When the active memory set
changes, the four loaders stay in sync as today; the manifest regenerates on
the next admiral start. A change to what is core is an owner-reviewed change to
the tier configuration.

## Retrieval and the `armada_fetch_context` tool

`ContextRetrievalService` is the retrieval layer over the built index. It takes
a request — free query text and/or explicit topics, the requesting persona, the
vessel, and a leaf byte budget — and returns an ordered result in three
disjoint sets:

1. **Core.** Every `tier: core` chunk, in the bundle order, first, and never
   counted against the leaf budget. The core is never filtered and never
   dropped: retrieval only ever widens beyond it.
2. **Must-retrieve.** Every leaf whose `must_retrieve` domain matches the
   request's vessel, persona, or a requested topic (a vessel's safety leaves).
   Also never budget-limited.
3. **Leaves.** The remaining leaves, filtered by `applies_to` — a persona
   request excludes leaves for other personas; a vessel request excludes leaves
   for other vessels; an `all` leaf is always eligible — then ranked and cut to
   the leaf byte budget.

Ranking is deterministic: a keyword and topic match over each chunk's topic,
summary, read-when, and applies-to metadata, plus a light body match capped so
a long body cannot outweigh a precise metadata hit, with ties broken by topic
id. The ranker sits behind an injectable `IContextLeafRanker`, the seam a
future typed relevance decision (`context_route`) plugs into to re-order and
widen the leaf set — it may only add or re-order, never drop a floor leaf and
never touch core. Any error fails safe: every core chunk plus a conservative
leaf superset, never zero core, and never an exception into the caller.

`armada_fetch_context` is the captain-facing tool over the service. It is
mission-scoped, read-only, and informative: a captain gives a `query` and/or a
`topic` and its `missionId`, and the tool returns the relevant leaf bodies for
that query (the core already ships inline in the brief, so the tool returns only
leaves and the vessel's must-retrieve safety leaves). It resolves the caller's
vessel and persona from the mission, so the leaf set is scoped without the
captain naming them. It is bounded by a per-mission call budget from the
`contextRetrieval` settings; when the budget is spent it returns a clear budget
message. It writes nothing to any Armada record and has no side effect — it
logs only the query length and a short hash, never the raw query — so authority
never travels with the tool. It is enabled by default because it makes no
external call and returns only already-sanitized memory and docs text.

The service is built in-process at startup by reusing the index generator, so
it and the written manifest derive from the same source and agree. Retrieval,
the fetch tool, and their wiring are additive: brief generation and every
loader are unchanged.
