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
  <a href="#what-this-fork-adds-over-upstream">What This Fork Adds</a> |
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

## What This Fork Adds Over Upstream

This is a private fork of [`jchristn/Armada`](https://github.com/jchristn/Armada). It tracks upstream (last sync: 2026-08-14, merge-base `e9e3021f`, 21 upstream commits absorbed before this pass) and ships at v0.9.0, preserving upstream's v0.8.0 delivery-management model while adding several subsystems that turn Armada from a captain launcher into an autonomous, retrieval-aware delivery platform. Selected upstream v0.9.0 features (workspace terminal + diff, landing-retry conflict capture, and the needs-you inbox) have been ported back into the fork as first-class features and are listed below. The rest of upstream's v0.9.0 — its Voltaic-based MCP migration, Touchstone test harness, OpenTelemetry stack, and dashboard nav consolidation — is deliberately not absorbed because the fork's own implementations of those surfaces are more developed.

The additions below are grouped by subsystem. Except where noted, each subsystem is fork-original — the corresponding services, models, and MCP tools do not exist upstream.

### Pipelines, personas, and autonomous planning

- **In-dock Definition-of-Done gate.** Each Worker mission's build and unit-test commands run inside the mission's own isolated checkout before the mission is accepted, with structured failure classification (Compile / TestFail / Timeout / Infra) and bounded, secret-redacted diagnostics. Upstream missions are not build/test-verified in-dock before acceptance.
- **Hardened Architect decomposition.** A pure structured parser turns an Architect plan into N missions with Blocked / StructuralFailure / OverCap verdicts, a dedicated system prompt, MCP decompose tools, and a configurable per-vessel mission cap (default 8, clamped 1–50). Upstream ships the Architect persona but no parser, settings, over-cap verdict, or cap.
- **Autonomous objective scheduler.** A background sweep selects eligible objectives under guardrails and a max-concurrent-voyages cap, auto-dispatches each through the Admiral, links the resulting voyage, and reconciles objectives to Completed when their linked voyage finishes. Upstream persists objectives but dispatches them manually.
- **Automatic Check-run gate resolution.** A bounded background orchestrator resolves pending Check gates (Build, UnitTest, and others) as work lands, tying results into releases and incidents so a voyage cannot land without its attached Checks passing. Upstream persists Check records but has no background resolver.
- **Expanded persona and pipeline set.** Built-in personas (Worker, Architect, Product Manager, Usability Engineer, Judge, TestEngineer, MemoryConsolidator, and specialist reviewers) drive WorkerOnly, Reviewed, Tested, FullPipeline, ProductDevelopment, specialist-tested, and reflection pipelines. Personas are stored records, not hardcoded prompt strings, so custom personas and templates can be added via REST or MCP.
- **Stage-lag branch hardening in pipeline handoff.** When a prior pipeline stage's dock is detached (for example the PortingReferenceAnalyst persona), its produced commit may never reach the shared mission branch ref — it is only captured to `refs/armada-preserved/<branch>` on dock teardown, so the next attached stage (TestEngineer, Judge) silently loses the prior stage's source-fidelity work. The handoff now resolves the dependency's produced commit from its still-alive dock HEAD and force-advances the shared branch ref plus its origin twin before the downstream stage is assigned. Upstream pipelines always attach worker work to a named branch and have no detached-head stage-lag gap.
- **Domain-neutral framing.** Persona prompts, system resources, and test literals are kept generic so the fork ships no private-domain wording.

### Code index, semantic search, and context packs

- **Admiral-owned per-vessel code index.** The Admiral extracts each vessel's repo at a commit, chunks source files, and persists an inline index plus status metadata, powering search, symbol graphs, and context packs. Upstream has no code index at all.
- **Hybrid lexical + semantic search.** Search blends a lexical/regex score with cosine similarity over per-chunk embedding vectors, weighted by configurable semantic/lexical weights, using a pluggable OpenAI-compatible embedding client. Lexical search remains the always-on fallback when semantic search is disabled.
- **Dispatch-ready context packs.** A mission-goal-scoped evidence bundle (ranked files, symbol-graph context, prestaged files, metrics) is written to `_briefing/context-pack.md` and auto-attached to a dock at dispatch. A missing query or unavailable index hard-fails the dispatch rather than silently shipping a code-blind mission.
- **Caching and budgets.** A baseline context pack is pre-warmed after each index refresh and keyed to the indexed commit SHA; large vessels take a cheaper search-only fast-pack path; summarization and pack build are time-boxed so slow inference falls back to raw evidence instead of blocking dispatch.
- **Symbol-graph sidecars.** A dependency-free extractor emits per-vessel symbol/edge sidecars across many languages plus framework endpoint patterns, exposing callers, callees, bounded impact traversal, and affected-test suggestions, with matching symbols additively boosting search ranking.
- **Incremental post-land refresh.** When a voyage lands, a per-vessel debounce scheduler coalesces an incremental refresh (unchanged chunks reuse prior embeddings and sidecars). Dispatch is gated while a refresh is in progress, but the guard is timeout-bounded so a stalled index backend never blocks dispatch.
- **Fleet-wide search and packs.** Search and context-pack assembly can span every vessel in a fleet, prefixing files with their vessel id while preserving the per-vessel metric shape.
- **Reflection-driven pack curation.** A reflections pipeline mines completed-mission captain logs into pack-usage buckets and proposes persisted per-vessel pre-selection hints that the context pack applies before ranking.

### Model-tier routing and preferredModel abstraction

- **Tiered routing.** Dispatchers pass an abstract complexity tier instead of a concrete model name; a pure selector resolves the tier to an eligible idle captain at dispatch time, with an upward-only fallback chain so strong captains aren't consumed by cheap work. There are two effective tiers — `mid` for Worker missions and `high` for specialist personas — and the legacy `low` selector maps to `mid`. Upstream has no tier concept and pins concrete models only.
- **Config-driven tier membership.** Tier lists live in settings, but classification also matches anchored model-family regexes so routine version bumps auto-register into the right tier without editing config.
- **Within-tier preference order.** A per-tier ordered list picks the first listed model with an eligible idle captain, falling back to random for unlisted models.
- **Provider-neutral model routing.** Tier membership and provider routes come from a configurable registry, not hard-coded model names. Each runtime reaches an external provider (for example zyloo) through its own launch mechanism — Codex through a per-endpoint `--profile` config layer, Claude Code through the Anthropic-native provider form, OpenCode through an inline OpenAI-compatible overlay — and per-captain provider credentials override the registry and are masked on the MCP captain surface. Selection prefers an idle non-native (external-provider) captain before native models in any tier, treats unranked models as equal random peers, and counts OpenCode-runtime captains as native for the preference.
- **Capability-aware selection.** Each model carries a 0–100 capability profile (telemetry richness, audit/reasoning fit, mechanical throughput, cost); a mission's optional capability hint (audit / reasoning-heavy / mechanical / doc-only) re-sorts eligible models by best fit before the preference step. The hint is persisted across all four database backends.
- **Specialist high-tier reservation.** Specialist personas are reserved to high-tier captains and have their preferred model upgraded at create time; the scheduler additionally holds back reserved high-tier slots from Worker dispatch, with an in-flight-demand deadlock guard so a high-tier-only fleet still primes.
- **Per-stage overrides.** Each pipeline stage can carry its own preferred model that overrides mission-level routing, so a single dispatched voyage can route different stages to different tiers.

### Recovery, incidents, reflection, and dispatch hardening

- **Autonomous mission recovery.** A policy service classifies failed and landing-failed missions, opens and links an incident, records a recovery runbook, and dispatches bounded rescue missions for recoverable non-landing failures — while leaving auth, quota, review, protected-path, dependency, and exhausted failures as open incidents for humans. A revise-retest-rejudge loop handles Judge failures.
- **Evidence-driven incident lifecycle.** Incidents move Open → Mitigated → RolledBack → Closed purely from Armada evidence: failed checks open incidents linked to the failed check; later passing checks, successful rescues, shipped releases, verified deployments, or completed rollbacks mitigate and close them after a quiet window; new matching failures reopen and raise severity.
- **Reflection memory.** Accepted mission evidence becomes reviewable per-scope learned-facts playbooks (vessel, persona, captain, pack, fleet) via MemoryConsolidator reflection missions, with a parser/verdict pipeline, duplicate-dispatch suppression by evidence window, and a write-side land of accepted facts.
- **Queryable long-running jobs.** Long operations run as request-independent background jobs with a sortable id and Accepted / Running / Succeeded / Failed status, so callers poll status instead of blocking a request.
- **Dispatch hardening.** Centralized configurable timeouts for admiral git processes and code-context/index queries, plus a non-blocking dispatch guard, make a stalled dependency, index backend, or pack build fail fast (or warn and proceed) instead of hanging dispatch before a voyage row exists.
- **Resource-pressure admission.** Dispatch is gated on available memory with OOM classification, so a host under pressure defers work instead of launching captains that die mid-mission (`93e500f3`, `8391b0f7`).
- **Merge-failure classification and recovery routing.** A pure router maps a classified merge failure plus a per-mission recovery-attempt count to a terminal action — redispatch off a fresh tip, spin up a rebase-captain mission, or surface for human resolution — with a bounded attempt cap for back-pressure.
- **Host-wide DoD-gate serialization.** The in-dock build/test gate holds a dock lease across a host-wide gate queue, so concurrent mission gates and operator check retries run one at a time instead of crashing the test host.
- **Checks on rescue voyages.** Rescue voyages carry Build and UnitTest checks from dispatch, and a Judge PASS is rejected with a named failure reason when no green independent check is attached, so a rescued mission cannot land on its own evidence alone.
- **Rescue-tier resolution.** A rescue mission's model tier resolves against the persona it will actually run as, so a Worker rescue can never sit unassignable waiting on a high-tier-only roster.
- **No-op completion rejection.** A mission that "completes" within seconds with an empty diff and only a bare completion marker is rejected as `no_op_completion_detected` and re-dispatched to a different captain, so a runtime false-complete cannot pass the pipeline as progress.

### Delivery: docks, merge queue, and landing

- **Dock-boundary scanner.** A pre-land scanner evaluates a dock's diff and changed-path set against protected-path globs, secret patterns, and per-vessel private-identifier denylists for public repos, returning structured blocking findings without echoing secret bytes. Upstream lands captain diffs without a boundary scan.
- **AutoLand predicate.** A per-vessel predicate (enabled, max files, max added lines, allow/deny path globs) plus a pure evaluator parses the unified diff and decides whether a passing mission auto-lands or is held for review, wired into landing and exposed via REST/MCP.
- **Durable landing-job recovery.** Landing is a persisted job with a full state machine (Queued → Rebasing → Merging → Testing → Passed → Pushing/CreatingPR → Landed/PullRequestOpen/Failed/Cancelled), bounded drift retries, and restart-safe integration-worktree merges, so a landing interrupted mid-flight resumes instead of corrupting the target.
- **Sibling-repo and artifact dock provisioning.** Each vessel-declared sibling repository and declared extraction/reference artifact is provisioned into the captain's worktree at declared relative paths, using detached sibling worktrees that skip failed siblings and never destroy a concurrent dock's checkout.
- **Branch preservation and cleanup.** On dock reclaim, the captain's branch is mirrored into a preserved ref in the vessel bare before the worktree is destroyed (best-effort, never blocking), and the bare HEAD is restored to the default branch after cleanup so later git ops don't see a dangling ref.
- **Merge queue with PR fallback.** The queue lands sequentially per vessel and target branch, landing each success immediately to avoid cascade failures. It classifies failures at fail-time, routes audit-critical or failed entries to a pull request instead of auto-land, unblocks dependent entries at PullRequestOpen, and refreshes the code index after each land.
- **LocalMerge no-push landing.** LocalMerge lands by merging into the local working directory and never pushes to origin; push-based modes verify the remote target commit after push; terminal mission branches are reliably deleted per the branch-cleanup policy, surfacing cleanup failures.
- **Landing-retry conflict capture** *(port of upstream v0.9.0)*. When a landing retry fails, the mission's `FailureReason` records the conflicted-file list (`git diff --name-only --diff-filter=U`) so the operator sees exactly which paths to fix, exposed as `IGitService.GetConflictedFilesAsync`.

### Operations and deployment

- **Needs-you inbox** *(port of upstream v0.9.0)*. A consolidated `InboxService` aggregates everything across the fleet awaiting a decision or intervention — missions in Review, failed landings, failed missions, failed merges, deployments pending approval, failed or verification-failed deployments, and stalled captains — ordered most-urgent first. Purely informational state changes are excluded. Exposed as the MCP `inbox` tool, `GET /api/v1/inbox`, the dashboard `Needs You` page, the `armada inbox` CLI command (`--critical` filters), and `ArmadaApiClient.GetInboxAsync`.
- **Vessel workspace terminal + diff** *(port of upstream v0.9.0)*. The workspace service runs a bounded shell command in a vessel's working tree (the in-browser dock terminal, tenant-admins only, killed with its process tree on timeout) and returns a unified working-tree git diff for in-app review. Every workspace git invocation is bounded by a 30-second timeout that kills the process tree and suppresses the pager and credential prompts, so a wedged git can no longer hang the workspace endpoints.
- **Stale background-job reap** *(port of upstream v0.9.0's DB job persistence)*. The process-local `LongRunningJobService` now fails any Accepted or Running job past a stale threshold on the health-loop cadence, so a hung or dead background worker reaches a terminal status instead of reading as in-flight forever.
- **REST captain unquarantine** *(port of upstream v0.9.0)*. `POST /api/v1/captains/{id}/unquarantine` restores a quarantined captain to the idle pool and clears its reason and reset window, giving the existing MCP `armada_unbench_captain` tool a REST counterpart.

- **Self-deploy with supervised restart.** The Admiral can rebuild and restart itself from a landed commit under a supervisor, so a fork change reaches the running server without a manual build step. Off by default (`b817f605`).
- **One-shot server provisioning.** `bootstrap-server.sh` provisions a fresh host end to end — runtimes, Docker, Postgres, and the Admiral — so a new deployment does not depend on remembered steps (`241e7b1e`).
- **Live admission and routing settings.** The settings API exposes resource-admission and model-tier policy, and the Admiral hot-reloads the settings file so an operator can correct routing without a restart (`847dbb39`).
- **Bounded disk lifecycle.** A background scan classifies disk use by owned category; a reconcile pass reclaims only eligible items — past grace period, under allowed roots, not referenced by active state — including stale sibling-worktree leases. A maintenance sweep prunes merged mission branches per the vessel cleanup policy.
- **Captain papercuts.** Captains report friction on a structured `[ARMADA:PAPERCUT]` line; the Admiral collapses repeated reports by vessel, category, and problem so operator triage starts from an aggregated signal and a distinct-captain count.

### Captain prompt shaping and context budget

- **Mission modes with a mode-aware completion gate.** A mission is `Implementation`, `Audit`, or `Research`. Read-only modes receive a reduced brief — no commit, merge-conflict, or learned-fact modules — and a report-shaped output contract, and the landing gate treats an absent commit as their success condition instead of a `worker_produced_no_commits` failure. An unrecognized mode is rejected rather than silently defaulted (`4751e3ce`, `7f16423f`, `e128228a`).
- **Prompt-component byte accounting.** A ledger records the UTF-8 size of every module written into a captain brief plus the launch-prompt size, emitted as `mission.prompt_budget` and `mission.launch_prompt_budget` events with a configurable budget warning. Prompt cost is measured by the Admiral rather than estimated by the captain, which matters because several runtimes report no token usage at all (`7da79de6`).
- **Test ownership resolved from the pipeline that ran.** Who owns tests is derived from the stage missions a dispatch actually created, not asserted in prompt text. A single-stage pipeline tells the Worker it owns tests, a following Test Engineer is directed to gap coverage with an escalation record for defects it may not fix, and the Judge is told not to withhold a PASS for a stage the pipeline never had (`a52e604d`, `7f16423f`).
- **Per-runtime instruction filenames without double delivery.** Each runtime gets the instruction filename it actually loads — `AGENTS.md` for OpenCode, `CLAUDE.md` for Claude Code, `GEMINI.md` for Gemini — and when the runtime already auto-loads that root file the brief points at it instead of inlining a second copy. A stale generated model-context dump found in a tracked instruction file is refused rather than re-fed (`7da79de6`, `1cc521a5`).
- **Shared memory as a prompt module.** A configured `aiMemoryRoot` puts one pointer to the shared memory index into every brief, for every runtime, naming the index only so memory content never inflates the prompt (`1cc521a5`).
- **Git anchors resolved once, instead of by every captain.** The brief states where the work starts, the target branch tip, the recent commits touching each path the mission names, and whether the mission's subject terms already exist on the checkout. A captain otherwise spends its opening turns deriving these, and that search output then occupies its context for the rest of the mission. Absence is stated explicitly and anchored to the commit it was proven against, because proving a subject absent is several turns of searching and a captain that cannot prove it either duplicates landed work or stops to ask. Prior art counts files rather than lines, so the number is exact rather than a bounded sample. Nothing resolved renders nothing; a partial resolution is marked `INCOMPLETE`, so silence in a half-finished block is never read as absence. Plain git only — it does not depend on code indexing (`8066fbce`).
- **Context switches that actually remove text.** Disabling code indexing or learned facts removes their guidance from the brief entirely, and the brief names no MCP tool a captain was not given; dock MCP client seeding is opt-in and off by default (`48be4c36`, `1cc521a5`).
- **Idempotent stage handoff with a bounded description.** A repeated handoff for the same upstream mission replaces its block instead of appending a second copy, and a persisted mission description is capped with the truncation logged (`7da79de6`).
- **Hard prompt-budget backstop.** When a composed brief would exceed the captain budget, the total-budget backstop elides lower-priority content modules, and the persisted mission description and stage handoff stay under the same budget, so an oversized brief cannot ship silently.
- **Measured prompt-budget telemetry.** Recorded brief sizes surface on mission status alongside authoritative runtime token usage, so prompt cost is comparable across runtimes without relying on captain self-reports.
- **Bounded three-lens Judge contract.** The Judge reviews through correctness, blast-radius, and source-fidelity lenses, must exhibit a real corpus-present affected case to block, and emits its verdict synchronously so a discarded review cannot silently re-run (`3fe3ea74`, `1c8d6b9d`). The Judge must emit a verdict line before exit, an explicit agent verdict wins over a non-zero process exit, and a rejected PASS cannot loop into a fresh rescue.

### Captains, runtimes, and interfaces

- **Multi-runtime captain pool.** Claude Code, Codex, Cursor, Gemini, and OpenCode captains schedule through the same mission, voyage, dock, and merge-queue model. OpenCode is a fork-added runtime with JSONL event parsing, standalone operation, tier registration, custom OpenAI-compatible providers, and a permission-config builder wired into dock provisioning. External-provider Claude captains (for example cun-ai) use Claude Code's Anthropic-native provider form. (The Mux runtime is [jchristn/Mux](https://github.com/jchristn/Mux), a .NET CLI agent driven headless via `mux print`; the image builds it from source in `src/Armada.Server/Dockerfile`. Do **not** install the npm package also named `mux` — that is Coder's unrelated "coder multiplexer" and its incompatible CLI silently breaks captain launches. It remains in-tree but is not part of the fork's default pool — configure it explicitly before relying on it.)
- **Captain health and quarantine.** Captains are auto-quarantined on provider quota / usage-limit and credit/auth signals (honoring per-provider reset times), and a health monitor detects near-instant crash loops, so tier selection only ever hands out live captains. Upstream has no quarantine state or health monitor.
- **Cross-runtime hardening.** Role/persona context is injected uniformly into every runtime's prompt, provider usage-limit crash signatures are detected across runtimes, and MSBuild node-reuse is disabled on captain launch.
- **Expanded MCP surface.** The built-in catalog has 177 operator tool names for planning, code-index retrieval, operational assets, reflection memory, Checks, delivery, incidents, audits, Architect decomposition, AgentWake, long-running jobs, objective scheduling, captain diagnostics, and unlanded branches. Normal discovery fits on one compatibility page, while larger extension catalogs retain standard cursor pagination.
- **Shared REST/MCP dispatch parity.** Voyage dispatch is consolidated onto a single path shared by REST and MCP, mapping validation failures to structured MCP errors so orchestrator agents get the same semantics as REST clients.
- **Per-captain reasoning effort.** A captain carries a reasoning-effort tier that each runtime translates to its own control: `-c model_reasoning_effort` for Codex, a `MAX_THINKING_TOKENS` budget for Claude Code, `--variant` for OpenCode. A companion flag lets recovery retry a Claude captain without extended thinking (`b5088b0d`, `bc038cf2`).
- **Readable runtime logs.** A structured formatter resolves real tool names out of each CLI's nested JSON event schema, with redaction and truncation, and a noise filter drops envelope-only lifecycle records at both write and read. Mission logs show named tool activity instead of per-event narration (`4937f277`, `6fcae76d`).
- **Captain launch isolation.** Claude Code captains launch with `--setting-sources project,local` and `--strict-mcp-config`, so a captain cannot inherit user-level settings or MCP servers from the host (`9edc1747`).
- **Remote control over the relay tunnel.** Focused remote-control queries and management actions route through the existing outbound dashboard-relay tunnel, letting an operator inspect and control an Admiral remotely without exposing the full REST surface. (The relay/tunnel transport itself is upstream; the structured query/control layer over it is fork-only.)

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
- `FullPipeline`: Architect, Worker, TestEngineer, then Judge.
- `ProductDevelopment`: Product Manager, Architect, Worker, Usability Engineer, TestEngineer, then Judge.
- Specialist-tested pipelines add a domain reviewer before tests and Judge.
- Reflection pipelines: MemoryConsolidator alone or MemoryConsolidator with parallel Judges.

Personas are stored records, not hardcoded prompt strings. Custom personas and prompt templates can be added through REST or MCP and then referenced by custom pipeline stages.

### Model-Tier Routing

Dispatchers can use `preferredModel` as routing guidance:

- `mid` and `high` select among available captains in a complexity tier; the legacy `low` value maps to `mid`.
- Literal model names remain available for direct pins.
- Pipeline stages can override mission-level routing with their own `PreferredModel`.
- Specialist personas such as Judge, Architect, TestEngineer, and MemoryConsolidator are reserved for high-tier captains by default.
- Reserved high-tier slots keep strong captains available for downstream specialist work instead of being consumed on first-stage Worker missions.

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
| `LocalMerge` | Merge the mission branch directly into the configured local working directory, without pushing to origin. |
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

The repository includes an MCP config that points at the default HTTP endpoint:

```json
{
  "mcpServers": {
    "armada": {
      "type": "http",
      "url": "http://localhost:7891/mcp"
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

---

## MCP Integration

The primary MCP transport is HTTP JSON-RPC at:

```text
http://localhost:7891/mcp
```

Armada uses the official MCP C# SDK and supports the stateless MCP
`2026-07-28` protocol as well as legacy initialization-based clients. The
former `/rpc` path remains available as a compatibility alias.

Common MCP tool groups:

- Fleet, vessel, captain, mission, voyage, dock, signal, event, persona, prompt-template, and pipeline enumeration.
- Dispatch, architect decomposition, mission status, voyage status, logs, diffs, and status transitions.
- Merge queue enqueue, process, retry, cancel, purge, and PR reconciliation.
- Code index status, update, search, context pack, fleet context pack, graph symbols, callers, callees, impact, and affected tests.
- Objective/backlog CRUD, refinement, planning, dispatch linkage, and the autonomous objective scheduler.
- Check run, release, deployment, incident, and runbook operations.
- Playbook management, mission playbook snapshots, and reflection memory.
- Captain diagnostics, quarantine controls, AgentWake registration, long-running-job status, and wake notifications.

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
- `/api/v1/check-runs`
- `/api/v1/releases`
- `/api/v1/deployments`
- `/api/v1/incidents`
- `/api/v1/runbooks`
- `/api/v1/events/token-usage` (authoritative per-runtime/model token telemetry)

---

## Architecture

```text
src/
  Armada.Core       Domain models, settings, database drivers, services, code index, and interfaces
  Armada.Runtimes   Runtime adapters for Claude Code, Codex, Cursor, Gemini, OpenCode, and extensible agents
  Armada.Server     Admiral REST/MCP/WebSocket server, orchestrators, and dashboard host
  Armada.Helm       CLI for config, server start, and MCP setup
  Armada.Dashboard  React/Vite operator dashboard
```

The server constructs most services directly in `ArmadaServer.cs` and runs the fork's background orchestrators (objective scheduler, automatic check runs, autonomous recovery, incident lifecycle, code-index refresh) off the health loop. Database drivers cover SQLite, PostgreSQL, MySQL, and SQL Server. Runtime adapters implement the shared captain process contract while preserving each CLI's launch and environment requirements.

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

## License and Attribution

Armada was created by [jchristn](https://github.com/jchristn) as [`jchristn/Armada`](https://github.com/jchristn/Armada). This repository is a private fork that builds on that work; all upstream copyright and attribution are retained.

Armada is licensed under the terms in [LICENSE.md](LICENSE.md).
