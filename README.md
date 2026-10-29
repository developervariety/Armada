<p align="center">
  <img src="assets/logo.png" alt="Armada Logo" width="160" />
</p>

<h1 align="center">Armada</h1>

<p align="center">
  <strong>Run a fleet of AI coding agents across all of your repositories: in parallel, isolated, reviewed, and on the record.</strong>
  <br />
  <em>Fork of <a href="https://github.com/jchristn/Armada">jchristn/Armada</a> | v1.0.0</em>
</p>

<p align="center">
  <a href="#upstream-vs-fork">Upstream vs Fork</a> |
  <a href="#what-you-get">What You Get</a> |
  <a href="#quick-start">Quick Start</a> |
  <a href="#configuration">Configuration</a> |
  <a href="#clients-and-apis">Clients and APIs</a> |
  <a href="#documentation">Documentation</a> |
  <a href="#license-and-attribution">License</a>
</p>

---

Armada is a self-hosted coordinator for the coding agents you already use: Claude Code, Codex, Gemini CLI, Cursor, Mux, OpenCode, and any model behind an API endpoint. You describe the work. Armada hands it to agents (captains), gives each one its own git worktree (dock) and branch, runs it through the stages you choose (plan, implement, test, review), lands the result the way you choose (merge, pull request, or merge queue), and records every log, diff, and decision in a database you can query later.

This repository is a fork of [jchristn/Armada](https://github.com/jchristn/Armada). It shares the core model (fleets, vessels, captains, missions, voyages, docks, pipelines) and takes upstream changes selectively. Its version numbers are independent of upstream. The fork focuses on:

- Autonomous operation: an objective scheduler, recovery and rescue missions, incidents, and an audit queue.
- Verified landing: a merge queue that proves delivery against the target branch and refuses unproven completion.
- Routing: capability tiers and optional usage-aware captain selection.
- Typed decisions: calibrated closed-question answers used as tie-breakers behind deterministic rules.
- Retrieval: a code index, context packs and graph search for mission briefs.
- Delivery records: checks, releases, deployments, incidents, and runbooks.

## Upstream vs Fork

The fork is a divergent superset of upstream, so it syncs incrementally by cherry-picking reviewed changes, never by merging the whole history. The procedure is in [CLAUDE.md](CLAUDE.md). Upstream features that the fork keeps in tree but does not enable by default stay opt-in.

### Differences from upstream

| Area | What the fork adds or changes | Detail |
|---|---|---|
| Scheduling | Objective backlog with an autonomous scheduler, dispatch preview, start-from-ref dispatch, and a persistent dispatch hold. | [Backlog](docs/BACKLOG.md), [Scheduling](docs/SCHEDULING.md) |
| Pipelines and personas | Reviewed pipelines with Judge acceptance evidence, Linter and Recorder stages, structured Architect plans (v2 and v3) with an integration barrier. | [Pipelines](docs/PIPELINES.md), [Personas](docs/PERSONAS.md) |
| Routing | Capability-tier routing, optional Smart Routing, and captain quarantine on repeated failure. | [Smart Routing](docs/USAGE_ROUTING.md) |
| Typed decisions | Calibrated typed-decision gate, off until a provider key is present, with a kill switch and observation review. | [Typed decisions](docs/TYPED_DECISIONS.md) |
| Landing | Merge queue with exact-commit verification, delivery proof, `LandingFailed` handling, and bounded rescue missions. | [Merging](docs/MERGING.md) |
| Delivery operations | Checks, releases, deployments with rollback, incidents, runbooks, workflow profiles, and environments. | [Delivery operations](docs/DELIVERY_OPERATIONS.md), [Operational assets](docs/OPERATIONAL_ASSETS.md) |
| Retrieval | Code index, context packs, graph search, and unified recall through `armada_search_context`. | [Context index](docs/CONTEXT_INDEX.md) |
| API endpoints | In-process tool loop for endpoint captains with deterministic compaction. | [REST API](docs/REST_API.md) |
| Security | Default-credential refusal on non-loopback starts, login lockout, route-authorization test, tenant-scoped reads. | [Upgrading](docs/UPGRADING.md) |
| Storage | Pre-migration backup with verified restore on all four database providers, storage inventory, history archive. | [Storage inventory](docs/STORAGE_INVENTORY.md), [Archive](docs/ARCHIVE.md) |
| Evaluation | Fixture battery and evaluation voyages kept out of production summaries. | [Battery](docs/BATTERY.md) |

The [changelog](CHANGELOG.md) holds the complete record.

### Upstream changes imported

The fork takes these upstream capabilities, some adapted:

- Salted password storage with legacy upgrade, and the seeded-credential refusal (adapted).
- Database protection: pre-migration backup and restore verification, per-file SQLite write queues.
- Runtime and MCP evidence: typed provider failures, deferred caller-filtered tool catalogs, matching HTTP and stdio tool names.
- Harbor execution: capacity waits, opt-in host-owned checkouts, fenced restart adoption, a jobs view and host chart.
- Operator workflows: saved Ask conversations with approval proposals (adapted), saved Fleet Actions limited to Mission actions, checkout import, Needs You inbox items.
- Storage inventory (adapted) and vessel health reports that never block dispatch.
- Paged `enumerate` routes, typed HTTP and WebSocket error codes, and request limits.
- Attempt provenance, evaluation flags, model probes, and a reduced fixture battery.
- Non-root container image stage (opt-in) and staged Dashboard installation with rollback.

### Upstream changes not imported

| Upstream capability | Reason |
|---|---|
| TUI and mobile app with server push | Deferred; the Dashboard and Helm remain the supported clients. |
| CLI permission requests for missions and other runtimes | The fork keeps mission launch policy; only saved Claude Code Ask turns route tool calls for approval. |
| Signed installers, package publishing, A/B-slot rebuilds | Out of scope; Docker and local scripts stay the release paths. |
| Hosted CI workflow publication | Held; local verification and the Linux gate remain. |
| Arbitrary shell Fleet Actions | Excluded; saved actions dispatch missions through existing checks and landing rules. |
| Upstream routing, blocked-stage, and recovery replacements | The fork keeps Smart Routing, exact-commit verification, incidents, and bounded rescue. |
| Stage-outcome store, failure classifier, escalation records, handoff artifact store | The fork's attempt facts, Judge, incidents, and rescue rules stay the record. |
| Hard Judge exclusion of chain authors | The fork's Judge preference is soft. |
| Native cloud model-endpoint providers (xAI, Ollama Cloud, Azure OpenAI, Vertex AI, Bedrock) | OpenAI-compatible endpoints cover xAI, Ollama Cloud, and Azure. |
| Endpoint custom headers, sampling and concurrency options | Not adopted. |
| pi coding-agent runtime | Not adopted. |
| WIP commit and push when an operator stops a mission | A stop keeps a local `recover/` ref and pushes nothing. |
| Automatic retention, MCP delete tool, deletion outside Ask conversations | The fork keeps its archive and retention rules. |
| Removal of whole-list routes, bounded responses with changed shapes | Successful responses keep their shape; whole-list routes stay beside paged routes. |

On hold or deferred: automatic workflow profiles, bulk checkout history, token heatmap, proxy build, SDK and Harbor desktop, extra fixture battery languages and rubric graders, voyage-size limits, and the per-reply copy button and thread-list refresh in Ask.

## What You Get

- **Parallel, isolated work.** Each mission runs in its own dock on its own branch, so agents never collide with each other or with your checkout.
- **Pipelines.** Built-in and custom pipelines chain personas (Architect, Worker, TestEngineer, Linter, Judge, Recorder) with optional review gates. See [Pipelines](docs/PIPELINES.md).
- **Landing modes.** Local merge, merge and push, pull request, merge queue, or none. See [Merging](docs/MERGING.md).
- **Recovery.** Stall detection, dock repair, relaunch, and bounded rescue missions.
- **Memory.** Vessel context, playbooks, and saved memories reach later missions automatically.
- **Ask conversations.** Saved threads that answer from live fleet state and propose changes for exact human approval. See [Ask conversations](docs/ASK_CONVERSATIONS.md).
- **Harbor.** Run agents on your own machine while the Admiral runs elsewhere. See [Harbor runner](docs/HARBOR_RUNNER.md) and [Harbor checkouts](docs/HARBOR_CHECKOUTS.md).
- **Four database providers.** SQLite, PostgreSQL, MySQL, and SQL Server.

## Quick Start

### Prerequisites

- .NET 10.0 SDK and Git.
- At least one supported agent CLI for local captains.
- Optional: `gh` for GitHub or `glab` for GitLab pull requests.

### Build and run

```bash
dotnet build src/Armada.sln
dotnet run --project src/Armada.Server --framework net10.0
```

The REST API and Dashboard listen on `http://localhost:7890`. MCP listens on `http://localhost:7891/mcp`.

### Connect an MCP client

Every MCP request must carry a credential. Helm writes client entries that read the key from `ARMADA_API_KEY`:

```bash
export ARMADA_API_KEY=<your key>
dotnet run --project src/Armada.Helm --framework net10.0 -- mcp install
```

### Your first dispatch

Ask the connected client to call `armada_dispatch` with a `vesselId`, a title, and one or more missions. Watch progress in the Dashboard, or with `armada watch` from Helm. See [Orchestrator instructions](docs/ORCHESTRATOR_INSTRUCTIONS.md) and the per-client guides ([Claude Code](docs/CLAUDE_CODE_AS_ORCHESTRATOR.md), [Codex](docs/CODEX_AS_ORCHESTRATOR.md), [Cursor](docs/CURSOR_AS_ORCHESTRATOR.md), [Gemini](docs/GEMINI_AS_ORCHESTRATOR.md)).

A new database seeds a default administrator. The Admiral refuses to start on a non-loopback address while the seeded password is active; set `ARMADA_INITIAL_ADMIN_PASSWORD` on first start. See [Upgrading](docs/UPGRADING.md#seeded-default-credentials).

### Deploy

- **Docker:** [docs/DOCKER.md](docs/DOCKER.md)
- **System startup (systemd, launchd, scheduled task):** [docs/RUN_ON_STARTUP.md](docs/RUN_ON_STARTUP.md)

Run the Admiral at login with the helper for your platform. Each installer publishes the server and registers it; each updater republishes and restarts it; each health check probes the running server.

| Platform | Install | Update | Health check |
|---|---|---|---|
| Linux (systemd user unit) | `scripts/linux/install-systemd-user.sh` | `scripts/linux/update-systemd-user.sh` | `scripts/linux/healthcheck-server.sh` |
| macOS (launchd agent) | `scripts/macos/install-launchd-agent.sh` | `scripts/macos/update-launchd-agent.sh` | `scripts/macos/healthcheck-server.sh` |
| Windows (current-user Run entry) | `scripts/windows/install-windows-task.bat` | `scripts/windows/update-windows-task.bat` | `scripts/windows/healthcheck-server.bat` |

- **Remote access and proxy:** [docs/REMOTE_MGMT.md](docs/REMOTE_MGMT.md)
- **Upgrading an existing server:** [docs/UPGRADING.md](docs/UPGRADING.md)

## Configuration

Settings load from the Admiral's settings file and environment variables. Operator procedures and the settings reference are in [docs/armada-ops.md](docs/armada-ops.md); request limits are in [docs/REQUEST_LIMITS.md](docs/REQUEST_LIMITS.md), and routing settings are in [Smart Routing](docs/USAGE_ROUTING.md).

## Clients and APIs

| Surface | Reference |
|---|---|
| REST (`/api/v1/*`) | [docs/REST_API.md](docs/REST_API.md) |
| MCP | [docs/MCP_API.md](docs/MCP_API.md), [tool catalog](docs/MCP_TOOL_CATALOG.md) |
| WebSocket | [docs/WEBSOCKET_API.md](docs/WEBSOCKET_API.md) |
| Dashboard, Helm, MCP entry points | [docs/CLIENT_SURFACES.md](docs/CLIENT_SURFACES.md) |

```text
src/
  Armada.Core       Domain models, settings, database drivers, services, code index
  Armada.Runtimes   Runtime adapters for the supported agent CLIs and the API endpoint loop
  Armada.Server     Admiral REST, MCP, and WebSocket server and orchestrators
  Armada.Helm       CLI for configuration, server start, and MCP setup
  Armada.Dashboard  React and Vite operator dashboard
```

## Documentation

- Operator guide: [docs/armada-ops.md](docs/armada-ops.md)
- Pipelines and personas: [PIPELINES](docs/PIPELINES.md), [PERSONAS](docs/PERSONAS.md), [PERSONAS_GUIDE](docs/PERSONAS_GUIDE.md)
- Scheduling and routing: [SCHEDULING](docs/SCHEDULING.md), [USAGE_ROUTING](docs/USAGE_ROUTING.md), [TYPED_DECISIONS](docs/TYPED_DECISIONS.md)
- Landing and delivery: [MERGING](docs/MERGING.md), [DELIVERY_OPERATIONS](docs/DELIVERY_OPERATIONS.md), [OPERATIONAL_ASSETS](docs/OPERATIONAL_ASSETS.md)
- Retrieval: [CONTEXT_INDEX](docs/CONTEXT_INDEX.md)
- Fleet work: [FLEET_ACTIONS](docs/FLEET_ACTIONS.md), [VESSEL_CHECKOUT_IMPORT](docs/VESSEL_CHECKOUT_IMPORT.md), [BACKLOG](docs/BACKLOG.md)
- Testing: [TESTING](docs/TESTING.md). Run the suites with `scripts/macos/run-tests.sh` or `scripts/linux/run-tests.sh`.
- History: [CHANGELOG](CHANGELOG.md)

## License and Attribution

Armada was created by [jchristn](https://github.com/jchristn) as [`jchristn/Armada`](https://github.com/jchristn/Armada). This fork builds on that work, and all upstream copyright and attribution are retained.

Armada is licensed under the terms in [LICENSE.md](LICENSE.md).
