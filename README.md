<p align="center">
  <img src="assets/logo.png" alt="Armada Logo" width="200" />
</p>

<h1 align="center">Armada</h1>

<p align="center">
  <strong>Multi-agent orchestration for scaling human developers with AI coding captains.</strong>
  <br />
  <em>v0.8.0 alpha - APIs and schemas may change</em>
</p>

<p align="center">
  <a href="#why-armada">Why Armada</a> |
  <a href="#fork-features-vs-upstream">Fork Features</a> |
  <a href="#features">Features</a> |
  <a href="#quick-start">Quick Start</a> |
  <a href="#mcp-integration">MCP</a> |
  <a href="#architecture">Architecture</a>
</p>

---

## Why Armada

Armada is an Admiral process that coordinates AI coding agents, called captains, across registered git repositories, called vessels. It gives humans a control plane for dispatching, monitoring, reviewing, and landing agent work without losing context between repositories or terminal sessions.

Use Armada when one prompt in one shell is not enough:

- You want several captains working in parallel without sharing a worktree.
- You need missions to flow through implementation, tests, review, and landing gates.
- You want every mission, voyage, log, diff, check, incident, and release to become durable project memory.
- You need MCP and REST access so humans, dashboards, and orchestrator agents can all operate the same system.

Armada is intentionally vocabulary-heavy because the model mirrors the operating workflow:

| Concept | Meaning |
|---|---|
| Admiral | The server that schedules work, owns persistence, exposes REST/MCP/WebSocket surfaces, and manages landing. |
| Fleet | A collection of related repositories. |
| Vessel | One git repository registered with Armada. |
| Captain | One configured AI worker runtime, such as Claude Code, Codex, Cursor, Gemini, or OpenCode. |
| Mission | One atomic unit of work assigned to a captain. |
| Voyage | A batch of related missions dispatched together. |
| Dock | An isolated git worktree where a captain performs the mission. |

### Who It's For

- Developers who work across multiple repositories and want less context rebuilding.
- Teams that want auditable AI-assisted delivery instead of one-off terminal sessions.
- Operators who need durable checks, releases, deployments, incidents, and runbooks around agent-produced work.
- Orchestrator agents that need a structured MCP surface for creating, monitoring, reviewing, and landing work.

---

## Fork features vs upstream

Last upstream sync: `e9e3021f` (21 upstream commits absorbed) on 2026-05-24.

This fork (`developervariety/Armada`) is based on `jchristn/Armada`. It keeps upstream v0.8.0 delivery-management surfaces while adding deeper orchestration, recovery, code-index, reflection-memory, and multi-runtime captain workflows.

### Fork-only features

- Durable multi-runtime orchestration: Claude Code, Codex, Cursor, Gemini, and OpenCode (Kimi K2.7) captains can be scheduled through the same mission, voyage, dock, and merge-queue model. (`cec36fa6`, `db9439c`, `34574c5f`)
- Pipeline and persona expansion: built-in Worker, Architect, TestEngineer, Judge, Product Manager, Usability Engineer, MemoryConsolidator, and specialist reviewer personas support WorkerOnly, Reviewed, Tested, FullPipeline, ProductDevelopment, specialist-tested, and reflection pipelines. (`60781f72`, `e5fe494d`, `5e2a993f`)
- Model-tier routing: `low`, `mid`, and `high` routing picks eligible captains by tier, reserves high-tier capacity for specialist personas, and supports per-stage routing. (`5ae0ce4b`, `fc48ea41`, `b5088b0d`)
- Code-index retrieval: context packs, fleet search, hybrid lexical/semantic search, symbol graph sidecars, caller/callee queries, impact search, affected-test suggestions, and background refresh after landing. (`d242cc7b`, `c2ec68f1`, `86724a1d`, `9716f670`)
- Landing automation: merge queue, pull-request fallback, local merge, no-auto-land mode, protected paths, branch cleanup, recovery routing, target-drift retry, and PR reconciliation. (`0b6ace6`, `9494b62`, `3276aa5b`, `02e52f6`)
- Reflection memory: accepted mission evidence can update vessel, persona, captain, pack, and fleet learned playbooks through reviewable memory-consolidation missions. (`30855c67`, `7266a77a`, `eec40778`)
- Structured operations: objectives/backlog, planning and refinement sessions, workflow profiles, check runs, releases, deployments, incidents, runbooks, and historical timeline surfaces are wired into the fork's orchestration flow. (`efcc8221`, `adfdc3b3`, `6335f4eb`)
- AgentWake and remote orchestration: wake signals can resume a local orchestrator session or emit MCP notification signals when missions need attention. (`35054fcd`, `48125adc`)
- Memory and lifecycle hardening: capped logs, lightweight mission summaries, captain diagnostics, launch cleanup, dock cleanup, and status projections keep large deployments operable. (`0fe4411c`, `22869d01`, `7cf460e9`)

### Upstream features in-tree but not actively wired

- Mux runtime is present in-tree, but the active default captain pool for this fork is Claude Code, Codex, Cursor, Gemini, and OpenCode. Configure Mux captains explicitly before relying on them.
- Some upstream planning and workspace UX remains available as operator-facing dashboard functionality, while the fork's main automation path runs through durable MCP dispatch, objective linkage, pipelines, and merge-queue landing.

---

## Features

### Multi-Agent Work Management

Armada models work explicitly so a human or orchestrator can inspect every layer:

- Fleets group related vessels and can carry default pipeline settings.
- Vessels store repository URLs, local/bare paths, default branches, landing modes, protected paths, sibling repositories, default playbooks, and code-index settings.
- Captains represent runnable AI workers with runtime, model, persona eligibility, state, health, and current assignment.
- Missions store the actual unit of work, status, persona, preferred model, dependencies, playbook snapshots, logs, diffs, landing state, and output.
- Voyages group missions and preserve shared title, description, vessel, objective, planning-session, playbook, pipeline, and landing context.
- Docks are per-mission git worktrees so captains work on isolated branches instead of sharing the user's checkout.

### Pipelines and Personas

Built-in pipelines let work move through the right level of review:

- `WorkerOnly`: one implementation mission.
- `Reviewed`: Worker followed by Judge.
- `Tested`: Worker, TestEngineer, then Judge.
- `ReconciliationTested`: Worker, TestEngineer, then Judge for evidence-based reconciliation of implementation and test drift.
- `FullPipeline`: Architect, Worker, TestEngineer, then Judge.
- `ProductDevelopment`: Product Manager, Architect, Worker, Usability Engineer, TestEngineer, then Judge.
- Specialist tested pipelines: DiagnosticProtocol, TenantSecurity, MigrationData, PerformanceMemory, ReferencePorting, and FrontendWorkflow review before tests and Judge.
- Reflection pipelines: MemoryConsolidator alone or MemoryConsolidator with parallel Judges.

Personas are stored records, not hardcoded prompt strings. Built-ins include Worker, Architect, Product Manager, Usability Engineer, Judge, TestEngineer, DiagnosticProtocolReviewer, TenantSecurityReviewer, MigrationDataReviewer, PerformanceMemoryReviewer, PortingReferenceAnalyst, FrontendWorkflowReviewer, and MemoryConsolidator. Custom personas and prompt templates can be added through REST or MCP and then referenced by custom pipeline stages.

### Model-Tier Routing

Dispatchers can use `preferredModel` as routing guidance:

- `low`, `mid`, and `high` select among available captains in a complexity tier.
- Literal model names remain available for direct pins.
- Pipeline stages can override mission-level routing with their own `PreferredModel`.
- Specialist personas such as Judge, Architect, TestEngineer, MemoryConsolidator, and specialist reviewers are reserved for high-tier captains by default.
- `ReservedHighTierSlots` keeps high-tier capacity available for downstream specialist work instead of consuming every strong captain on first-stage Worker missions.

### Code Index, Context Packs, and Graph Search

Armada owns a repository code index for dispatch-time retrieval:

- `armada_code_search` searches indexed chunks for a vessel.
- `armada_context_pack` builds dispatch-ready markdown and returns a prestaged `_briefing/context-pack.md`.
- `armada_fleet_code_search` and `armada_fleet_context_pack` retrieve across a fleet.
- Graph tools search symbols, callers, callees, impact, and affected tests from sidecar files.
- Hybrid search can combine lexical and semantic ranking when semantic search is enabled.
- Context packs can be attached automatically during MCP dispatch and architect decomposition.
- Merge landing can refresh the index in the background so later missions see newly landed code.

### Merge Queue and Automated Landing

Armada can leave work for manual inspection or land it through configured modes:

| Mode | Behavior |
|---|---|
| `MergeQueue` | Enqueue work, create a temporary integration worktree, run validation, push, reconcile, and clean up branches sequentially per vessel and target branch. |
| `LocalMerge` | Merge the mission branch directly into the configured local working directory. |
| `PullRequest` | Push the branch and open a PR/MR; the mission remains `PullRequestOpen` until the PR is merged. |
| `None` | Stop at `WorkProduced`; the branch remains available for manual integration. |

Landing features include auto-land predicates, protected path checks, convention and critical-trigger gates, PR fallback, target-branch-drift retry, durable landing jobs, restart recovery, branch cleanup policies, pull-request reconciliation, and merge-queue purge/cancel tools.

### Structured Delivery Operations

Armada is not only a captain launcher. It also keeps delivery records connected to the work:

- Objectives and backlog items track scope, priority, effort, acceptance criteria, non-goals, rollout constraints, owners, tags, and evidence links.
- Planning and backlog-refinement sessions preserve captain-backed scoping conversations before dispatch.
- Workflow profiles define build, test, package, deploy, rollback, smoke-test, and health-check commands.
- Check runs persist structured validation output and can import external CI results.
- Releases collect linked voyages, missions, checks, notes, versions, tags, and artifacts.
- Deployments support approval, execution, verification, and rollback records.
- Incidents track operational issues, hotfix handoff, evidence, mitigation, and closure.
- Runbooks provide guided operational procedures with execution history.
- The historical timeline correlates objectives, planning, dispatch, checks, releases, deployments, incidents, events, merge activity, request history, and runbook execution.

### Captain Health and Quarantine

The Admiral tracks captain state and health so a busy fleet remains debuggable:

- Captains move through idle, assigned, in-progress, planning, stopped, and failure states.
- Health checks reclaim stale captains and docks after restarts.
- Diagnostics report active mission timing, dock git status, uncommitted files, launch/log hints, and code-index freshness.
- Quarantine and lifecycle controls prevent unhealthy captains from repeatedly taking work until an operator intervenes.
- Stop, recall, stop-all, and emergency controls are exposed through MCP, REST, dashboard, and WebSocket flows.

### Playbooks and Persistent Memory

Playbooks are reusable markdown guidance that can be delivered inline, referenced, or attached into the worktree. Fleet, vessel, persona, captain, voyage, and per-mission selections merge into mission playbook snapshots so every captain receives the guidance that applied at dispatch time.

Reflection memory turns accepted mission evidence into reviewable learned notes for future missions. Vessel learned facts, persona notes, captain behavior anchors, pack hints, and fleet hints can be consolidated and reviewed instead of rediscovered by each new captain.

### Interfaces

Armada exposes the same operating model through multiple surfaces:

- REST API for dashboards, scripts, and external services.
- MCP HTTP endpoint for orchestrator agents.
- MCP stdio command for clients that prefer local framed or stdio transport.
- WebSocket events for live dashboard updates.
- Helm CLI for setup, config, server start, and MCP installation.
- React/Vite dashboard for operators.

### Persistence

Armada persists state through database drivers for SQLite, PostgreSQL, MySQL, and SQL Server. Missions, voyages, captains, docks, events, playbooks, pipelines, objectives, checks, releases, deployments, incidents, runbooks, request history, and merge-queue records are stored outside agent sessions so the system can recover, audit, and resume.

---

## Quick Start

### Prerequisites

- .NET 10.0 SDK.
- Git.
- At least one supported agent CLI if you want local captains: Claude Code, Codex, Cursor, Gemini, or OpenCode.
- Optional for pull requests: `gh` for GitHub or `glab` for GitLab.

### Build

```bash
dotnet build src/Armada.sln
```

### Start the Admiral

```bash
dotnet run --project src/Armada.Server --framework net10.0
```

Default local endpoints:

- REST and dashboard: `http://localhost:7890`
- MCP HTTP JSON-RPC: `http://localhost:7891/rpc`

### Configure MCP Clients

The repository includes an MCP config that points at the default HTTP endpoint:

```json
{
  "mcpServers": {
    "armada": {
      "type": "http",
      "url": "http://localhost:7891/rpc"
    }
  }
}
```

Helm can install managed MCP entries for supported clients:

```bash
dotnet run --project src/Armada.Helm --framework net10.0 -- mcp install
```

### Dispatch a Voyage Through MCP

`armada_dispatch` requires a top-level `vesselId`. Put `preferredModel` on each mission that needs routing guidance.

```json
{
  "title": "Improve status health output",
  "description": "Make the health endpoint easier for operators to inspect.",
  "vesselId": "vsl_example123",
  "pipeline": "Reviewed",
  "codeContextMode": "auto",
  "missions": [
    {
      "alias": "worker",
      "title": "Add concise health details",
      "description": "Update the status health response and dashboard copy. Keep the change focused and run the relevant build or smoke check.",
      "preferredModel": "mid"
    },
    {
      "title": "Review health details",
      "description": "Review the worker diff for correctness, regressions, and missing validation.",
      "dependsOnMissionAlias": "worker",
      "alias": "review",
      "preferredModel": "high"
    }
  ]
}
```

For dependency aliases, assign `alias` to the upstream mission and reference it from `dependsOnMissionAlias` on the downstream mission:

```json
{
  "title": "Two-stage implementation",
  "vesselId": "vsl_example123",
  "missions": [
    {
      "alias": "worker",
      "title": "Implement the change",
      "description": "Make the code change and commit it.",
      "preferredModel": "mid"
    },
    {
      "alias": "judge",
      "title": "Judge the change",
      "description": "Review the implementation and emit a verdict.",
      "dependsOnMissionAlias": "worker",
      "preferredModel": "high"
    }
  ]
}
```

---

## MCP Integration

The primary MCP transport is HTTP JSON-RPC at:

```text
http://localhost:7891/rpc
```

Common MCP tool groups:

- Fleet, vessel, captain, mission, voyage, dock, signal, event, persona, prompt-template, and pipeline enumeration.
- Dispatch, architect decomposition, mission status, voyage status, logs, diffs, and status transitions.
- Merge queue enqueue, process, retry, cancel, purge, and PR reconciliation.
- Code index status, update, search, context pack, fleet context pack, graph symbols, callers, callees, impact, and affected tests.
- Objective/backlog CRUD, refinement, planning, and dispatch linkage.
- Check run, release, deployment, incident, and runbook operations.
- Playbook management and mission playbook snapshots.
- Captain diagnostics, AgentWake registration, and wake notifications.

---

## REST and Dashboard

The Admiral server exposes REST routes under `/api/v1/*`, serves the dashboard from the same HTTP server, and broadcasts live state through WebSocket. REST and MCP share the same database-backed services, so operators can mix dashboard workflows, scripts, and orchestrator-agent calls without splitting state.

Useful REST areas include:

- `/api/v1/status`
- `/api/v1/fleets`
- `/api/v1/vessels`
- `/api/v1/captains`
- `/api/v1/missions`
- `/api/v1/voyages`
- `/api/v1/merge-queue`
- `/api/v1/objectives`
- `/api/v1/check-runs`
- `/api/v1/releases`
- `/api/v1/deployments`
- `/api/v1/incidents`
- `/api/v1/runbooks`

---

## Architecture

```text
src/
  Armada.Core       Domain models, settings, database drivers, services, and interfaces
  Armada.Runtimes   Runtime adapters for Claude Code, Codex, Cursor, Gemini, OpenCode, and extensible agents
  Armada.Server     Admiral REST/MCP/WebSocket server and dashboard host
  Armada.Helm       CLI for config, server start, and MCP setup
  Armada.Dashboard  React/Vite operator dashboard
```

The server constructs most services directly in `ArmadaServer.cs`. Database drivers cover SQLite, PostgreSQL, MySQL, and SQL Server. Runtime adapters implement the shared captain process contract while preserving each CLI's launch and environment requirements.

---

## Build and Test

Build the solution:

```bash
dotnet build src/Armada.sln
```

Run test projects on .NET 10:

```bash
dotnet run --project test/Armada.Test.Automated --framework net10.0
dotnet run --project test/Armada.Test.Unit --framework net10.0
dotnet run --project test/Armada.Test.Runtimes --framework net10.0
```

Dashboard asset changes require:

```bash
npm.cmd run build
```

from `src/Armada.Dashboard`.

---

## License

Armada is licensed under the terms in [LICENSE.md](LICENSE.md).
