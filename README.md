<p align="center">
  <img src="assets/logo.png" alt="Armada Logo" width="200" />
</p>

<h1 align="center">Armada</h1>

<p align="center">
  <strong>Multi-agent orchestration for scaling human developers with AI coding captains.</strong>
  <br />
  <em>Private fork of <a href="https://github.com/jchristn/Armada">jchristn/Armada</a> — v0.9.0 alpha, APIs and schemas may change</em>
</p>

<p align="center">
  <a href="#why-armada">Why Armada</a> |
  <a href="#upstream-vs-fork">Upstream vs Fork</a> |
  <a href="#features">Features</a> |
  <a href="#quick-start">Quick Start</a> |
  <a href="#mcp-integration">MCP</a> |
  <a href="#architecture">Architecture</a> |
  <a href="#license-and-attribution">License</a>
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

## Upstream vs Fork

This repository is a private fork of [jchristn/Armada](https://github.com/jchristn/Armada).
It shares the core operating model -- fleets, vessels, captains, missions, voyages,
docks, pipelines, and delivery records -- but it is a divergent **superset**, not a
version behind upstream. The fork keeps parity by re-implementing selected upstream
features rather than merging, because a blanket merge would collide with, and in
places regress, deliberate fork choices. The capability-by-capability map is the list below; the incremental cherry-pick
sync procedure and its baseline live in [`CLAUDE.md`](CLAUDE.md).

What the fork adds on top of the shared model:

- **Typed decisions.** A calibrated advisory classifier (TypeSafe Jev) behind the
  existing deterministic rules. Every configured built-in decision defaults to
  Gate; without a provider key the effective mode is Off. It only ever makes a call more conservative, never lands
  or dispatches, fails closed to the rule, and never egresses unredacted state.
  Captains get read-only tools, including prior-art retrieval
  that answers "does this already exist?" with evidence before work starts.
- **Deeper review.** Linter and Recorder pipeline stages, immutable reviewed-commit
  Checks, declared-consumer builds, verified landing evidence, and full recovery
  pipelines with provider-aware rescue.
- **Smart Routing.** Legacy Routing (captain capability tiers and preference
  ranks, persona locks and minimum tiers, non-native-first) plus an opt-in usage
  filter that removes captains on exhausted accounts and demotes low ones,
  per-persona default, lighter, and stronger model lists chosen by the
  `capacity_escalation` typed decision, and optional persona route restrictions.
  Smart Routing only reorders the captains Legacy Routing admits.
- **Operations.** An in-place Restart Server action adapted for Docker (a graceful
  stop under the container restart policy); a Voyage AI code-index embedding client;
  supervised self-deploy, hardened but disabled pending its safety integration; and
  server-side Harbor remote runners, disabled by default.
- **Repository context.** A code index, symbol graph, and context packs feed dispatch;
  a context index supplies scoped retrieval, with optional brief slimming disabled
  by default. See [Context Index](docs/CONTEXT_INDEX.md).

Some upstream additions are unported by decision, not by omission: the cloud
model-endpoint providers (Azure OpenAI, Vertex AI, Bedrock) and upstream's native
installer and A/B-slot rebuild paths, because the fork ships Docker with supervised
self-deploy.

---

## Features

### Multi-Agent Work Management

Armada models work explicitly so a human or orchestrator can inspect every layer:

- Fleets group related vessels and can carry default pipeline settings.
- Vessels store repository URLs, local/bare paths, default branches, landing modes, protected paths, sibling repositories, default playbooks, and code-index settings. Tenant administrators can push landing-repository branches to `origin` and merge them locally through guarded, confirmed controls that never force, delete, or bypass mission landing gates.
- Captains represent runnable AI workers with runtime, model, persona eligibility, state, health, and current assignment.
- Missions store the actual unit of work, status, persona, preferred model, dependencies, playbook snapshots, logs, diffs, landing state, and output.
- Voyages group missions and preserve shared title, description, vessel, objective, planning-session, playbook, pipeline, and landing context.
- Docks are per-mission git worktrees so captains work on isolated branches instead of sharing the user's checkout.

### Autonomous Scheduling And Operator Tools

The objective scheduler selects eligible objectives and dispatches captains.
Its settings persist across Admiral restarts, and its voyages use the normal
Build and UnitTest Check-arming path. On .NET vessels that path also arms a Slop
Check, which classifies the reviewed diff natively: skipped tests, project-wide
`NoWarn` and central package version bypasses fail it, and empty catch blocks,
literal delays and warning suppressions are reported without failing it. Once a
voyage has a commit under review,
an armed Check that has not run yet is queued work: the Judge gate stamps it at
the reviewed commit and holds the PASS until it runs, rather than rejecting the
PASS for missing Checks. Operators handle landing, incidents,
campaign planning, and helper requests.

`scripts/autonomy/spawn-helper.sh` provides capped, timed helpers for narrow
delegated work. `scripts/autonomy/watch-armada.mjs` subscribes to the WebSocket
hub and emits one line per voyage, mission, incident or directed note, so an
operator session watches by subscribing instead of blocking on a poll.

A sweep that dispatches nothing says why. `LastSkipReason` is null only when work
was dispatched, and otherwise names the constraint with counts. Note that an
objective must name exactly one vessel to auto-dispatch: set `VesselIds` to the
vessel whose repository receives the commit. An objective can also carry a
`StartFromRef`: the first stage's branch is cut from that ref instead of the
default branch, and a ref that does not resolve refuses the dispatch by name
(`start_from_ref_missing`) rather than falling back. Operator dispatch inherits
that objective ref unless `missions[].startFromRef` overrides it. The dispatch
result echoes resolved commits in `MissionStartRefs`, and
`mission.start_ref_resolved` records the root commit.

Use `preview_objective_dispatch` or
`GET /api/v1/objectives/{id}/dispatch-preview` before dispatch. The read-only
preview checks the target, pipeline roles, configured captains, required Checks,
repository inputs, brief, and the complete typed dependency graph. Diagnostic
paths are bounded and report when they are truncated. Role coverage asks the
rules assignment asks: the vessel's tenant, quarantine (including a future
quarantine deadline), Smart Routing persona routes, the requested captain from a
captain override or the persona's default captain with its fallback tier floor,
and the tier floor a pinned model no captain runs sets. A compatible captain
does not have to be idle for the objective to be ready; idle state is capacity.
Operator and autonomous objective dispatch use this same preflight.
The REST preview accepts a JSON `captainAssignments` query value when an
operator must test the same captain and fallback-tier overrides as dispatch.

Every work-creation path also passes through one durable fleet-capacity gate.
The gate counts active work-bearing voyages plus standalone active missions,
uses the transitive sibling-lane map, and holds a renewable tenant-scoped lease
through initial graph creation. It reads only the vessel footprint of active
work in one database query, so its cost does not grow with retained history.
Capacity refusals use
`fleet_capacity_reached` or `sibling_lane_capacity_reached`; failed creation is
cancelled before the lease is released. Mission metadata updates cannot move a
mission between vessels or voyages, and restarts are admitted like new work.
Captain launch has its own global limit, `MaxConcurrentCaptainWorkloads`. One
global workload reservation is held from the active-workload count until the
mission is durably Assigned, so parallel assignments in different lanes cannot
both take the last slot. Voyage and sibling-lane limits keep their own leases.

### Pipelines and Personas

Built-in pipelines let work move through the right level of review:

- `WorkerOnly`: one implementation mission.
- `Reviewed`: Worker followed by Judge.
- `Tested`: Worker, TestEngineer, Linter, then Judge.
- `FullPipeline`: Architect, Worker, TestEngineer, then Judge.
- `ProductDevelopment`: Product Manager, Architect, Worker, Usability Engineer, TestEngineer, Linter, Judge, then Recorder.
- `Recorded`: Worker, then Recorder -- do the work, then record what is worth remembering.
- Specialist-tested pipelines add a domain reviewer before tests; the reference-porting pipeline also runs the Linter before the Judge.

The Judge must cite evidence for each acceptance criterion in the brief; duplicate
claims and missing criteria refuse PASS. Brief trimming preserves the criteria.
See [Judge acceptance evidence](docs/PIPELINES.md#judge-acceptance-evidence).

A Linter stage runs before the Judge in the pipelines that produce vessel code, so overengineering and style are caught before review. `FullPipeline` stays without a Linter as the minimal review shape.

Personas are stored records, not hardcoded prompt strings. Custom personas and prompt templates can be added through REST or MCP and then referenced by custom pipeline stages.

### Model-Tier Routing

Dispatchers can use `preferredModel` as routing guidance. Routing
selects in three layers; see [Smart Routing](docs/USAGE_ROUTING.md).

- Each captain carries a capability tier (`Economy`, `Standard`, `Premium`; classified from the model name unless pinned) and a preference rank, both edited on the captain. `low`, `mid`, and `high` name a tier floor (Economy, Standard, Premium): the lowest idle tier at or above the floor is tried first, higher rank first.
- A persona can set a minimum capability tier. The Test Engineer persona uses a Standard minimum so routine test work can use Standard captains.
- Literal model names remain available for direct pins.
- Pipeline stages can override mission-level routing with their own `PreferredModel`.
- Dispatch and objective preview use the same persona-aware result for each generated
  stage and mission. The persona minimum and mission request combine into one
  floor. Preview reports separate
  requirements when mission descriptions use different literal model pins.
- Non-native-first, reserved Premium slots, and the stage-persona title-prefix guard live in `ArmadaSettings` (`factory/settings.fleet.example.json` is the fleet overlay). The retired tier keys move onto captain tiers, captain ranks, and persona minimum tiers once at startup, with a settings backup.
- **Legacy Routing** is everything above, with `modelTier.usageRouting.enabled` false. It needs no Jev key and remains available when operators prefer its deterministic order. Optional [Smart Routing](docs/USAGE_ROUTING.md) (`enabled` true) keeps the Legacy Routing order and filters it: captains on Exhausted accounts are removed and captains on Low or Reserve accounts move after the rest. Per-persona `default`, `lighter`, and `stronger` model lists group the order, the `capacity_escalation` decision chooses the list tried first, and optional persona routes restrict a persona to named accounts. Without Jev, Smart Routing tries Default first and keeps the account usage filter. The Dashboard Settings hub’s Routing tab supports account usage, reserve thresholds, budget planning, and draft previews. Collectors support Codex, Claude, Cursor, OpenCode Go, and normalized local snapshots. An account can own a separate captain login for Claude Code, Codex, OpenCode, or Cursor; it is off unless configured, and a provider limit on one captain holds its whole account. A logged-out, expired, or held account blocks assignment with a named reason code in status and the usage preview.
- The same Routing tab edits those fields and saves only the fields that changed. `modelTier` and `voyageDispatch` hot-reload; `modelProviders` and additional personas/pipelines/templates load at startup.

### Typed decisions (Off until a key is present)

A calibrated classifier (TypeSafe Jev) the admiral can consult at a decision
point, behind the deterministic rules it never replaces. Without a provider key
the effective global mode is `Off` (reason `typed_decisions_no_key`), whatever
the stored mode. With a key, every decision ships in `Gate` and records the
rule's verdict and the model's on every call. The key comes from the environment
variable named by `apiKeyEnv`, or from the key file
`<data directory>/secrets/typesafe-api-key` that `PUT /api/v1/typed-decisions/key`
writes; adding or removing it takes effect without a restart. The decision
catalogue and its principles are documented in
[Typed decisions](docs/TYPED_DECISIONS.md).
When a decision is enabled it can only make a call
more conservative, never lands or dispatches, gates only at or above the
confidence threshold, fails closed to the deterministic rule, and never egresses
unredacted state. Captains can consult read-only tools
(`armada_typed_decision`, `armada_score_items`, `armada_check_premise`, `armada_check_prior_art`,
`armada_memory_triage`). Configure it under `typedDecisions` in `settings.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `mode` | `Gate` (effective `Off` without a key) | Global cap and kill switch: `Off`, `Shadow`, or `Gate`. `Off` stops every decision point and every captain tool. Hot-reloaded. |
| `baseUrl` | `https://api.typesafe.ai` | Provider base URL; the client POSTs to `{baseUrl}/v1/systemone`. |
| `model` | `jev-latest` | Model id sent with each request. |
| `apiKeyEnv` | `ARMADA_TYPESAFE_KEY` | Environment variable holding the Bearer key. When unset, the key file `<data directory>/secrets/typesafe-api-key` is read. The key is never stored in settings. |
| `timeoutSeconds` | `10` | Per-request timeout; a slower decision is unavailable, not late. |
| `maxStateChars` | `60000` | Character cap on redacted state per request, about 20,000 provider tokens. A batch also stays under 80,000 characters of state plus question text, inside the provider's 32,000-token request limit. |
| `egressExcludedMarkers` | empty | Text markers meaning a state carries content that must never leave the host (for example a directory of decrypted vendor data). Checked on the UNREDACTED state by every egress path - the adapter skeleton, the standalone adapters, custom decisions and captain tools. A decision's or custom definition's own `egressExcludedMarkers` replaces this list for it; an empty own list opts it out. A match sends nothing and records `egress_excluded_content`. |
| `egressExcludedVesselIds` | empty | Vessels whose content must never leave the host. Every decision about an objective, mission or vessel that names one of them keeps its rule, sends nothing, and records an unavailable event with reason `egress_excluded_vessel`; an objective names its `VesselIds`. |
| `decisions` | all `Gate` | Per-decision `mode` (`Off`/`Shadow`/`Gate`) and `gateThreshold`. Effective mode is the minimum of the global and per-decision mode. |
| `evalOnModelChange` | `true` | Run the synthetic evaluation set in the background when the provider reports a model version not yet evaluated. |
| `captainTool` | enabled | Captain-facing tool: `enabled`, `maxStateChars` (default `60000`). There is no per-mission call cap. |
| `retention` | off | Host-local retention of REDACTED decision state as training data: `enabled`, `retentionDays`, `minimumSamplesPerDecision`, plus `retainState` per decision. Nothing leaves the host and no event carries the state. |

Without a key no decision calls a client or records an event. `mode` and
`decisions` hot-reload in place, so decision points see the change and a later
settings write cannot clobber it. `GET /api/v1/typed-decisions` reports the
effective and stored modes, key presence and source, and every decision;
`PUT /api/v1/typed-decisions` changes modes and thresholds. Every enabled call emits a
`typed_decision.gated`, `typed_decision.shadow`, or `typed_decision.unavailable`
event carrying the decision, verdicts, confidences, tokens, latency, and the
state's hash and byte count — never the state itself. See
[Typed decisions](docs/TYPED_DECISIONS.md) for the full
contract.

### API-endpoint tools and compaction

The `ApiEndpoint` runtime runs its workspace tools and holds the conversation in
process. Harness captains keep their harness's own tools and compaction; Armada
ships no harness plugin. These are shape rules, not typed decisions.

- `run_command` starts bash as the leader of its own process group (through
  `setsid`, or `perl` where `setsid` is absent). A timeout or a cancellation kills
  the whole group, including a background child the shell left behind. A
  descendant that starts its own session and leaves the tree escapes the kill,
  and on Windows only the live tree is killed (see "Child processes"). Output is
  read in fixed-size chunks, so a line longer than the cap is cut while it is read.
- `edit_file` and `multi_edit` refuse empty `old_string`. Each `multi_edit` step
  must match exactly once in the content the earlier steps produced. An ambiguous
  match reports its count and at most 20 candidate lines.
- Large `run_command` output is pruned inbound before it enters the conversation.
  Past about 10,000 estimated tokens, progress-only chunks (download, compile,
  cache, test progress dots) may drop. Diagnostics, test totals, JSON/XML/YAML,
  diffs, source dumps and binary blobs stay. The full output is archived at
  `.armada-tool-output/` in the workspace and cited on the result. A failed
  archive leaves the original output.
- When the conversation crosses about 1 MiB, older tool results keep a
  300-character head plus a `[compacted]` marker. Diagnostic and test-total
  lines stay. The conversation never drops a message, so tool-call and
  tool-result pairing stays intact.
- Past a hard ceiling of 8 MiB the same pass truncates remaining large results,
  including diagnostics. A conversation that still exceeds the ceiling fails the
  run.

Anthropic API endpoints request a five-minute cache breakpoint on the fixed
launch message. This caches the tools, system prompt, and launch prefix when
the provider minimum is met. Later messages do not move that breakpoint. Other
transports log once that explicit breakpoints are unavailable; automatic
provider caching may still apply. Cache reads, cache writes, and reasoning
tokens are copied from provider usage into runtime usage events. A zero counter
is not proof that a request was eligible for caching.

A 2026-09-19 census of 1,129 real Claude Code sessions found zero
`compact_boundary` events, so no Bash `tool.call` hook ships.

### Code Index, Context Packs, and Graph Search

Armada owns a repository code index for dispatch-time retrieval:

- `armada_code_search` searches indexed chunks for a vessel.
- `armada_context_pack` builds dispatch-ready markdown and returns a prestaged `_briefing/context-pack.md`.
- `armada_fleet_code_search` and `armada_fleet_context_pack` retrieve across a fleet.
- Graph tools search symbols, callers, callees, impact, and affected tests from sidecar files.
- Hybrid search can combine lexical and semantic ranking when semantic search is enabled. Semantic embeddings use a Voyage AI client (`voyage-code-3`); supply the embedding key from the environment to enable live indexing.
- Context packs can be attached automatically during MCP dispatch and architect decomposition.
- Search and context packs never create an index. On a vessel that has never been indexed, vessel search returns `Available: false` with `UnavailableReason: not_indexed`, fleet search and fleet packs list the vessel in `NotIndexedVesselIds`, and a context pack stages no file. Nothing from that vessel reaches the embedding provider. An `auto` dispatch then proceeds without code context and logs the reason, even when `codeIndex.requireContextPackWhenEnabled` is set; a `force` dispatch fails and names the reason.
- Only an explicit index update (`armada_index_update` or the REST update route) indexes a vessel for the first time. Automatic refresh (after a landing, from the merge queue, from a stale-index dispatch, and from the staleness sweep) refreshes an already-indexed vessel in the background and never indexes a new one.
- A failed index update reports `Freshness: Error` with `LastError` and keeps the last successfully indexed commit, so the vessel stays enrolled and the staleness sweep still compares it.
- Index status reports lexical freshness (`Freshness`) and semantic completeness (`EmbeddingState`, `EmbeddedChunkCount`, `MissingEmbeddingCount`, `EmbeddingDimensions`) separately. Vectors belong to one embedding provider (model, endpoint and vector length): a provider change replaces every vector, including those on unchanged files, and an update on the same commit retries chunks whose embedding failed while it reuses the valid vectors.

### Merge Queue and Automated Landing

Armada can leave work for manual inspection or land it through configured modes:

| Mode | Behavior |
|---|---|
| `MergeQueue` | Enqueue work, create a temporary integration worktree, run validation, push, reconcile, and clean up branches sequentially per vessel and target branch. |
| `LocalMerge` | Merge the mission branch in a detached integration worktree, advance the local target branch by compare-and-swap, and sync the configured working directory, without pushing to origin. |
| `PullRequest` | Push the branch and open a PR/MR; the mission remains `PullRequestOpen` until the PR is merged. |
| `None` | Stop at `WorkProduced`; the branch remains available for manual integration. |

Landing features include auto-land predicates, protected-path checks, convention and critical-trigger gates, PR fallback, target-branch-drift retry, durable landing jobs, restart recovery, branch cleanup policies, pull-request reconciliation, and merge-queue purge/cancel tools.

### Structured Delivery Operations

Armada is not only a captain launcher. It also keeps delivery records connected to the work:

- Objectives and backlog items track scope, priority, effort, acceptance criteria, non-goals, rollout constraints, owners, tags, and evidence links.
- Planning and backlog-refinement sessions preserve captain-backed scoping conversations before dispatch.
- Workflow profiles define build, test, package, deploy, rollback, smoke-test, and health-check commands.
- Check runs persist structured validation output and can import external CI results.
- Releases collect linked voyages, missions, checks, notes, versions, tags, and artifacts.
- Release shipping can notify an external CD system through an authenticated webhook (`cdWebhook`), with bounded retries, per-release delivery history, and a synthetic-payload test tool.
- Deployments support approval, execution, verification, and rollback records.
- Incidents track operational issues, hotfix handoff, evidence, mitigation, and closure.
- Runbooks provide guided operational procedures with execution history.
- The historical timeline correlates objectives, planning, dispatch, checks, releases, deployments, incidents, events, merge activity, request history, and runbook execution.

### Captain Health and Quarantine

The Admiral tracks captain state and health so a busy fleet remains debuggable:

- Captains move through idle, assigned, in-progress, planning, stopped, quarantined, and failure states.
- Health checks reclaim stale captains and docks after restarts.
- Diagnostics report active mission timing, dock git status, uncommitted files, launch/log hints, and code-index freshness.
- Quarantine and lifecycle controls prevent unhealthy captains — including those hitting provider quota or usage limits — from repeatedly taking work until an operator or reset window intervenes.
- Stop, recall, stop-all, and emergency controls are exposed through MCP, REST, dashboard, and WebSocket flows. Stop-all, deletion, and restart apply one shared rule on every interface: stop-all covers working captains and planning and refinement sessions and reports each failure; deletion refuses busy captains and removes the captain's events and sessions; restart keeps the captain record and resets only its runtime state.

### Playbooks and Persistent Memory

Playbooks are reusable markdown guidance that can be delivered inline, referenced, or attached into the worktree. Fleet, vessel, persona, captain, voyage, and per-mission selections merge into mission playbook snapshots so every captain receives the guidance that applied at dispatch time.

The built-in `Recorder` persona reviews the finished work of a voyage and records what is worth remembering. It is seeded and available: the `Recorded` pipeline runs it after a Worker, and `ProductDevelopment` ends with a Recorder stage. Every other built-in persona is told to recall existing memory before it acts.

Native captain memory keeps what earlier work learned: a vessel fact, a prior finding, or a procedure worth repeating, classified as episodic, semantic or procedural, with provenance, tags and a salience that orders recall. A stable key makes recording the same finding twice correct one record instead of scattering copies. Manage it over MCP (`search_memory`, `get_memory`, `create_memory`, `update_memory`, `delete_memory`) or REST (`/api/v1/memories`). Where a fleet keeps a shared external memory repository, that repository stays the authority for accepted rules and wins over a native record on conflict.

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

**Deployment note:** The active Armada server uses PostgreSQL. A file named
`armada.db` is a SQLite example or test artifact; it is not the database used
by the active server. Confirm the configured database type before inspecting or
deleting any database-looking file.

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
- MCP Streamable HTTP: `http://localhost:7891/mcp`

### Configure MCP Clients

The repository includes an MCP config that points at the default HTTP endpoint.
The endpoint refuses a request without a credential, so each client entry must
send one. Read it from the environment rather than writing the key into a file:

```json
{
  "mcpServers": {
    "armada": {
      "type": "http",
      "url": "http://localhost:7891/mcp",
      "headers": { "X-Api-Key": "${ARMADA_API_KEY}" }
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

### Run on system startup

To keep the Admiral running across reboots, use the scripted startup workflow. It publishes
`Armada.Server` into `~/.armada/bin`, deploys the dashboard, registers a platform service
definition, and verifies health on boot. Full guide:
[docs/RUN_ON_STARTUP.md](docs/RUN_ON_STARTUP.md).

Install the local deployment:

| Platform | Install | Update | Health check |
|---|---|---|---|
| Linux (`systemd --user`) | `scripts/linux/install-systemd-user.sh` | `scripts/linux/update-systemd-user.sh` | `scripts/linux/healthcheck-server.sh` |
| macOS (`launchd`) | `scripts/macos/install-launchd-agent.sh` | `scripts/macos/update-launchd-agent.sh` | `scripts/macos/healthcheck-server.sh` |
| Windows (scheduled task) | `scripts/windows/install-windows-task.bat` | `scripts/windows/update-windows-task.bat` | `scripts/windows/healthcheck-server.bat` |

Run the install script once, then use the update script after each rebuild to republish the
server and restart the service. The health-check helper verifies the dashboard responds.

#### Behind an enterprise proxy or firewall

If npm fails with `SELF_SIGNED_CERT_IN_CHAIN` on an SSL-inspecting corporate
proxy, add `--insecure` (or `-k`) to the install or dashboard-deploy script so
that run disables strict TLS checks for npm/Node only. Example:
`scripts/macos/install.sh --insecure` or
`scripts\windows\install.bat net10.0 --insecure`. Put the framework first on
Windows, then the flag. `dotnet` and NuGet still use the OS certificate store.
The same scripts deploy the committed `src/Armada.Dashboard/dist/` when Node.js
is not installed. Install, update, reinstall, publish-server, MCP, and
dashboard-deploy scripts honor `--insecure`. Prefer not to pass the flag each
time: set `NODE_TLS_REJECT_UNAUTHORIZED=0` (`set` on Windows, `export` on
Linux/macOS) in your shell, or run `npm config set strict-ssl false` once.

---

## MCP Integration

The primary MCP transport is HTTP JSON-RPC at:

```text
http://localhost:7891/mcp
```

Armada uses the official MCP C# SDK and supports the stateless MCP
`2026-07-28` protocol as well as legacy initialization-based clients. The
former `/rpc` path remains available as a compatibility alias.

To add Armada to Claude Code manually instead of using `armada mcp install`,
register its default HTTP MCP endpoint (`http://localhost:7891/mcp`):

```bash
claude mcp add --transport http --scope user armada http://localhost:7891/mcp \
  --header "X-Api-Key: ${ARMADA_API_KEY}"
```

Every MCP request must carry a credential; a request without one gets `401`.
`armada mcp install` writes each HTTP client entry with an `X-Api-Key` header
that reads `ARMADA_API_KEY` from the client's environment, so set that variable
before starting the client. No key is written to a configuration file. `docs/MCP_API.md` covers caller rules, the
captain launch credential and the SSH bridge.

Drop `--scope user` to add it for the current project only; substitute your
port if you changed `McpPort`. On enterprise-managed Claude Code this may fail
with `not allowed by enterprise policy` — that restriction is set by your IT
administrator (Claude Code's `allowedMcpServers` managed setting) and cannot be
overridden locally. See [docs/MCP_API.md](docs/MCP_API.md#transport) for the
managed-settings snippet and alternatives.

Common MCP tool groups:

- Fleet, vessel, captain, mission, voyage, dock, signal, event, persona, prompt-template, and pipeline enumeration.
- Dispatch, architect decomposition, mission status, voyage status, logs, diffs, and status transitions.
- Merge queue enqueue, process, retry, cancel, purge, and PR reconciliation.
- Code index status, update, search, context pack, fleet context pack, graph symbols, callers, callees, impact, and affected tests.
- Objective/backlog CRUD, refinement, planning, dispatch linkage, and the autonomous objective scheduler.
- Check run, release, deployment, incident, and runbook operations.
- Playbook management and mission playbook snapshots.
- Captain diagnostics, quarantine controls, AgentWake registration, long-running-job status, and directed wake delivery.

Send `X-Armada-Participant: <participantKey>` on MCP requests to receive pending
board mail appended to any tool result. A session that sends no header is
anonymous and receives none, so one session cannot read another's mail. Long
broadcast notes on a board read come back previewed; notes addressed to the
caller and unread wakes are always whole.

Discover live tool descriptions and input schemas with `tools/list`. Follow
`nextCursor` until it is absent. The complete operator workflow and current
catalog are in [`docs/armada-ops.md`](docs/armada-ops.md); transport behavior
is in [`docs/MCP_API.md`](docs/MCP_API.md).

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
- `/api/v1/objectives/{id}/dispatch-preview`
- `/api/v1/check-runs`
- `/api/v1/releases`
- `/api/v1/deployments`
- `/api/v1/incidents`
- `/api/v1/runbooks`
- `/api/v1/events/token-usage` (authoritative per-runtime/model token telemetry)
- `/api/v1/token-usage/summary` (bucketed token usage; input is split into uncached, cache-read and cache-write
  tokens for every runtime, and rows stored before that split are reported apart under the legacy rule)

Every route authorizes the caller as a global administrator, a tenant
administrator or an ordinary user; `docs/REST_API.md` holds the full
authorization matrix. A tenant administrator manages its own tenant's users and
credentials but never a global administrator or a protected user, and only a
global administrator and a credential's own user read its bearer token.
Stopping or restarting the server requires a global administrator unless a
deployment sets `RequireAuthForShutdown` to `false`.

---

## Architecture

```text
src/
  Armada.Core       Domain models, settings, database drivers, services, code index, and interfaces
  Armada.Runtimes   Runtime adapters for Claude Code, Codex, Cursor, Gemini, OpenCode, Mux, and the in-process ApiEndpoint loop
  Armada.Server     Admiral REST/MCP/WebSocket server, orchestrators, and dashboard host
  Armada.Helm       CLI for config, server start, and MCP setup
  Armada.Dashboard  React/Vite operator dashboard
```

The server constructs most services directly in `ArmadaServer.cs` and runs the fork's background orchestrators (objective scheduler, automatic check runs, autonomous recovery, incident lifecycle, code-index refresh) off the health loop. Periodic maintenance on that loop (data expiry, disk reconciliation, branch cleanup) runs one isolated step at a time, so a failing step cannot skip the others. Database drivers cover SQLite, PostgreSQL, MySQL, and SQL Server. Runtime adapters implement the shared captain process contract while preserving each CLI's launch and environment requirements. The `ApiEndpoint` adapter runs an in-process tool loop and applies a deterministic prune and truncate-head to that conversation.

Every short-lived child process the admiral starts (git, `gh`/`glab`, `docker`, version and `command -v` probes, check, gate and merge-queue test commands, AgentWake agent runs, and the `run_command` tool), and each Helm `server start` build step, runs through one bounded runner in `Armada.Core`. It reads both output streams at once, so a child that fills one pipe while the other is open never blocks. Each stream keeps a per-call byte budget; past it the result keeps the beginning and the end and names the omitted bytes. A timeout and a caller cancellation both kill the live process tree, and the readers get a bounded drain window after exit or kill, so a background child holding a pipe cannot hang the call. Callers that run arbitrary shell commands (`run_command`, check and Definition-of-Done gate commands, merge-queue test commands, and the workspace exec route) also start the command as the leader of its own process group on Linux and macOS (through `setsid`, or `perl` where `setsid` is absent), and the kill signals that whole group, so a background child still dies after its shell has exited. Containment has platform limits. On Linux and macOS, a descendant that calls `setsid` or `setpgid` AND leaves the live tree (a double fork, or a subshell that exits) is in neither the group nor the tree, and survives the kill. On Windows there is no process group and no Job Object: only the live tree is killed, so a descendant whose parent has already exited survives. The other callers (git, `gh`/`glab`, `docker`, probes, AgentWake agent runs, self-deploy native commands, Helm build steps) rely on the tree kill alone. Git runs with terminal, credential-manager and pager prompts off; network git (fetch, push) uses the git timeout (`ARMADA_GIT_TIMEOUT_MS`, default 120 s). Long-lived and two-way processes (captain runtimes, OpenCode servers, MCP stdio sessions, the self-deploy host) keep their own supervision; the OpenCode server's two output streams are still read at once into the same per-stream byte budget, so a daemon's output is never held whole.

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
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net10.0
```

The last command runs the shared suites in `src/Test.Shared` and lists every skipped case with its reason; see [Testing](docs/TESTING.md#shared-suite-runner).

`scripts/macos/run-tests.sh` (or `scripts/linux/run-tests.sh`) runs all four at once, with the unit runner split into shards, and prints one combined result; see [Testing](docs/TESTING.md#sharded-unit-runs).

Dashboard asset changes require:

```bash
npm.cmd run build
```

from `src/Armada.Dashboard`.

---

## License and Attribution

Armada was created by [jchristn](https://github.com/jchristn) as [`jchristn/Armada`](https://github.com/jchristn/Armada). This repository is a private fork that builds on that work; all upstream copyright and attribution are retained.

Armada is licensed under the terms in [LICENSE.md](LICENSE.md).
