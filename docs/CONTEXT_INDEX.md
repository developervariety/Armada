# Context Index

The admiral generates a **context index** at startup and uses it for scoped
retrieval through `armada_fetch_context`. Optional brief slimming uses the same
retrieval service and defaults to off. With slimming off, briefs instruct
captains to read the configured memory files.

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
  `tier: core` chunk under a short header. Brief slimming includes this text
  inline when enabled.

## How a chunk is formed

For each source file:

1. If the file carries chunk **front-matter** (a leading `---` block with
   `topic`, `summary`, `read_when`, `applies_to`, `tier`, and optional
   `must_retrieve`), that front-matter defines one chunk (the `docs/ops/`
   chapters carry front-matter). A front-matter LEAF larger than the sub-chunk
   threshold is split like any other large leaf (below).
2. Otherwise the **tier configuration** decides:
   - A whole-file-core source becomes one `core` chunk.
   - A section-anchored (mixed) source is split at its `##` headings; only the
     configured sections are `core`, and the rest of the file is `leaf`.
   - Any other source becomes one `leaf` chunk.
3. **Large leaves are sub-chunked.** A leaf whose body exceeds the sub-chunk
   threshold (`ContextIndexGenerator.LeafSubChunkThresholdBytes`, 8 KB) is split
   at its section headings into per-section leaf chunks, and a section still over
   the threshold is split again at the next-deeper heading level, so a single
   load-bearing rule is a small, individually retrievable leaf. A small file, or
   a file with no section headings, stays one chunk; a `core` chunk is never
   sub-chunked. Each section leaf inherits the file's `applies_to` and
   `must_retrieve`. A leaf section that holds only its heading (a parent heading
   whose content sits in its sub-sections, or a title with no preamble) carries
   nothing to read and is not indexed.

Output is deterministic: text is normalized to LF, chunks are ordered by topic,
the core bundle is ordered by the allowlist sequence then topic, and neither
artifact carries a timestamp or a host path. Two runs over the same input are
byte-identical.

## The chunk-metadata sidecar

The auto-derived metadata is thin: a leaf's `summary` is its leading heading,
its `read_when` is empty, and its `must_retrieve` is empty. An optional
**sidecar** enriches that without editing AI-Memory, so the memory files stay
the sole durable memory source. The sidecar is an operator-local, gitignored file (never committed, since it can name real vessels),
`docs/context-index/chunk-metadata.json`, resolved automatically as the default
file under the docs root (an explicit path may be passed to the generator).

It is a JSON object with a `chunks` map from a chunk **id** (the manifest `id`,
which equals the chunk `topic`, for example `memory.repos.examplevessel.readme`) to an
override with any of `summary`, `read_when`, `applies_to`, and `must_retrieve`:

```json
{
  "version": 1,
  "chunks": {
    "memory.repos.examplevessel.readme": {
      "summary": "ExampleVessel porting rules: source fidelity, reproduce-the-defect, ground-truth vectors, and escalate a hang.",
      "read_when": "You are porting an ExampleVessel decoder or command, or reviewing an ExampleVessel port.",
      "applies_to": ["vessel:ExampleVessel"],
      "must_retrieve": ["examplevessel"]
    }
  }
}
```

The generator **merges** the sidecar over the auto-derived metadata: a sidecar
field wins where it is present, and the auto-derived value fills every gap. A
chunk the sidecar does not name is unchanged. The sidecar is metadata ABOUT the
chunks, never a copy of their content, and it carries no `tier`: it never
promotes or demotes a chunk, so the core allowlist and the always-on core bundle
are byte-identical with or without it. Loading is fail-open: a missing or
malformed sidecar is ignored and the index still generates.

`must_retrieve` is set only on a small set of safety-shaped per-vessel leaves
whose absence on a task is costly. The retrieval layer force-includes such a
leaf whenever the request's vessel, persona, or a requested topic matches one of
its domain tokens, even with no keyword match. A sidecar can tag a
vessel's memory leaf `must_retrieve` with that vessel's token, so its
source-fidelity and hang-escalation rules are always retrieved for that vessel's
task. Ordinary leaves
leave `must_retrieve` empty, and core rules are never listed (core already ships
inline, always).

## What is core

Core is an explicit, owner-approved allowlist (see `archive/design/context-system.md`,
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
6. Unified-memory Domain-scope (the owner's hard guardrails).
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

## Unified retrieval and the `armada_search_context` tool

`armada_search_context` is the shared query path for applicable guidance,
native memory and an existing vessel code index. It reads the current source
stores and the configured SQLite, PostgreSQL, MySQL or SQL Server driver. It
needs no other database or vector service.

Supply `query` and, for an operator call, `vesselId`. A mission credential fixes
the vessel to its active, owned mission and uses ordinary owner visibility even
when the mission owner is an administrator. A mission or vessel stored without
an owner belongs to the default user, as in every other ownership check.
Access checks run before ranking.
Vessel recall includes native records about or originating from that vessel and
visible records with no vessel association. The database applies the tenant and
vessel filter, so a query reads only that vessel's candidates. Records from
another tenant are excluded. Working facts retain memory authority; they cannot
become rules.

The response separates `Required` guidance from optional `Results`. Required
core and applicable mandatory leaves are returned in full. `limit` (default 20,
maximum 40) and `maxBytes` (default 24000) limit optional text only. Byte counts
use UTF-8. Oversized matches are skipped so smaller matches can still fit.
`Truncated` names omissions. Each item has a source class, source reference and
version. Code references include the source line and indexed commit, with index
freshness. Guidance versions are hashes of the loaded snapshot text.

Optional guidance and facts use local BM25 ranking over one shared candidate
list, so the strongest match leads whether it is a rule, a document or a fact.
Code uses the existing local lexical index, and its ranks join that list by
reciprocal rank. The admiral keeps a text-only copy of the most recently
searched indexes in memory and reloads one when its index file changes, so a
query does not re-read the stored embeddings. Search never creates a code
index. `Sources` distinguishes an available source with no
matches from a missing or failed source. Set `includeCode` to false to omit code
search.

### Local semantic ranking

`ContextRetrieval.SemanticEndpointId` names a registered Embedding model
endpoint. When it is set, guidance and native facts are also ranked by cosine
similarity, and the two rankings are fused by reciprocal rank. The query and
each candidate's title and text (at most 8,000 characters) go to that endpoint.
The text is private guidance and memory, so the endpoint must:

- be enabled, with a model;
- use the `Ollama` or `OpenAICompatible` provider; and
- address a loopback, private-network, link-local or unique-local address, a
  single-label host such as a container service name, or a name ending in
  `.internal`, `.local`, `.lan` or `.localhost`.

Any other endpoint is refused. The code-index embedding endpoint is never used
in its place. Search calls `<base URL>/v1/embeddings`, so an Ollama endpoint
registered at its server root works unchanged. For example, run Ollama beside
the admiral, pull an embedding model such as `nomic-embed-text`, register it as
an Embedding endpoint at `http://ollama:11434`, then set the pin.

Semantic ranking only reorders and widens recall within candidates the caller
can already see; access checks still run first. Vectors are stored in the
`context_embeddings` table on every provider, keyed by tenant, source and
model. A stored vector is reused only while its model, dimension and the
SHA-256 of the embedded text all match. Otherwise it is embedded again. One
query embeds at most 64 new candidates; the rest rank lexically until a later
query embeds them. When concurrent queries store a vector for the same key,
one row remains and neither query fails. Deleting a native record deletes its
vectors in the same transaction.

The `semantic` entry in `Sources` reports the result. When it is available,
`rules` and `memory` report `hybrid`. Otherwise it names the reason and search
stays BM25: `not_configured`, `endpoint_unavailable`, `endpoint_not_local`,
`embedding_failed`, `embedding_timeout` or `source_failed`. The endpoint's
`TimeoutMs` bounds each call. Without the pin, search makes no embedding call.

`ContextRetrieval.FetchToolEnabled` governs captain recall through the shared
query and compatibility tools. `MaxLeafBytesPerCall` caps optional query text.
Required guidance is outside the optional byte cap. `Sources` reports the
actual search mode and the code index freshness even when there are no hits.

One durable counter on each mission bounds calls across `armada_search_context`,
`armada_fetch_context`, `armada_mission_code_search` and mission-token calls to
`search_memory`. The effective maximum is `ContextRetrieval.MaxCallsPerMission`,
also capped by `CodeIndex.CaptainSearchMaxCallsPerMission` while code indexing is
enabled. This retains the existing code limit as a conservative installation
cap. An atomic owner-scoped transaction spends each call. Concurrent calls
cannot exceed the maximum. Restarts and normal mission updates cannot reset
usage. A terminal mission cannot spend calls. Operator calls without a mission
have no mission counter. A missing budget store fails the call.

### Recall consumers and compatibility

| Consumer | Retrieval contract |
| --- | --- |
| Built-in working personas, Recorder and Linter | Use `armada_search_context`; native results identify records for `get_memory` before reconciliation. The Linter states the lexical-search limit. |
| Generated slimmed brief | Names the shared query for additional guidance, facts and code. Delivered required files remain complete. |
| `armada_fetch_context` | Compatibility guidance-only response with exact topic preference. Uses the shared scope check and mission budget. |
| `search_memory` and native REST memory listing | Retain substring, type/topic filtering, paging and salience order for record management. Mission-token MCP calls use the credential's owned vessel and shared budget. |
| `armada_mission_code_search` | Compatibility code response with path/language filters and configured semantic behavior. Uses the shared scope check and budget. It can still call the configured embedding provider. |
| Operator code, graph and fleet tools | Retain structural and graph contracts over the same existing code index. No second index is created for the shared query. |
| Native memory writes | Keep key/upsert, version-conflict, ownership and provenance rules in `MemoryService`. |
| External guidance and historical evidence | Guidance remains a loaded, approved source snapshot. Native result evidence points to its source; archive storage and retention keep their own contracts. |

Only exact shipped prompt passages are upgraded inside operator-edited built-in
templates; other text is preserved. Unknown custom wording remains unchanged.
Compatibility tool names remain available for those clients. Tool removal
requires a verified client and template cutover. Import/rebuild proof and scale
measurements remain separate work. Authoring authority stays with the existing
source owners.

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
   for other vessels; an `all` leaf is always eligible — then ranked and filled
   into the leaf byte budget. The fill SKIPS a leaf that would exceed the
   remaining budget and keeps filling, so a smaller relevant leaf ranked below a
   large one is still included; ranked order and the budget cap are preserved, so
   the result stays deterministic.

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
it and the written manifest derive from the same source and agree.

## Brief slimming (flagged, default off)

By default, a captain brief's Shared Memory section names the memory root and
tells the captain to read every file under `shared/`. Retrieval can supply that
section instead, behind `contextRetrieval.briefSlimmingEnabled`:

- **`briefSlimmingEnabled` (default `false`).** While `false`, brief generation
  uses the full memory-reading instruction, and no retrieval runs for the brief. Enabling it is a separate, deliberate step.
- While `true`, the memory is built from a retrieval request scoped to the
  mission's persona and vessel, with a query from the mission's title and
  description: the always-on core rules, the mission's must-retrieve safety
  leaves, and ranked relevant leaves within
  `contextRetrieval.briefLeafBudgetBytes` (default 24000; core and must-retrieve
  are exempt). The memory is written into the dock as files under
  `_briefing/memory/`, not inline in the instruction file. Each file stays under
  9,000 bytes and 200 lines so a captain reads it in one call on every runtime;
  a chunk larger than that is split on line boundaries, and nothing is cut. The
  brief's Shared Memory section lists the files in reading order: **Read first**
  (core rules, then safety leaves and the leaves that apply), then **Reference**
  (leaves the `memory_relevance` typed decision judged not to apply to the
  mission's work; still delivered in full). The section ends with a one-line
  pointer to `armada_fetch_context` for anything else by topic. It never tells
  the captain to read every file under `shared/`, and it names only the
  dock-local `_briefing/memory/` folder, never the configured memory root, which
  is a host path that does not resolve inside a dock. A chunk that holds only its
  heading is never staged. A copy of the delivered files is kept beside the
  instruction snapshot, under `<mission id>.memory/`.
- **Memory is pinned per voyage.** The first slimmed brief in a voyage pins its
  section and files under `memory-pins/` in the data directory, and every later
  stage of that voyage receives the same bytes, across an Admiral restart too.
  Only a healthy slimmed delivery is pinned; a fallback to the full section is
  not, so a later stage retries retrieval. Pins older than 30 days are pruned
  when a new pin is written. `_briefing/memory/manifest.json` lists each staged
  file's path, bytes and SHA-256, and names the voyage and the mission that first
  staged it, so a later stage verifies its files against an earlier stage's
  report. Telemetry marks a pinned delivery with `PinnedFromVoyage`.
- **The mission description follows the same rule.** A description longer than
  the metadata cap (12,000 characters) is embedded as its head and newest
  handoff block, and the full text is written under `_briefing/mission/` in the
  same bounded files; the elision marker names them. Acceptance criteria and
  report references are pinned whole beside it, and at least 8,000 characters
  of that head and tail stay with them even when the pinned lines alone reach
  the cap, so large criteria never leave the captain without its role text.
  When the byte budget shrinks that bounded copy further, it shortens only the
  head and tail beside the pinned lines, so the role text stays at the head. A copy is kept under
  `<mission id>.mission/`. Neither counts against the instruction-file byte
  budget (`captainInstructionByteBudget`, default 65,536), so the budget
  bounds the brief itself; `docs/PERSONAS.md` describes its warning event.
- **A read of a staged file the dock does not hold is rewritten.** A line that
  tells the captain to read, open or see a `_briefing/` file that is not staged
  (a range of `_briefing/mission/` parts copied from another brief, a handoff
  path in plain text) points at the complete handoff text under
  `_briefing/handoff-full/` when one is staged, and otherwise at the inline
  text of the brief. Other absent paths are listed under
  `## Named Files Not Staged`.
- **Fail-safe.** When no context index was built, or retrieval returns a
  degraded (fail-safe) result or no core, the section falls back to the full
  read-every-file memory section and logs a warning. A failure therefore
  degrades to today's behaviour, never to fewer rules, and never to an empty
  section.
- **Telemetry.** When the flag is on, the core, must-retrieve, and leaf byte
  counts, the delivered file count and bytes, the number of reference leaves,
  the leaf-sort outcome, and any fallback are recorded under `ContextSlimming`
  on the `mission.prompt_budget` event.

The flag is runtime-tunable: it is merged in place on a settings hot reload, so
it can be turned on or off from the watched settings file without an Admiral
restart. Retrieval, the fetch tool, and their wiring stay additive: with the
flag off, brief generation and every loader are unchanged.
