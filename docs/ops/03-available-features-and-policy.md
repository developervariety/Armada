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

Captains search code through `armada_mission_code_search`, which resolves the
vessel from the calling mission and searches no other vessel. Its budget and
result cap are `codeIndex.captainSearchMaxCallsPerMission` (default 40) and
`codeIndex.captainSearchMaxResults` (default 10). Enabling indexing for a vessel
with semantic search on sends that vessel's source chunks to the embedding
provider; exclude trees that must not leave the server with
`codeIndex.excludedPathFragments` before the first index update.

When a feature is off, use the explicit fallback. For example, search the
checkout directly when code indexing is off. Do not call disabled tools in a
loop.
