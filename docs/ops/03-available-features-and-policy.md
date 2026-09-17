---
topic: "Available Features And Active Policy"
summary: "Which optional features a deployment enables, the setting that gates each, and the fallback when one is off."
read_when: "Before depending on a feature such as code indexing, autonomous recovery, or a landing mode."
applies_to: orchestrator
tier: leaf
---
# Available Features And Active Policy

The repository contains features that an operator can disable. Documentation
of a feature does not mean that a deployment enables it.

Check these settings before you depend on the related workflow:

| Setting or record | Effect |
| --- | --- |
| `codeIndex.enabled` | Enables code search, graph search, and context packs. |
| `codeIndex.dispatchStalenessPolicy` | How dispatch reacts to a stale/updating index: `Proceed` (default, dispatch now + background refresh), `RefreshInline` (incremental refresh then dispatch), or `Block` (wait for Fresh, the legacy behaviour). |
| `seedDockRuntimeMcpConfig` | Gives supported captains the local Armada MCP URL through runtime-appropriate dock or launch configuration. Default: enabled. |
| `apiCaptainCloudProviders` | Lists the hosted providers (`OpenAI`, `Anthropic`, `Gemini`) an API-endpoint captain may run against. Default: empty, so only operator-hosted `Ollama` and `OpenAICompatible` endpoints run. Azure OpenAI, Vertex AI and Bedrock are not available. |
| `autonomousRecovery.enabled` | Enables bounded server-side mission recovery. |
| `incidentLifecycle.enabled` | Enables evidence-driven incident transitions. |
| `remoteTrigger.enabled`, mode, and `agentWake.deliveryMode` | Enables AgentWake process and/or signal delivery. |
| Objective `AutoDispatchEnabled` and scheduler state | Enables autonomous objective dispatch. |
| Vessel or voyage landing mode | Selects `LocalMerge`, `PullRequest`, `MergeQueue`, or `None`. |
| Vessel default pipeline | Selects the normal persona path. |
| Workflow profile | Defines the commands that Checks and delivery operations run. |

Code-index embeddings use the Voyage AI client by default (model
`voyage-code-3`, base URL `https://api.voyageai.com/v1`), with the embedding key
supplied from the environment. With no key the embedding client is inert and
search falls back to the checkout. Summarization uses its own configured
inference endpoint, unchanged.

The embedding provider can instead be a registered **Embedding model endpoint**:
when one is enabled, the code index uses its base URL, model, and server-side
key, so the provider and key are managed on the model-endpoints surface rather
than in settings or the environment. `codeIndex.embeddingEndpointId` pins one
when several are enabled; with none registered the `codeIndex` embedding
settings above apply. The endpoint is resolved at startup, so add or change it
then restart the admiral.

Source in brace-delimited languages (C#, Java, JavaScript, TypeScript, Go, Rust,
Kotlin, C, C++, Swift, Scala, Dart, PHP) is chunked at declaration boundaries
when `codeIndex.structuralChunking` is on (the default): a member with at least
`codeIndex.duplicateMinLines` non-blank lines (default 6) is one chunk with its
doc comment, attributes and signature, smaller neighbouring members are packed
together up to `codeIndex.maxChunkLines`, and a member larger than that budget is
split at its inner blocks. Two copies of a method then produce the same chunk
wherever they sit. A file whose braces do not balance, and every other language,
uses fixed line windows. Expect several times more chunks than line windows on a
method-heavy repository. Changing either setting rebuilds the index on the next
update.

Dispatch never waits on a reindex by default. When a voyage dispatches to a
vessel whose index is stale (a landing moved the branch) or mid-refresh,
`codeIndex.dispatchStalenessPolicy` decides: `Proceed` (default) dispatches
immediately against the current index and schedules a debounced background
refresh, so the context pack may trail the newest landed commit until the
refresh lands; `RefreshInline` runs an incremental refresh (only changed files
re-embed) bounded by the dispatch timeout, then dispatches, falling back to
Proceed on timeout; `Block` keeps the legacy strict wait (dispatch is refused
until the index is Fresh). The debounced post-land refresh and the periodic
staleness sweep keep indexes warm regardless.

`armada_code_duplicates` compares one vessel's indexed chunks and returns groups
of similar code with path and line ranges. It pairs chunks with identical content
(after trimming each line), and, when the index carries embedding vectors, chunks
whose cosine similarity is at least `codeIndex.duplicateSimilarityThreshold`
(default 0.92); pairs join transitively into groups. The comparison is exhaustive,
runs as a background job, and skips reference-only chunks and chunks under
`codeIndex.duplicateMinLines`. Its report lists what it compared and left out,
and warns when the index is stale, has no vectors (identical content only), or
stopped at its pair cap. A missing or failed index returns an unavailable report,
which never means "no duplicates". The groups are candidates: read both members
before calling one a duplicate.

Captains search code through `armada_mission_code_search`, which resolves the
vessel from the calling mission and searches no other vessel. Its budget and
result cap are `codeIndex.captainSearchMaxCallsPerMission` (default 40) and
`codeIndex.captainSearchMaxResults` (default 10). Enabling indexing for a vessel
with semantic search on sends that vessel's source chunks to the embedding
provider; exclude trees that must not leave the server with
`codeIndex.excludedPathFragments` before the first index update.

A stale index is not always relevant. Before applying the policy, the guard asks
whether the diff between the indexed commit and the current commit touches any
indexable source; a stale index whose only changes are docs, excluded paths, or
non-source is byte-identical in indexable content to a fresh one, so dispatch
proceeds with no refresh under any policy, `Block` included. This
staleness-relevance rule is deterministic and authoritative.

When a feature is off, use the explicit fallback. For example, search the
checkout directly when code indexing is off. Do not call disabled tools in a
loop.
