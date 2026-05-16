<p align="center">
  <img src="assets/logo.png" alt="Armada Logo" width="200" />
</p>

<h1 align="center">Armada</h1>

<p align="center">
  <strong>Reduce context switching across projects. Keep agent work in queryable memory.</strong>
  <br />
  <em>v0.8.0 alpha -- APIs and schemas may change</em>
</p>

<p align="center">
  <a href="#why-armada">Why Armada</a> |
  <a href="#features">Features</a> |
  <a href="#how-it-works">How It Works</a> |
  <a href="#quick-start">Quick Start</a> |
  <a href="#pipelines">Pipelines</a> |
  <a href="#use-cases">Use Cases</a> |
  <a href="#architecture">Architecture</a> |
  <a href="#rest-api">API</a> |
  <a href="#mcp-integration">MCP</a>
</p>

---

## Why Armada

Armada is for people working across multiple repositories who are tired of paying the context-switching tax every time they come back to a project.

The first problem is operational: switching between projects means rebuilding context over and over. What was in flight, what already landed, what failed, what the agent was about to do next. That overhead adds up fast.

The second problem is memory. Most agent sessions disappear into terminal history and branch diffs. A week later, neither you nor the next agent has a clean way to ask "what happened here?" without manually piecing it back together.

Armada is built around those two problems:

1. **Reduce context switching across projects.** Armada keeps the state of work outside your head. You can dispatch, leave, come back later, and see where things stand without reconstructing everything from scratch.

2. **Provide extended, queryable memory for both users and agents.** Missions, logs, diffs, status changes, and related work are preserved behind a searchable interface. You no longer have to remember what you were working on; you can ask. Agents can do the same.

Armada gives models a place to maintain working context on a vessel over time. Agents can update vessel context with notes, hints, and project-specific guidance so the next dispatch does not have to rediscover the same facts from scratch. That reduces context load time for both humans and models.

Everything else in Armada exists to support that: isolated worktrees, parallel dispatch, pipelines, retries, dashboards, API access, and MCP tools.

### What You Get

- **Less project-switch overhead.** Leave one repo, work somewhere else, then come back to a current view of what happened.
- **A queryable memory layer.** Logs, diffs, status history, and agent output stay available through the dashboard, API, and MCP instead of vanishing into scrollback.
- **Persistent vessel context.** Models can maintain repository-specific context, hints, and working notes on each vessel to speed up future dispatches.
- **Dispatch-ready code context packs.** Admiral can index a vessel's default branch, search it, and generate a `_briefing/context-pack.md` file to stage into a mission before an agent starts.
- **Interactive planning before dispatch.** Chat with a captain in the dashboard, keep the transcript, then open the result in Dispatch or launch the work directly from the planning screen.
- **Parallel execution across repos.** Dispatch work to multiple agents across multiple repositories at once.
- **Quality gates that run automatically.** Every piece of work can flow through a pipeline: plan it, implement it, test it, review it. No manual intervention between steps.
- **Git isolation by default.** Every agent works in its own worktree on its own branch. Agents can't step on each other. Your main branch stays clean until you merge.
- **Configurable and extensible workflows.** Prompt templates, personas, and pipelines are user-controlled, so you can adapt the system to your project instead of fitting your project to the built-ins.
- **Reusable playbooks at dispatch time.** Store markdown guidance such as `CSHARP_BACKEND_ARCHITECTURE.md`, manage it in the dashboard, and select it per voyage or mission with inline or file-based delivery modes.
- **Works with the agents you already have.** Claude Code, Codex, Gemini, Cursor, and Mux -- pluggable runtime system.
- **Guided setup in the dashboard.** First-run configuration can stay inside the setup wizard instead of bouncing between unrelated pages.
- **Internationalized dashboard UX.** Login, shared shell UI, list/detail/admin routes, setup flows, notifications, pagination, server management, and legacy embedded dashboard surfaces support live language selection and locale-aware formatting.

### Who It's For

- **Solo developers** working across multiple repos.
- **Tech leads** who want a record of what agents changed.
- **Teams** that need shared visibility into agent-driven work.
- **Anyone** who wants more structure than a single-agent terminal loop.

---

## Fork features vs upstream

Last upstream sync: `9fcfe995` (20 upstream commits absorbed) on 2026-05-15.

This fork (`developervariety/Armada`) is based on `jchristn/Armada` and keeps upstream's v0.8.0 delivery-management work while adding orchestration features for multi-vessel, multi-runtime dispatch with auto-recovery, reflection memory, and human-review gating.

### Fork-only features

- **PR-fallback flow for breaking changes.** New `PullRequestOpen` mission status between `Testing` and `Landed`. When a captain branch fails the merge-queue test pipeline (or hits a "Critical" path/size trigger), admiral opens a platform-aware PR (`gh` on GitHub, `glab` on GitLab) and stays in `PullRequestOpen` until a human reviewer merges. The reconciler then transitions the entry to `Landed` automatically. (`0b6ace6`, `9494b62`, `644f2e8`, `e85d88d`, `63b6f6f`)
- **Auto-recovery pipeline.** When merge-queue testing fails, `MergeFailureClassifier` categorizes the failure (test-failure, conflict, build-error, etc.) and `RecoveryRouter` dispatches a `RebaseCaptain` (with `pbk_rebase_captain` playbook) to attempt remediation. Recovery exhaustion falls through to PR-fallback. Mission rows track `recovery_attempts` + `last_recovery_action_utc`. Schema v38. (`3276aa5b`, `26700062`, `3be8b13`, `8c1fa2cf`)
- **Audit queue + Judge-hybrid review.** Judge verdicts of `NEEDS_REVISION`, or `PASS` with non-empty Suggested-Follow-ups, route into a deep-review audit queue. Orchestrator drains via the new `armada_drain_audit_queue` MCP tool, records a final verdict (Pass / Concern / Critical) per entry. Calibration sampling pushes the first N AutoLand-eligible entries through deep review for predicate calibration. (`5fc5b11`)
- **Cross-vessel `dependsOnMissionId`.** A mission on vessel A may depend on a mission on vessel B. Cross-vessel deps wait for `Complete` (no `WorkProduced` shortcut, since branch handoff across repos is meaningless), and the downstream always gets a fresh branch on its own vessel. (`55fd1367`)
- **Logical-alias `dependsOnMissionAlias` resolution.** Architect-emitted single-voyage plans dispatch with one or more `alias`-tagged missions; downstream missions reference upstream via alias instead of pre-resolved `msn_*` ids. Server topologically sorts at dispatch and resolves aliases to concrete ids in dependency order. Forward references and cycles rejected at validation. (`c061bd7d`)
- **Voyage-to-mission playbook propagation.** Vessel `DefaultPlaybooks` merge into every dispatch automatically; voyage-level `selectedPlaybooks` cascade into each mission. Architect and `armada_create_mission` paths included. (`8143268`, `f52720a0`)
- **Per-stage `PreferredModel` on `PipelineStage`.** Pipeline stages can route by tier (`low`, `mid`, `high`) or literal model. Tier routing randomly selects among available peer models in the tier and upgrades upward when a tier has no available captain; literal model names remain an escape hatch. Alias-aware dispatch path expands pipeline stages correctly. (`5ae0ce4b`, `fc48ea41`, `dae5a50a`, `fbc724f8`)
- **`ProtectedPaths` per-vessel gate.** Vessels carry a glob-list of paths that captain commits may NOT touch. Captain commits to `**/CLAUDE.md` (or other protected paths) are rejected with a coaching message teaching the `[CLAUDE.MD-PROPOSAL]` block format for proposing rule changes. (`02e52f6`)
- **Cursor-agent launch hardening.** Cursor runtime feeds the prompt to `cursor-agent` via stdin instead of inlining as a CLI arg, bypassing Windows `cmd.exe`'s ~8 KB command-line limit. On Windows it also prefers the official Cursor agent install path under `%LOCALAPPDATA%\cursor-agent\cursor-agent.cmd` when PATH/npm shim resolution would hit the broken `AppData\Roaming\npm\cursor-agent.cmd` wrapper that prints repeated "The system cannot find the path specified." lines and exits `0` without invoking the model. (`db9439c`, `351d7f88`)
- **`GitService.IsPrMergedAsync` platform-aware.** PR-merge detection routes to `gh pr view` (GitHub) or `glab mr view` (GitLab) based on URL host. Closes a silent-failure path where GitLab MRs always appeared "not merged" because `gh` returned non-zero on unsupported hosts. (`63b6f6f`)
- **Captain-lifecycle hardening.** Captain process cleanup on cancel + launch log lock recovery prevents orphaned worktrees and stuck launch state. Merge-queue land + dock-delete honor the vessel's `BranchCleanupPolicy`. (`7f99fa9`, `23c22eb7`)
- **`JsonStringEnumConverter` registered for settings.json mode loading.** Fixes a startup crash when `settings.json` carries `RemoteTriggerMode` as a string. (`3dcecd5`)
- **`RemoteTriggerMode.AgentWake` -- local Claude/Codex one-shot wake.** New transport mode that spawns a local Claude Code or Codex CLI process only when Armada has real work: WorkProduced, MissionFailed, auto_land_skipped, and audit-Critical events. No HTTP token, Routines API, or constant daemon loop required. Configure via `remoteTrigger.mode = "AgentWake"` with optional `agentWake` subsection (`runtime`, `runtimePreference`, `command`, `sessionId`, `workingDirectory`, `timeoutSeconds`, `environmentVariables`). Supports fixed `Claude`/`Codex` runtime selection or `Auto`, where MCP clients can call `armada_register_agentwake_session` so Armada resumes the most recently registered orchestrator session before falling back to the configured runtime order. Supports Claude `--print --continue` / `--resume <sessionId>` and Codex `exec resume --last -` / session resume paths. Per-vessel 60s coalescing, configurable rolling-hour throttle (`remoteTrigger.throttleCapPerHour`, default 20), and global single-flight lease prevent burst spawning. LocalDaemon mode has been removed; AgentWake is its replacement.
- **Codex/Claude MCP startup hardening.** `armada mcp install` now configures Codex through the native `codex mcp add` flow and the correct `~/.codex/config.toml` target, using a stdio Armada entrypoint from source checkouts. Claude Code captain launches are restricted to project/local settings with `--strict-mcp-config` so user-level MCP servers do not leak into headless captain processes.
- **Captain diagnostics for long-running/no-diff cases.** `armada_captain_diagnostics` is a read-only MCP tool for investigating captains that appear busy without producing files. It reports active mission elapsed time, dock git status, uncommitted files, launch/log hints, and code-index freshness/staleness so operators can distinguish stuck model startup, missing context, stale index, and normal long-running work.
- **Mission/dashboard/MCP memory hardening.** Mission stdout/stderr buffers are capped, planning-session live output is tail-bounded, status/progress APIs use grouped counts instead of hydrating all mission rows, mission list and MCP enumerate/status paths project lightweight summaries that omit `description`, `diff_snapshot`, and `agent_output`, and the embedded dashboard coalesces refreshes while avoiding background merge-queue enrichment outside the merge view. This fixes the observed Admiral memory growth and browser out-of-memory failure when many missions or planning turns carry large logs/output. (`0fe4411c`, `22869d01`, `c6b8ffe1`, `7cf460e9`)

- **Alias-dispatch playbook snapshot fix.** Downstream pipeline stages in alias-aware multi-stage dispatch now persist `MissionPlaybookSnapshot` rows the same way the first stage does. Previously, Judge and subsequent stages had no snapshots, so playbook content was missing from the rendered captain brief. Also clarifies the merge hierarchy (vessel defaults < voyage `selectedPlaybooks` < per-mission `selectedPlaybooks`) in docs and duplicate-prevention behavior in code and docs.
- **Tracked default `docker/server/armada.json`.** A default server config template is now committed at `docker/server/armada.json` and included via `.gitignore` whitelist so fresh clones work with `docker compose up -d` without manually creating the file. Factory reset scripts already preserved `armada.json`; the `.gitignore` now explicitly allows it. (partial port of `28d0f846`)
- **Reflections memory pipeline.** Post-mission reflection system for vessel, pack, persona, captain, and fleet learned playbooks. Armada can auto-dispatch a `MemoryConsolidator` after enough completed missions, reorganize learned memory with dual-Judge validation, mine frequently referenced context packs, consolidate persona/captain behavior patterns, and promote cross-vessel fleet hints with fan-out and anti-thrash controls. Operators can trigger, accept, reject, and inspect proposals through reflection MCP tools. Schemas v40-v45. (`30855c67`, `7266a77a`, `6f2f01ff`, `aacd963c`, `98e2c739`, `f4000735`, `a6d9126c`, `56063c33`, `86e5efbf`, `465a687a`, `dffd0bb9`, `1f760248`, `b75053b0`, `de0b3567`, `9c223800`, `29ccadd4`, `cc2067ad`, `7286cf22`, `c43c35b2`, `ab96624d`, `5b45b421`, `7566191f`, `ecec69f4`, `3a4775ee`, `4ba68598`, `0b90da99`, `a865e366`, `1bec855f`, `b3188c23`, `9096c102`, `0f816b50`, `0b94a1d0`, `0bfa999a`, `eb7c5f86`, `38c20f0f`, `bace72d7`, `eec40778`)
- **Parallel pipeline stages.** Schema v42 allows same-order pipeline stages to dispatch as parallel siblings instead of sequentially. `AdmiralService` groups stages by `Order` when expanding a pipeline into missions. (`5e2a993f`)
- **DeepSeek embedding/inference clients + hybrid code search.** `DeepSeekEmbeddingClient` (implements `IEmbeddingClient`) and `DeepSeekInferenceClient` (implements `IInferenceClient`) wired into `CodeIndexService` for hybrid embedding+lexical search. `CodeIndexSettings.UseSemanticSearch` (default `false`) activates semantic score blending with configurable `SemanticWeight`/`LexicalWeight`. Summarizer (`UseSummarizer`, default `false`) and file-signature (`UseFileSignatures`, default `false`) also supported but disabled by default. (`d242cc7b`, `c2ec68f1`, `86724a1d`, `2883cb21`)
- **AgentWake MCP-notification delivery mode.** New `AgentWakeDeliveryMode.McpNotification` writes a `SignalTypeEnum.Wake` signal row instead of spawning a process. Interactive orchestrators drain pending wakes via `armada_enumerate entityType=signals signalType=Wake unreadOnly=true` and acknowledge with `armada_mark_signal_read`. `Both` mode does spawn + signal for transition deployments. (`35054fcd`)
- **Per-captain reasoning effort via RuntimeOptionsJson.** Captains carry `runtime_options_json` (schema v39 field) forwarding `reasoning_effort` to Codex CLI and supporting pinned cursor reasoning effort documentation. (`b5088b0d`)
- **Built-in specialist personas and tested pipelines.** Six specialist tested pipelines: DiagnosticProtocolTested, TenantSecurityTested, MigrationDataTested, PerformanceMemoryTested, ReferencePortingTested, FrontendWorkflowTested — each with corresponding specialist personas (DiagnosticProtocolReviewer, TenantSecurityReviewer, MigrationDataReviewer, PerformanceMemoryReviewer, PortingReferenceAnalyst, FrontendWorkflowReviewer). (`60781f72`)
- **Code context pack auto-attach on dispatch.** `CodeIndexService.BuildContextPackAsync` is called at architect and dispatch time to auto-attach a context pack (`_briefing/context-pack.md`) as a prestaged file when the code index is available. (`baec5512`)
- **Stdio MCP framing fix for Codex.** Corrects the framed stdio MCP transport so that Codex `mcp add` works correctly with the Armada server entrypoint. (`cec36fa6`)
- **Cursor model alias matching.** Preferred-model tier routing recognizes Cursor-specific model aliases (composer-2-fast, kimi-k2.5, claude-4.6-sonnet-medium, gemini-3-flash) alongside canonical Claude/Codex names. (`34574c5f`)
- **Configurable AgentWake throttle cap.** `remoteTrigger.throttleCapPerHour` setting (default 20) limits how many one-shot AgentWake processes can fire per rolling hour. (`48125adc`)

### Upstream features absorbed and actively used

- **Backlog and objectives.** Upstream v0.8.0 added first-class objectives, ranked backlog state, refinement sessions, and backlog CRUD/reorder aliases. This fork keeps those surfaces and uses objectives as the preferred durable scope for planning, dispatch, delivery, deployment, and incident evidence. (`efcc8221`, `afb80da0`, `84cef5b6`, `372dfc20`)
- **Workspace and request-history surfaces.** The React dashboard now includes vessel-aware Workspace, API Explorer, and request history views; orchestrators can use them to scope dispatch from selected files, replay request evidence, and debug API flows. (`7b70b7e0`, `de2dc5dc`, `b982f56f`, `bfdc7826`, `eb6b6c7a`)
- **Workflow profiles, structured checks, releases, and delivery operations.** Upstream delivery workflows added executable profiles, check runs, release records, environments, deployments, incidents, runbooks, historical timeline views, and related MCP/WebSocket events. This fork wires the new services into the server while preserving existing merge queue, audit, and reflection flows. (`adfdc3b3`, `6335f4eb`)
- **GitHub delivery evidence.** Global and per-vessel GitHub tokens, issue/PR objective import, GitHub Actions sync, and PR review/check evidence are available to connect external repository signals to Armada objectives, checks, missions, and releases. (`63d19996`)
- **Pipeline and mission review gates.** Upstream review gates are integrated with fork pipeline expansion and persistence across SQLite, MySQL, PostgreSQL, and SQL Server. A stage can require explicit approval, and denial either retries the stage with feedback or fails the pipeline. (`dd3d52c8`, `aef283b0`)
- **Captain tool visibility.** Captain list/detail pages and `GET /api/v1/captains/{id}/tools` expose the effective Armada MCP tool catalog for a selected captain, helping operators diagnose runtime/tool availability before dispatch. (`e5fe494d`)
- **Windows and dashboard deploy hardening.** The sync also brought shorter MCP tool names, dashboard deploy fixes, Docker database assets, and framework/script ergonomics that reduce setup friction on Windows and Docker hosts. (`270a9c53`, `28d0f846`, `1a46c2fb`)

### Upstream features in-tree but not actively wired

- **Mux captain runtime.** Absorbed via `cd27ea6`. Our active captain pool is ClaudeCode + Codex + Cursor + Gemini; we don't currently register Mux captains by default. The runtime stays available for future Mux endpoint integration, but orchestrators should not assume a Mux captain exists unless one is explicitly configured.

---

## Features

A by-category inventory of what Armada actually ships. Each feature is implemented in `Armada.Core` / `Armada.Server` / `Armada.Runtimes` and exposed via REST, MCP, the dashboard, or the CLI.

### Orchestration & Dispatch

- **Voyages and missions.** A voyage groups one or more missions; each mission is an atomic unit of work assigned to one captain working in one dock (worktree). Voyages target one vessel; missions inherit the vessel and may carry per-mission `prestagedFiles` and `preferredModel`.
- **Single-voyage dispatch with logical aliases.** Architect-emitted plans dispatch as one voyage. Each mission may carry an `alias` (e.g. `"M1"`) and depend on another via `dependsOnMissionAlias`. Server topologically sorts the alias graph at dispatch, resolves aliases to concrete `msn_*` ids in dependency order. Forward references and cycles rejected at validation.
- **`dependsOnMissionId` plumbing.** A dependent mission stays `Pending` until the referenced mission reaches a completion state (`Complete`, `WorkProduced`, or `PullRequestOpen`). Validation rejects unknown ids at dispatch.
- **Cross-vessel dependencies.** A mission on vessel A may depend on a mission on vessel B. Cross-vessel deps wait for `Complete` (skipping the `WorkProduced` shortcut, since branch handoff across repos is meaningless), and the downstream always gets a fresh branch on its own vessel — no branch inheritance across repos.
- **Foundation-first dispatch.** Use `dependsOnMissionId` (or alias) to gate downstream missions on an upstream that introduces shared infra; admiral promotes them only when the dependency is ready.
- **Pre-staged files.** Optional `prestagedFiles: [{sourcePath, destPath}]` per mission copies absolute Admiral-host paths into the dock worktree before captain spawn. Useful for spec snapshots, generated fixtures, briefing docs the agent shouldn't commit. Cap 50 entries / 50 MB per mission.
- **Code index context packs.** `armada_index_status`, `armada_index_update`, `armada_code_search`, and `armada_context_pack` provide an Admiral-owned per-vessel code index. `armada_context_pack` builds dispatch-ready markdown for a mission goal and returns a `prestagedFiles` entry for `_briefing/context-pack.md`. Index records include vessel id, repo-relative path, commit SHA, content hash, language, line range, and freshness. Hybrid search (schema v45) adds DeepSeek-powered embedding + lexical ranking with configurable blend weights (`SemanticWeight`/`LexicalWeight`, default 0.7/0.3). Enabled via `CodeIndexSettings.UseSemanticSearch: true`. Codex/context-pack tools transparently use hybrid ranking when active; lexical-only default otherwise.
- **Preferred captain / preferred model.** Pin a mission to a specific captain id, or use `preferredModel` for model routing. Pass `low`, `mid`, or `high` for tiered random selection (Armada picks a peer model within the tier from available captains); pass a literal model name (e.g. `claude-opus-4-7`) to pin to a specific model as an escape hatch. Tier: low=`kimi-k2.5`, mid=`composer-2-fast`/`claude-sonnet-4-6`/`gemini-3.5-pro`, high=`claude-opus-4-7`/`gpt-5.5`. When the requested tier has no available captains, Armada upgrades upward (low->mid->high, mid->high). Both honored across all dispatch paths (standard, alias, pipeline).
- **Architect-mode dispatch.** `armada_decompose_plan` runs an Architect captain that produces a markdown plan + N `[ARMADA:MISSION]` blocks; `armada_parse_architect_output` parses to a structured plan. Spec → architect → captain missions in one flow.
- **Planning Sessions (interactive plan-to-dispatch).** `PlanningSession` + `PlanningSessionMessage` persist a multi-turn planning conversation with a captain in the dashboard; `PlanningSessionCoordinator` reserves a captain + dock for the session lifetime, re-launching the captain with a `context.md` (instructions + vessel context + selected playbooks + transcript-so-far) on each user turn. `/api/v1/planning-sessions/{id}/dispatch` converts a chosen assistant message into one voyage with one seeded mission; `Voyage.SourcePlanningSessionId` + `SourcePlanningMessageId` preserve lineage. Captain enters `CaptainStateEnum.Planning` for the session lifetime and cannot accept other missions. The dashboard planning flow is SQLite-only and single-mission; use Architect-mode dispatch for multi-mission decomposition. Design doc: [`PLANNING.md`](PLANNING.md).

### Backlog, Objectives & Workspace

- **First-class objectives and backlog.** Objectives carry ranked backlog state, priority, effort, lifecycle status, acceptance criteria, non-goals, rollout constraints, tags, owners, and linked fleet/vessel/planning/voyage/mission/check/release/deployment/incident evidence.
- **Objective refinement sessions.** A user can refine backlog/objective scope with a selected captain, preserve the transcript, summarize the outcome, and apply the result back to the objective before dispatch.
- **Workspace handoff.** The dashboard Workspace can browse, search, edit, inspect changes, and hand selected vessel context into Planning or Dispatch. Use it when a task starts from concrete files rather than a free-form prompt.
- **Request history and API Explorer.** REST requests can be captured, searched, replayed, exported, and used as evidence from dashboard system views. This is useful for reproducing API bugs and attaching concrete behavior to objectives or incidents.

### Delivery & Operations

- **Workflow profiles.** Vessels and fleets can define named recipes for lint, build, unit test, integration test, e2e, package, publish, release-versioning, changelog, deploy, rollback, smoke-test, and health-check commands.
- **Structured check runs.** Checks persist status, timing, logs, artifacts, parsed test/coverage summaries, compare-to-previous-run analysis, branch/commit metadata, and links back to missions, voyages, releases, and deployments.
- **Releases, environments, deployments, incidents, and runbooks.** Armada can draft release records, seed named environments, execute/approve/verify/rollback deployments, track incidents and hotfix handoff, and run guided operational runbooks with execution history.
- **Historical timeline.** `Activity > History` correlates objectives, planning, dispatch, checks, releases, deployments, incidents, events, merge activity, request history, and runbook execution into one searchable delivery memory.
- **GitHub evidence.** Optional server or per-vessel GitHub tokens enable objective import from issues/PR scope, GitHub Actions sync into check runs, and PR review/check evidence on mission and release detail surfaces.

### Pipelines & Personas

- **Built-in pipelines.** `WorkerOnly`, `Reviewed`, `Tested`, `FullPipeline`, plus specialist tested pipelines for diagnostic protocol, tenant security, migration/data, performance/memory, reference porting, and frontend workflow review. Configurable at fleet/vessel level with per-dispatch override.
- **Custom pipelines.** Define your own ordered persona chain. MCP `armada_create_pipeline` accepts `stages: [{personaName, isOptional, description, preferredModel}]`; REST/dashboard pipeline editors also expose review-gate fields.
- **Per-stage `PreferredModel`.** Each pipeline stage can carry an optional `PreferredModel` that overrides the per-mission pin for missions created from that stage. Lets the `Reviewed` pipeline route Worker to Mid-tier Sonnet and Judge to Opus independently. Dispatcher precedence: `mission.PreferredModel = stage.PreferredModel ?? md.PreferredModel`.
- **Per-stage review gates.** A pipeline stage with `RequiresReview = true` pauses in `MissionStatusEnum.Review` after producing work. Approve to continue to the next stage or landing; deny with `RetryStage` to return the stage to `Pending` with feedback, or `FailPipeline` to stop the chain.
- **Built-in personas.** Worker, Architect, Judge, TestEngineer, DiagnosticProtocolReviewer, TenantSecurityReviewer, MigrationDataReviewer, PerformanceMemoryReviewer, PortingReferenceAnalyst, FrontendWorkflowReviewer.
- **Custom personas.** `armada_create_persona` registers a persona with `name`, `description`, `promptTemplateName`. Pipelines reference personas by name, so user-defined personas slot into stages just like built-ins.
- **Prompt templates.** Every prompt agents see is template-driven. `armada_create_prompt_template` / `armada_update_prompt_template` / `armada_reset_prompt_template`. Templates use `{Placeholder}` parameters. Built-in templates ship as defaults; user edits persist in the database.
- **Allowed-personas filter on captains.** Each captain has an `AllowedPersonas` list (e.g. Opus captains: `Worker, Judge, Architect`; Codex: `Worker, Architect`). Hard filter inside the dispatcher — a mission with `Persona = "Judge"` only routes to captains whose `AllowedPersonas` includes Judge.

### Agent Runtimes

- **Pluggable runtime adapter.** `IAgentRuntime` abstraction; `BaseAgentRuntime` factors out shared stdout/stderr/log piping + process lifecycle.
- **Claude Code.** Local `claude` CLI runtime. Captain launches use `--print --verbose --setting-sources project,local --strict-mcp-config` and strip nested Claude session environment variables so headless captains do not inherit user-level MCP/plugin state.
- **Codex.** OpenAI Codex CLI. On Windows, Armada maps full-auto execution to `--dangerously-bypass-approvals-and-sandbox`; other platforms use the normal full-auto flag. Per-captain `runtime_options_json.reasoning_effort` is forwarded to Codex.
- **Gemini.** Google Gemini CLI.
- **Cursor.** Cursor agent CLI (`cursor-agent`); supports cursor-specific models (composer-2-fast, kimi-k2.5, claude-4.6-sonnet-medium, gemini-3-flash). Prompts are sent on stdin, and Windows launches prefer the official Cursor agent install path when the npm shim is broken.
- **Cross-runtime model equivalence.** Captains expose a `Model` string used for both literal-pin matching and tier eligibility. Equivalence pairs (e.g. `claude-sonnet-4-6` and `claude-4.6-sonnet-medium`) are documented; the orchestrator picks overflow captains across runtime variants when needed. Tier-based dispatch randomly selects across peer models within a tier to avoid vendor concentration.

### Quality Gates & Auto-land

- **AutoLand predicate.** Per-vessel predicate gates auto-landing of merge-queue entries by file count + line count + path allow/deny lists. Configured via `Vessel.AutoLandPredicate`. Predicate-Skip events emit `merge_queue.auto_land_skipped`.
- **Auto-land calibration counter.** Each predicate-enabled vessel maintains an atomic `auto_land_calibration_landed_count`. The first 50 auto-lands per vessel get sampled into deep audit review regardless of trigger; after calibration, only flagged entries land in the deep queue.
- **Audit safety net — Layer 1 (sync).** `ConventionChecker` (CORE-rule pattern denylist on `+` lines) + `CriticalTriggerEvaluator` (path/content critical patterns: UDS 0x34, RSA primitives, schema migrations, security-sensitive paths) run after auto-land. Convention failures or critical triggers short-circuit auto-land and surface to the orchestrator.
- **Audit safety net — Layer 2 (async deep review).** `armada_drain_audit_queue` returns picked entries; orchestrator reviews via `armada_get_mission_diff` + `superpowers:code-reviewer`; `armada_record_audit_verdict` records `Pass | Concern | Critical`. Critical fires a wake event.
- **Breaking-change PR-fallback.** When a critical trigger fires on an entry that would otherwise auto-land, `MergeQueueService` pushes the captain branch to origin and opens a real platform PR (`gh pr create` / `glab mr create`) instead of merging. Entry transitions to `MergeStatusEnum.PullRequestOpen` with `PrUrl` + `PrBaseBranch`. Linked mission goes to `MissionStatusEnum.PullRequestOpen`. Same-vessel dependent missions unblock immediately and chain off the upstream captain branch — work continues while a human reviews the PR. When the PR merges, the mission reconciler flips to `Complete` and the merge-queue reconciler lands the entry. Chained-PR base resolution: when an upstream dep is itself in PR review, the downstream's PR targets the upstream's captain branch instead of vessel default.
- **Judge persona enforcement.** `Persona = "Judge"` only routes to captains with `Judge` in `AllowedPersonas` (Opus-only by policy in our deployment). Judge mission produces a structured verdict (`PASS | FAIL | NEEDS_REVISION`) with required `## Completeness`, `## Correctness`, `## Tests`, `## Failure Modes`, `## Suggested Follow-ups`, `## Verdict` sections. PASS verdicts with shallow output are rejected; verdict line `[ARMADA:VERDICT] {value}` parsed structurally.

### Judge Hybrid Autopilot

- **Suggested Follow-ups in Judge prompt.** Judge enumerates cleanup-mission candidates the diff implies but doesn't address (stale TODOs, helper extractions, missing test coverage of pre-existing branches, doc updates). `(none)` sentinel for empty.
- **Audit-flag wiring.** When Judge verdict is `NEEDS_REVISION` OR `PASS` with non-empty Suggested Follow-ups, the upstream Worker's most recent merge entry is marked `AuditDeepPicked = true` with `AuditDeepNotes` carrying the verdict label + follow-ups body. `armada_drain_audit_queue` surfaces it next call.
- **Audit-queue depth notification.** Each health-check cycle counts pending picked entries; when count crosses `ArmadaSettings.AuditQueueNotifyThreshold` (default 1) AND the debounce window (`AuditQueueNotifyDebounceMinutes`, default 30) has elapsed since the last notification, fire a desktop notification (cross-platform: macOS osascript, Linux notify-send, Windows toast).

### Merge Queue & Landing

- **Sequential test-before-merge.** Each `(vessel, target-branch)` group processes one entry at a time: fetch → create temp worktree from current target tip → merge captain branch → run tests → land immediately. Eliminates batch-style cascade failures.
- **Three landing modes.** `MergeQueue` (default — testable, auditable), `LocalMerge` (fast direct merge), `PullRequestMode` (always open PR, never auto-merge). Configurable per-vessel and per-voyage with vessel default.
- **Branch cleanup policy.** Per-vessel `BranchCleanupPolicy`: `None`, `LocalOnly`, `LocalAndRemote`. Symmetric local + remote cleanup runs after `Landed` transitions. Integration branches always pruned from bare regardless.
- **PR merge reconciliation.** Health-check loop polls `PullRequestOpen` missions via the platform CLI, transitions to `Complete` when merged + pulls the working tree. A second pass lands `PullRequestOpen` merge entries when their linked mission is `Complete`, then runs cleanup.
- **Merge-queue retry / cancel / purge.** `armada_process_merge_entry`, `armada_cancel_merge`, `armada_purge_merge_entry` / `armada_purge_merge_queue`, `armada_delete_merge`.

### Captain Lifecycle

- **Idle pool with on-demand spawn.** Captains stay idle until a matching mission arrives; `MinIdleCaptains` setting auto-spawns when below threshold. Health check cleans up stale captains on admiral restart.
- **Per-captain max parallelism.** Each captain serves one mission at a time (default); reservation logic prevents double-assignment.
- **Stop / recall.** `armada_stop_captain` (kills process + recalls to idle); `armada_stop_all` for emergency stop.
- **Cancel kills process.** `armada_cancel_voyage` and `armada_cancel_mission` now also kill the agent process for in-flight missions, reset the captain to Idle, and clear `Mission.ProcessId`. Without this, in-flight cancels left captains stuck Working forever.
- **Launch log lock recovery.** Mission log opens are best-effort cleanup-then-create; on share-violation, fall back to a unique-suffix log path so launches always succeed even when an orphan agent process from a prior crash holds the canonical log file.

### Playbooks

- **Vessel `DefaultPlaybooks`.** Per-vessel default list auto-merges into every dispatch.
- **Merge hierarchy.** Playbook selections merge in priority order: vessel defaults (lowest) < voyage `selectedPlaybooks` < per-mission `selectedPlaybooks` (highest). A duplicate `playbookId` is never rendered twice -- the most-specific `deliveryMode` wins on collision, and non-colliding entries append. Place shared guidance at vessel or voyage level to avoid duplicating payload across missions; use per-mission `selectedPlaybooks` only for true mission-specific extras or delivery-mode overrides.
- **Voyage->mission propagation.** Voyage-level `selectedPlaybooks` plus per-mission `selectedPlaybooks` merge before persisting the per-mission snapshot. Standard, pipeline-expansion, and alias-aware multi-stage dispatch paths all merge and snapshot consistently -- including downstream pipeline stages.
- **Three delivery modes.** `InlineFullContent` (template substitution), `InstructionWithReference` (path reference), `AttachIntoWorktree` (file copy into dock).
- **Materialization snapshot.** Mission persists a frozen `PlaybookSnapshots` copy at dispatch so mid-flight playbook edits don't change what the captain sees.
- **Code-index context packs.** Use `armada_context_pack` to inject targeted repository discovery snippets (symbol locations, file excerpts) as a `prestagedFiles` entry alongside the captain brief. Context packs are a focused supplement to existing playbook guidance, not a replacement for it.

### Reflections (Memory Consolidation Pipeline)

- **Memory consolidation and learned playbooks.** Per-vessel thresholds can auto-dispatch a `MemoryConsolidator` captain to review completed-mission evidence and produce capsule proposals, learned playbooks, reorganized memory, context-pack hints, persona/captain identity memory, and fleet-wide suggestions. Operators can run reflection modes manually with `armada_consolidate_memory`, gate reorganizations with dual-Judge validation, and accept or reject proposals through MCP. Schemas v40-v45.
- **MCP tools.** `armada_consolidate_memory` (with mode, vesselId, dualJudge, tokenBudget params), `armada_accept_memory_proposal`, `armada_reject_memory_proposal`, `armada_get_reflection_status`.
- **Reflection modes.** `reflect` (default — full review), `reorganize` (compact, diff-focused), `pack-curate` (pack pattern consolidation), `persona-curate` (persona behavior learning), `captain-curate` (captain-specific learning), `fleet-curate` (fleet-wide pattern learning), `hybrid` (combined). Mode encoded as wire string in `armada_consolidate_memory` and parsed on the server.
- **Pipeline reflections.** Three reflection pipelines auto-registered: `Reflect`, `Reflect-DualJudge` (same pipeline with a second Judge stage for gate validation), and `Reflect-Reorganize` (compact reorganize-only). Pipeline names and stage order are configurable.

### Multi-Tenancy & Auth

- **Tenants, users, credentials.** Every operational entity (fleet, vessel, captain, voyage, mission, dock, signal, event, merge entry) carries `TenantId` + `UserId`. `BypassTenantFilter` discipline on cross-tenant queries documented in code.
- **Bearer-token auth on REST + MCP.** Per-credential token; protected resources flag (`is_protected`) keeps system tenant/user/credential undeletable.
- **Tenant-scoped enumeration.** All list APIs accept tenant scope; admin context bypasses for system operations.
- **Self-registration toggle.** `AllowSelfRegistration` setting; `RequireAuthForShutdown` gates the stop endpoint.

### Persistence

- **Four database backends.** SQLite (default, embedded), PostgreSQL, MySQL, SQL Server. Same schema, same migration sequence (currently v47 for SQLite).
- **Numbered schema migrations.** Versioned `SchemaMigration(N, description, statements)` entries applied at admiral startup. Latest fork migrations: v37 planning sessions/transcripts/lineage, v38 merge failure classification and mission recovery attempts, v39 captain `runtime_options_json`, v40 vessel reflection tracking, v41 reorganize threshold, v42 parallel pipeline stages, v43 vessel pack hints / pack-curate threshold, v44 persona/captain identity-memory, v45 fleet-memory, v46 pipeline and mission review gates, v47 SQLite workflow/delivery/objective/request-history tables.
- **Backup / restore.** `armada_backup` and `armada_restore` MCP tools for full-database snapshots.
- **Bulk delete / purge.** `armada_delete_*` and `armada_purge_*` per entity for terminal-state cleanup.

### Captain Pool & Model Routing

- **Tier-upgrade fallback.** When all captains for a preferred tier are busy, dispatch upgrades to the next-higher tier (Quick → Mid → High); never downgrades.
- **Model-tier discipline.** Quick (≤30 LOC, zero judgement), Mid (≤200 LOC, established pattern), High (architectural, security primitives, novel protocol). Model peers within a tier with no vendor bias.
- **Persona ↔ captain hard filter.** Documented above. Judge restricted to Opus by policy; orchestrator decides per dispatch.

### Observability

- **REST API.** SwiftStack-based; OpenAPI spec available; covers fleets, vessels, captains, voyages, missions, docks, signals, events, merge queue, playbooks, personas, pipelines, prompt templates, workspace, request history, workflow profiles, check runs, objectives/backlog, releases, environments, deployments, incidents, runbooks, audit, backup, and status.
- **MCP server.** Voltaic-based standards-compliant MCP server on port 7891. Tools span orchestration, enumeration, playbooks, personas, pipelines, prompt templates, code-index context workflows, backlog/objectives, checks, releases, deployments, runbooks, reflections, audit, and server control.
- **WebSocket hub.** Real-time captain/mission state changes broadcast at `/ws`.
- **Embedded dashboard.** Legacy dashboard served from admiral; standalone React dashboard for production.
- **Mission logs.** Stdout/stderr capture per mission at `~/.armada/logs/missions/<missionId>.log`. Read via `/api/v1/missions/{id}/log` or `armada_get_mission_log`.
- **Captain logs.** Per-captain `.current` pointer at `~/.armada/logs/captains/<captainId>.current` always points at the active mission log.
- **Diff snapshots.** Mission diffs persisted on `WorkProduced` so they survive worktree reclamation. `armada_get_mission_diff`.
- **AgentOutput capture.** Final agent stdout captured into `Mission.AgentOutput` for audit + Judge verdict parsing. Runtime stdout/stderr buffers are capped, planning-session live output is tail-bounded, and REST/MCP list/status/detail surfaces use lightweight projections or strip persisted output so large agent output does not make normal dashboard/status/orchestrator refreshes hydrate heavy mission payloads. Use dedicated log/diff tools for large text.
- **Captain diagnostics.** `armada_captain_diagnostics` reports captain state, active mission elapsed time, dock git status, uncommitted files, log hints, and code-index freshness for investigating long-running captains or no-modified-file missions.
- **Captain tool catalog.** `GET /api/v1/captains/{id}/tools` and the dashboard captain tool viewer show the effective Armada MCP tool catalog available to a selected captain.
- **Structured logging.** SyslogLogging-based; placeholder-style throughout (`{DeviceId}`, `{StatusCode}`, `{ElapsedMs}`).
- **Events table.** Every state transition + lifecycle hook fires an event with entity refs and JSON payload; queryable via `armada_enumerate events`.

### Operational Tools

- **Backup / restore.** Full-database snapshot + restore round-trip.
- **Stop server.** Graceful shutdown via MCP `armada_stop_server` or REST.
- **Retry landing.** `armada_retry_landing` re-runs the landing handler for a mission stuck in `LandingFailed`.
- **Restart mission.** `armada_restart_mission` clones the mission descriptor and re-dispatches.
- **Audit drain queue.** `armada_drain_audit_queue` returns picked entries with notes; `armada_record_audit_verdict` records the deep-review outcome.
- **Remote tunnel.** Optional capability manifest + heartbeat for federated multi-admiral deployments (currently disabled by default).

### Vessel Safety

- **`ProtectedPaths` on vessels.** Reject captain commits touching listed paths with a coaching message. Default for managed deployments: `["**/CLAUDE.md"]`. Captain proposals surface in `mission.AgentOutput` for orchestrator review.
- **Working-directory isolation.** Each captain works in its own dock (git worktree); the main working tree stays clean until merge. Branches scoped under `armada/<captain-name>/<mission-id>`.

### Internationalization

- **Live language selection.** Login, shared shell UI, list/detail/admin routes, setup flows, notifications, pagination, server management, embedded legacy dashboard surfaces all support locale switching with locale-aware formatting.
- **i18n bundles.** JSON catalogues under `src/Armada.Server/wwwroot/i18n/` and the React dashboard.

### CLI

- **`armada` global tool** ([Spectre.Console](https://spectreconsole.net/)). Rich terminal UI for dispatch, status, watch, retry, captain management, signal management, voyage detail, mission log tail. CLI is a thin client over the REST API.

---

## How It Works

<table align="center">
<tr><td>
<pre>
+-----------------------------------------------------------+     +-----------------------------------------------------------+
| Direct Dispatch                                           |     | Planning In The Dashboard                                 |
| CLI / API / MCP sends work immediately                    |     | Chat with a captain inside the UI on a reserved dock      |
+-----------------------------------------------------------+     +-----------------------------------------------------------+
                              |                                               |
                              |                                               v
                              |                          +-----------------------------------------------------------+
                              |                          | Select a planning reply                                    |
                              |                          | Summarize it, open it in Dispatch, or dispatch directly   |
                              |                          +-----------------------------------------------------------+
                              |                                               |
                              +-------------------------------+---------------+
                                                              |
                                                              v
                         +-----------------------------------------------------------+
                         | Admiral                                                   |
                         | Coordinates work, resolves pipeline, assigns captains,    |
                         | provisions worktrees, and tracks mission state            |
                         +-----------------------------------------------------------+
                                                              |
                                                              v
                         +-----------------------------------------------------------+
                         | Architect                                                 |
                         | Reads the codebase, breaks work into missions, and        |
                         | identifies file boundaries                                |
                         +-----------------------------------------------------------+
                                                              |
                                                              v
                         +-----------------------------------------------------------+
                         | Worker                                                    |
                         | Implements the mission in an isolated git worktree        |
                         | and produces a diff                                       |
                         +-----------------------------------------------------------+
                                                              |
                                                              v
                         +-----------------------------------------------------------+
                         | TestEngineer                                              |
                         | Reviews the worker diff and adds or updates tests         |
                         +-----------------------------------------------------------+
                                                              |
                                                              v
                         +-----------------------------------------------------------+
                         | Judge                                                     |
                         | Reviews correctness, completeness, scope, and style       |
                         | Produces PASS or FAIL                                     |
                         +-----------------------------------------------------------+
</pre>
</td></tr>
</table>

1. **You choose the entry point.** Dispatch directly from the CLI/API/MCP, or start a planning session in the dashboard and chat with a captain first.
2. **Planning can hand off directly to execution.** From the planning UI, select an assistant reply and either summarize it into a dispatch draft, open it in the main Dispatch page, or dispatch it directly without copy/paste.
3. **The Admiral coordinates execution.** It resolves the pipeline, assigns captains, provisions worktrees, and tracks mission state.
4. **The Architect plans.** It reads the codebase, breaks the work into missions, and identifies likely file boundaries.
5. **Workers implement.** Each worker runs in its own git worktree on its own branch.
6. **TestEngineers add tests.** They get the worker diff as input.
7. **Judges review.** They check the result against the original task and return a pass/fail verdict.

Each step is a **persona** with its own prompt template. A sequence of personas is a **pipeline**. The built-ins are just defaults; pipelines are user-configurable and can be extended with whatever personas your project needs:

| Pipeline | Stages | When to use |
|----------|--------|------------|
| **WorkerOnly** | Implement | Quick fixes, one-liners |
| **Reviewed** | Implement -> Review | Normal development |
| **Tested** | Implement -> Test -> Review | When you need coverage |
| **FullPipeline** | Plan -> Implement -> Test -> Review | Big features, unfamiliar codebases |
| **DiagnosticProtocolTested** | Implement -> Diagnostic protocol review -> Test -> Review | J1939, UDS, J1708, K-line, seed-key/security access, diagnostic timing/framing, and banned reflash boundary checks |
| **TenantSecurityTested** | Implement -> Tenant security review -> Test -> Review | Multi-tenant authz/authn, tenant isolation, secrets, auditability, and cross-tenant leak risk |
| **MigrationDataTested** | Implement -> Migration/data review -> Test -> Review | Migrations, schema/provider parity, indexes, backfills, rollback/restart safety, and data-loss risk |
| **PerformanceMemoryTested** | Implement -> Performance/memory review -> Test -> Review | Memory/allocations, retained object graphs, output/log growth, DB materialization, throughput, and resource lifetime |
| **ReferencePortingTested** | Implement -> Reference parity analysis -> Test -> Review | Approved reference material, decompiler-derived notes, vendor traces, protocol captures, and semantic parity evidence for porting work |
| **FrontendWorkflowTested** | Implement -> Frontend workflow review -> Test -> Review | Frontend UX/workflow, accessibility, responsive states, i18n, errors, and design consistency |

You can set a default pipeline per repository and override it on a single dispatch when needed. Use the specialist tested pipelines when the mission has one of those known risk profiles; if the built-in roles are not enough, define your own personas and compose them into custom pipelines for additional project-specific steps.

### Parallel Tasks

Semicolons or numbered lists split a prompt into separate missions. Armada can assign those to different agents:

```bash
armada go "Add rate limiting; Add request logging; Add input validation"

armada go "1. Add auth middleware 2. Add login endpoint 3. Add token validation"
```

### Auto-Recovery

If a captain crashes, the Admiral can repair the worktree and relaunch the agent up to `MaxRecoveryAttempts` times (default: 3).

## Quick Start

### Prerequisites

- [.NET 8.0+ SDK](https://dot.net/download)
- At least one AI agent runtime on your PATH:
  - [Claude Code](https://docs.anthropic.com/en/docs/claude-code) (`claude`)
  - [Codex](https://github.com/openai/codex) (`codex`)
  - [Gemini CLI](https://github.com/google-gemini/gemini-cli) (`gemini`)
  - [Cursor](https://docs.cursor.com/cli) (`cursor-agent`)
  - Mux (`mux`)

### Install

```bash
git clone https://github.com/jchristn/armada.git
cd armada
```

Linux: `./scripts/linux/install.sh`

macOS: `./scripts/macos/install.sh`

Windows: `scripts\windows\install.bat`

Windows can also override the target framework when only one SDK is available, for example `scripts\windows\install.bat net8.0` or `scripts\windows\install.bat --framework net10.0`. The same override works for `reinstall.bat`, `remove.bat`, `update.bat`, `install-mcp.bat`, `remove-mcp.bat`, `publish-server.bat`, `install-windows-task.bat`, and `update-windows-task.bat`. The default remains `net10.0`.

Examples:

- `scripts\windows\publish-server.bat net8.0`
- `scripts\windows\install-windows-task.bat net8.0`
- `scripts\windows\update-windows-task.bat --framework net8.0`

These `install.*` scripts build the solution, deploy dashboard assets, and install `Armada.Helm` as a global tool from the current checkout.

Platform entrypoints are split under `scripts/windows/`, `scripts/linux/`, and `scripts/macos/`. Shared shell implementations live under `scripts/common/`.

If you want Armada deployed and managed on your local machine from source, use the deployment scripts below:

| Task | Linux | macOS | Windows |
|------|-------|-------|---------|
| Publish server and dashboard only | `./scripts/linux/publish-server.sh` | `./scripts/macos/publish-server.sh` | `scripts\windows\publish-server.bat` |
| Install and register a user-scoped local deployment | `./scripts/linux/install-systemd-user.sh` | `./scripts/macos/install-launchd-agent.sh` | `scripts\windows\install-windows-task.bat` |
| Update the deployed server from the current checkout | `./scripts/linux/update-systemd-user.sh` | `./scripts/macos/update-launchd-agent.sh` | `scripts\windows\update-windows-task.bat` |
| Verify the running deployment | `./scripts/linux/healthcheck-server.sh` | `./scripts/macos/healthcheck-server.sh` | `scripts\windows\healthcheck-server.bat` |
| Remove the startup-managed deployment | `./scripts/linux/remove-systemd-user.sh` | `./scripts/macos/remove-launchd-agent.sh` | `scripts\windows\remove-windows-task.bat` |

These deployment scripts publish `Armada.Server` into `~/.armada/bin` on Linux and macOS, or `%USERPROFILE%\.armada\bin` on Windows, and deploy dashboard assets into `~/.armada/dashboard` or `%USERPROFILE%\.armada\dashboard`.

The remove scripts unregister the background startup entry or user service, but they do not delete the published files under `~/.armada` or `%USERPROFILE%\.armada`.

Repo-relative deployment script paths:

- Linux: `scripts/linux/install-systemd-user.sh`, `scripts/linux/update-systemd-user.sh`, `scripts/linux/healthcheck-server.sh`
- macOS: `scripts/macos/install-launchd-agent.sh`, `scripts/macos/update-launchd-agent.sh`, `scripts/macos/healthcheck-server.sh`
- Windows: `scripts/windows/install-windows-task.bat`, `scripts/windows/update-windows-task.bat`, `scripts/windows/healthcheck-server.bat`

For background startup details, see [docs/RUN_ON_STARTUP.md](docs/RUN_ON_STARTUP.md).

### Your First Dispatch

```bash
cd your-project
armada go "Add input validation to the signup form"
armada watch   # monitor progress in real time
```

Armada detects the runtime, infers the current repository, provisions a worktree, and dispatches the task.

### Planning Before Dispatch

If you want to negotiate a plan with a captain before launching work, use the dashboard planning flow:

1. Open `http://localhost:7890/dashboard`
2. Go to `Planning`
3. Choose a captain, vessel, optional pipeline, and any playbooks
4. Chat with the captain until the plan is ready
5. Select the assistant output you want, then either summarize it into a cleaner draft, open it in the main Dispatch page, or dispatch directly from the same screen
6. Delete the session when you no longer need the transcript, or let Armada clean it up through retention settings

Current planning-session behavior:

- Planning currently supports the built-in `ClaudeCode`, `Codex`, `Gemini`, `Cursor`, and `Mux` runtimes. `Custom` captains are not yet supported there.
- Planning sessions reserve the selected captain and a dock/worktree for the selected vessel while the session is active.
- The captain can inspect and modify the repository while planning. Treat the planning session as tool-capable, not read-only.
- Planning is transcript-backed today: each turn relaunches the runtime against the preserved transcript and repo context rather than holding a persistent stdin session open.
- Planning turn output shown live in the dashboard is capped to a retained tail. Full logs stay in the planning-session log file; the live transcript should not be used as an unbounded storage channel.
- Planning-session persistence is implemented for SQLite first. Other database backends currently reject planning-session operations with an explicit `501 Not Supported`.
- Armada can summarize a selected planning reply into a server-owned dispatch draft before you launch the voyage.
- You can open the current planning draft in the main `Dispatch` page without copy/paste.
- Optional cleanup controls are available through `PlanningSessionInactivityTimeoutMinutes` and `PlanningSessionRetentionDays`.

### Default Credentials

On first boot, Armada seeds a default tenant, user, and credential:

| Item | Value |
|------|-------|
| Email | `admin@armada` |
| Password | `password` |
| Bearer Token | `default` |

Dashboard at `http://localhost:7890/dashboard`. API access with `Authorization: Bearer default`.

The dashboard supports language selection from the login screen and keeps the chosen locale for the authenticated session.

> **Security:** Armada runs agents with auto-approve flags by default (Claude Code: `--dangerously-skip-permissions`, Codex: `--full-auto`, Gemini: `--approval-mode yolo`, Mux: `--yolo`). Agents can read, write, and execute in their worktrees without confirmation. Review the [configuration](#configuration) section before running in sensitive environments.

> **Important:** Change the default password in production environments.

For a deeper walkthrough, see the [Getting Started Guide](GETTING_STARTED.md).

## Pipelines

Pipelines are the workflow layer in Armada. They let you run work through explicit stages instead of treating every task as a single agent session.

### Built-in Personas

| Persona | Role | What it does |
|---------|------|-------------|
| **Architect** | Plan | Reads the codebase, decomposes a high-level goal into concrete missions with file lists and dependency ordering |
| **Worker** | Implement | Writes code. The default -- this is what you get without pipelines. |
| **TestEngineer** | Test | Receives the Worker's diff, identifies gaps in coverage, writes tests |
| **Judge** | Review | Examines the diff against the original mission description. Checks completeness, correctness, scope violations, style. Produces a verdict. |

### Pipeline Resolution

When you dispatch, Armada picks the pipeline in this order:

| Priority | Source | How to set |
|----------|--------|-----------|
| 1 (highest) | Dispatch parameter | `--pipeline FullPipeline` on the CLI or `pipeline` in the API |
| 2 | Vessel default | Set on the repository in the dashboard or via API |
| 3 | Fleet default | Set on the fleet -- applies to all repos in the fleet unless overridden |
| 4 (lowest) | System fallback | WorkerOnly |

### Custom Personas and Pipelines

The built-in personas are starting points. You can create your own:

```bash
# Create a security auditor persona with custom instructions
armada_update_prompt_template name=persona.security_auditor content="Review for OWASP vulnerabilities..."
armada_create_persona name=SecurityAuditor promptTemplateName=persona.security_auditor

# Build a pipeline that includes security review
armada_create_pipeline name=SecureRelease stages='[{"personaName":"Worker"},{"personaName":"SecurityAuditor"},{"personaName":"Judge"}]'
```

Every prompt Armada sends is backed by an editable template. You can change agent behavior without modifying code. The dashboard includes a template editor with a parameter reference panel.

Pipelines are not limited to planning, implementation, testing, and review. Armada ships specialist tested pipelines for diagnostic protocol, tenant security, migration/data, performance/memory, reference porting, and frontend workflow review. If a project needs a SecurityAuditor, DocsWriter, ReleaseManager, or another internal role with custom instructions and handoff rules, Armada can support that by adding the persona and inserting it into the pipeline.

For the full pipeline reference, see [docs/PIPELINES.md](docs/PIPELINES.md).

## Playbooks

Playbooks are tenant-scoped markdown instruction documents that you can manage in the dashboard and attach to work at dispatch time.

- Create, edit, delete, and browse playbooks from the `Playbooks` area in the dashboard.
- Select any number of playbooks when creating a voyage or standalone mission.
- Choose delivery per selection: `InlineFullContent`, `InstructionWithReference`, or `AttachIntoWorktree`.
- Armada snapshots the exact playbook content, filename, order, and resolved delivery mode used for a mission so later edits do not change historical execution context.
- REST, MCP, proxy remote-management, dashboard, CLI, SDK, and Postman surfaces all use the same playbook selection model.
- File-based delivery resolves readable playbook files for the agent without polluting repository history, while inline delivery embeds the full markdown body directly into the rendered instruction set.

This is useful for architecture rules, coding standards, migration checklists, release procedures, security review requirements, or any other reusable instruction set that should travel with the work.

### Playbook Merge Hierarchy and Duplicate Prevention

Playbook selections merge in strict priority order: vessel defaults (lowest) < voyage-level `selectedPlaybooks` < per-mission `selectedPlaybooks` (highest). A `playbookId` that appears at multiple levels is rendered exactly once -- the most-specific `deliveryMode` wins on collision, and non-colliding entries from lower levels append. This means:

- **Vessel defaults** supply baseline guidance that applies to every mission on that vessel.
- **Voyage-level `selectedPlaybooks`** add or override guidance for all missions in that voyage without repeating it per mission. This is the recommended way to share extra context across a multi-mission voyage.
- **Per-mission `selectedPlaybooks`** are for true mission-specific extras or `deliveryMode` overrides. If the same `playbookId` appears at voyage level and per-mission level, the per-mission `deliveryMode` wins but the content is not duplicated.

To preserve captain instruction quality, avoid duplicating the same playbook at both voyage and per-mission level just to change the delivery mode -- instead, set it once at voyage level and override only the mode per-mission. Do not globally downgrade `InlineFullContent` to a lighter delivery mode as a blanket token-reduction strategy; use lighter modes only when you can confirm the captain still receives and follows the full required guidance.

For repository discovery context, use `armada_context_pack` to generate targeted snippets (symbol locations, relevant file excerpts). Pass the returned `prestagedFiles` entry into your dispatch to supplement existing playbook guidance without replacing it.

## Internationalization

The dashboard supports live language selection and locale-aware formatting across both the React shell and the legacy embedded surfaces.

- Supported locales: English, Spanish, Mandarin (Simplified), Mandarin (Traditional), Cantonese, Japanese, German, French, and Italian.
- Language selection is available from login, setup, and the authenticated shell, and the active locale persists between sessions.
- Shared UI elements such as notifications, pagination, dialogs, labels, date/time formatting, and numeric formatting honor the selected locale.
- Route-level coverage includes list pages, detail pages, admin screens, setup flows, and server-management views so common actions do not fall back to English unexpectedly.
- Legacy dashboard confirms, alerts, toasts, and static shell copy are routed through the same runtime so mixed old/new surfaces stay consistent.

## Use Cases

### Solo Developer Multiplier

If a feature depends on a few independent refactors, you can dispatch them together instead of working through them serially:

```bash
armada go "1. Extract UserRepository from UserService 2. Add ILogger to all controllers 3. Migrate config to Options pattern"
```

That gives you three parallel branches to review instead of one long queue.

### Ship with Confidence

Set `Tested` as the default pipeline if you want implementation, test generation, and review on every dispatch.
Set a specialist tested pipeline as the default for repositories where that domain risk is routine, or choose it per dispatch when a single mission touches that area.

### Code Review Prep

Batch mechanical cleanup before opening a review:

```bash
armada voyage create "Pre-review cleanup" --vessel my-api \
  --mission "Add XML documentation to all public methods in Controllers/" \
  --mission "Replace magic strings with constants in Services/" \
  --mission "Add input validation to all POST endpoints"
```

### Multi-Repo Coordination

Dispatch related changes across multiple repositories:

```bash
armada go "Update the shared DTOs to include CreatedAt field" --vessel shared-models
armada go "Add CreatedAt to the API response serialization" --vessel backend-api
armada go "Display CreatedAt in the user profile component" --vessel frontend-app
```

### Prototyping and Exploration

Try a few approaches in parallel:

```bash
armada voyage create "Auth approach comparison" --vessel my-api \
  --mission "Implement JWT-based authentication with refresh tokens" \
  --mission "Implement session-based authentication with Redis store" \
  --mission "Implement OAuth2 with Google and GitHub providers"
```

Review the branches, keep one, and drop the others.

### Bug Triage

Spread investigation and fixes across multiple reported issues:

```bash
armada go "Fix: login fails when email contains a plus sign" --vessel auth-service
armada go "Fix: pagination returns duplicate results on page 2" --vessel search-api
armada go "Fix: file upload silently fails for files over 10MB" --vessel upload-service
```

### Let AI Manage AI

If you connect Claude Code to Armada's MCP server, Claude can act as the orchestrator: decompose work into missions, dispatch them, and monitor progress.

```
> "Refactor the authentication system. Decompose it into parallel missions
   and dispatch them via Armada. Monitor progress and redispatch failures."
```

See [Claude Code as Orchestrator](docs/CLAUDE_CODE_AS_ORCHESTRATOR.md) for setup.

## Screenshots

<details>
<summary>Click to expand</summary>

<br />

![Screenshot 1](assets/screenshot-1.png)

![Screenshot 2](assets/screenshot-2.png)

![Screenshot 3](assets/screenshot-3.png)

![Screenshot 4](assets/screenshot-4.png)

</details>

## Architecture

Armada is a C#/.NET solution with five main projects:

| Project | Description |
|---------|-------------|
| **Armada.Core** | Domain models (including tenants, users, credentials), database interfaces, service interfaces, settings |
| **Armada.Runtimes** | Agent runtime adapters (Claude Code, Codex, Gemini, Cursor, Mux, extensible via `IAgentRuntime`) |
| **Armada.Server** | Admiral process: REST API ([SwiftStack](https://github.com/jchristn/swiftstack)), MCP server ([Voltaic](https://github.com/jchristn/voltaic)), WebSocket hub, embedded dashboard |
| **Armada.Dashboard** | Standalone React dashboard for Docker/production deployments |
| **Armada.Helm** | CLI ([Spectre.Console](https://spectreconsole.net/)), thin HTTP client to Admiral |

### Key Concepts

| Term | Plain Language | Description |
|------|---------------|-------------|
| **Admiral** | Coordinator | The server process that manages everything. Auto-starts when needed. |
| **Captain** | Agent/worker | An AI agent instance (Claude Code, Codex, Gemini, Cursor, Mux, etc.). Auto-created on demand. |
| **Fleet** | Group of repos | Collection of repositories. A default fleet is auto-created. |
| **Vessel** | Repository | A git repository registered with Armada. Auto-registered from your current directory. |
| **Mission** | Task | An atomic work unit assigned to a captain. |
| **Voyage** | Batch | A group of related missions dispatched together. |
| **Planning Session** | Interactive draft | A dashboard chat session with a captain on a reserved dock/worktree. You can turn a selected reply into a dispatch draft or dispatch directly from the session. |
| **Dock** | Worktree | A git worktree provisioned for a captain's isolated work. |
| **Signal** | Message | Communication between the Admiral and captains. |
| **Persona** | Agent role | A named agent role (Worker, Architect, Judge, TestEngineer, or a specialist reviewer) that determines what a captain does during a mission. Users can create custom personas with custom prompt templates. |
| **Pipeline** | Workflow | An ordered sequence of persona stages (e.g. Architect -> Worker -> TestEngineer -> Judge). Configured at fleet/vessel level with per-dispatch override. |
| **Prompt Template** | Instructions | A user-editable template controlling the instructions given to agents. Every prompt in the system is template-driven with `{Placeholder}` parameters. |

For details on mission scheduling and assignment, see [docs/SCHEDULING.md](docs/SCHEDULING.md).

### Data Model

<table align="center">
<tr><td>
<pre>
+-------------------------------------------------------------+
|                            Admiral                            |
|                     (coordinator process)                     |
+--------+--------------+--------------+--------------+---------+
         |              |              |              |
         v              v              v              v
    +---------+   +----------+  +----------+   +----------+
    |  Fleet  |   | Captain  |  |  Voyage  |   |  Signal  |
    | (flt_)  |   |  (cpt_)  |  |  (vyg_)  |   |  (sig_)  |
    |         |   |          |  |          |   |          |
    | group   |   | AI agent |  | batch of |   | message  |
    | of repos|   | worker   |  | missions |   | between  |
    +----+----+   +----+-----+  +----+-----+   | admiral  |
         |             |             |         | & agents |
         v             |             v         +----------+
    +----------+       |       +----------+
    | Vessel   |<------+-------| Mission  |
    | (vsl_)   |       |       |  (msn_)  |
    |          |       |       |          |
    | git repo |       +------>| one task |
    +----+-----+       assigns | for one  |
         |             captain | agent    |
         v                     +----------+
    +----------+
    |   Dock   |
    |  (dck_)  |
    |          |
    |   git    |
    | worktree |
    +----------+

    Relationships:
    Fleet  1--*  Vessel       A fleet contains many vessels (repos)
    Vessel 1--*  Dock         A vessel has many docks (worktrees)
    Voyage 1--*  Mission      A voyage groups many missions
    Mission *--1 Vessel       Each mission targets one vessel
    Mission *--1 Captain      Each mission is assigned to one captain
    Captain 1--1 Dock         A captain works in one dock at a time
</pre>
</td></tr>
</table>

### Data Flow

<table align="center">
<tr><td>
<pre>
Direct Dispatch (CLI / API / MCP)                Dashboard Planning UI
                |                                           |
                |                                           +--> Start planning session
                |                                           +--> Reserve captain + dock
                |                                           +--> Chat with captain in the UI
                |                                           +--> Select reply for handoff
                |                                           +--> Summarize, open in Dispatch,
                |                                           |    or dispatch directly
                |                                           v
                +---------------------------+---------------+
                                            |
                                            v
                               Admiral receives dispatch
                                            |
                                            +--> Creates/updates Mission in database
                                            +--> Resolves target Vessel (repository)
                                            +--> Allocates Captain (find idle or spawn new)
                                            +--> Provisions worktree (git worktree add)
                                            +--> Starts agent process with mission context
                                            +--> Monitors via stdout/stderr + heartbeat
                                            |
                               Captain works autonomously
                                            |
                                            +--> Reports progress via signals
                                            +--> Admiral updates Mission status
                                            +--> On completion: push branch, create PR (optional)
                                            +--> Captain returns to idle pool
</pre>
</td></tr>
</table>

### Technology Stack

| Component | Technology | Notes |
|-----------|-----------|-------|
| Language | C# / .NET 8+ | Cross-platform |
| Database | SQLite, PostgreSQL, SQL Server, MySQL | SQLite default; zero-install, embedded |
| REST API | [SwiftStack](https://github.com/jchristn/swiftstack) | OpenAPI built-in |
| MCP/JSON-RPC | [Voltaic](https://github.com/jchristn/voltaic) | Standards-compliant MCP server |
| CLI | [Spectre.Console](https://spectreconsole.net/) | Rich terminal UI |
| Logging | [SyslogLogging](https://github.com/jchristn/sysloglogging) | Structured logging |
| ID Generation | [PrettyId](https://github.com/jchristn/prettyid) | Prefixed IDs (flt_, vsl_, cpt_, msn_, etc.) |

## CLI Reference

### Common Commands

```
armada go <prompt>           Quick dispatch (infers repo from current directory)
armada status                Dashboard (scoped to current repo)
armada status --all          Global view across all repos
armada watch                 Live dashboard with notifications
armada log <captain>         Tail a specific agent's output
armada log <captain> -f      Follow mode (like tail -f)
armada doctor                System health check
```

### Missions and Voyages

```
armada mission list|create|show|cancel|retry
armada voyage list|create|show|cancel|retry
armada playbook list|add|show|remove
```

### Entity Management

All commands accept names or IDs:

```
armada vessel list|add|remove
armada captain list|add|stop|stop-all
armada fleet list|add|remove
```

### Infrastructure

```
armada server start|status|stop
armada config show|set|init
armada mcp install|remove|stdio
```

### Examples

```bash
# Dispatch a single task in your current repo
armada go "Fix the null reference in UserService.cs"

# Dispatch three tasks in parallel
armada go "Add rate limiting; Add request logging; Add input validation"

# Work with a specific repo
armada go "Fix the login bug" --vessel my-api

# Register additional repos
armada vessel add my-api https://github.com/you/my-api
armada vessel add my-frontend https://github.com/you/my-frontend

# Add more agents (supports claude, codex, gemini, cursor, mux)
armada captain add claude-2 --runtime claude
armada captain add codex-1 --runtime codex
armada captain add gemini-1 --runtime gemini
armada captain add mux-1 --runtime mux --mux-endpoint local-openai
armada captain update mux-1 --mux-config-dir C:\Users\you\.mux-work --mux-endpoint staging-openai

# Emergency stop all agents
armada captain stop-all

# Retry a failed mission
armada mission retry msn_abc123

# Retry all failed missions in a voyage
armada voyage retry "API Hardening"
```

Mux captains require a named endpoint. Armada stores that endpoint selection on the captain, validates it through `mux probe --require-tools`, and can optionally target a non-default Mux config directory via `--mux-config-dir`. The React dashboard and legacy dashboard can both browse saved endpoints through Armada's `/api/v1/runtimes/mux/endpoints` helper APIs.

## Configuration

Settings live in `~/.armada/settings.json` and are created on first use.

```bash
armada config show              # View current settings
armada config set MaxCaptains 8 # Change a setting
armada config init              # Interactive setup (optional)
```

| Setting | Default | Description |
|---------|---------|-------------|
| `AdmiralPort` | 7890 | REST API port |
| `MaxCaptains` | 0 (auto, defaults to 5) | Maximum total captains |
| `StallThresholdMinutes` | 10 | Minutes before a captain is considered stalled |
| `MaxRecoveryAttempts` | 3 | Auto-recovery attempts before giving up |
| `AutoPush` | true | Push branches to remote on mission completion |
| `AutoCreatePullRequests` | false | Create PRs on mission completion |
| `AutoMergePullRequests` | false | Auto-merge PRs after creation |
| `LandingMode` | null | Landing policy: `LocalMerge`, `PullRequest`, `MergeQueue`, or `None` |
| `BranchCleanupPolicy` | `LocalOnly` | Branch cleanup after landing: `LocalOnly`, `LocalAndRemote`, or `None` |
| `RequireAuthForShutdown` | false | Require authentication for `POST /api/v1/server/stop` |
| `TerminalBell` | true | Ring terminal bell during `armada watch` |
| `DefaultRuntime` | null (auto-detect) | Default agent runtime |
| `PlanningSessionInactivityTimeoutMinutes` | 0 | Automatically stop idle planning sessions after this many minutes; 0 disables the timeout |
| `PlanningSessionRetentionDays` | 0 | Automatically delete stopped or failed planning transcripts after this many days; 0 disables retention cleanup |

### Remote trigger / AgentWake

Remote triggers are disabled by default. Supported modes are `Disabled`, `RemoteFire`, and `AgentWake`. `LocalDaemon` is intentionally removed and is not supported.

`AgentWake` is the local, event-driven replacement for dormant Claude/Codex orchestrator sessions. It does not run a loop. Armada starts one Claude Code or Codex CLI process only after real work events, coalesces bursts per vessel, throttles repeated wakes with `remoteTrigger.throttleCapPerHour` (default 20), and uses a global single-flight lease so a burst cannot spawn many agents. Set `runtime` to `Auto` when you want Armada to pick whichever orchestrator was most recently registered.

AgentWake supports three delivery modes controlled by `agentWake.deliveryMode`:
- `SpawnProcess` (default): Spawns the agent CLI as a headless process that autonomously drives the orchestrator turn.
- `McpNotification`: Writes a `Wake` signal row instead of spawning a process. Interactive orchestrators drain wakes via `armada_enumerate entityType=signals signalType=Wake unreadOnly=true` and acknowledge with `armada_mark_signal_read`.
- `Both`: Spawns a process AND writes a Wake signal (belt-and-suspenders; may double-fire).

Example:

```json
{
  "remoteTrigger": {
    "enabled": true,
    "mode": "AgentWake",
    "throttleCapPerHour": 60,
    "agentWake": {
      "runtime": "Codex",
      "sessionId": null,
      "workingDirectory": "C:\\Users\\Owner\\RiderProjects\\project",
      "timeoutSeconds": 600
    }
  }
}
```

Auto example:

```json
{
  "remoteTrigger": {
    "enabled": true,
    "mode": "AgentWake",
    "throttleCapPerHour": 60,
    "agentWake": {
      "runtime": "Auto",
      "runtimePreference": [ "Codex", "Claude" ],
      "workingDirectory": "C:\\Users\\Owner\\RiderProjects\\project",
      "timeoutSeconds": 600
    }
  }
}
```

For Claude Code, set `runtime` to `Claude`; Armada uses `--print --continue` when `sessionId` is empty and `--print --resume <sessionId>` when one is configured. For Codex, Armada resumes the configured session when available or uses the last session path. With `runtime: "Auto"`, Armada first uses the most recent `armada_register_agentwake_session` registration, then tries `runtimePreference` in order. `RemoteFire` remains available for HTTP webhook style triggers.

## Authentication

As of v0.3.0, Armada supports multi-tenant authentication with three methods:

| Method | Header | Description |
|--------|--------|-------------|
| **Bearer Token** (recommended) | `Authorization: Bearer <token>` | 64-character tokens linked to a tenant and user. Default token: `default` |
| **Session Token** | `X-Token: <token>` | AES-256-CBC encrypted, 24-hour lifetime. Returned by `POST /api/v1/authenticate` |
| **API Key** (deprecated) | `X-Api-Key: <key>` | Legacy. Maps to a synthetic admin identity. Migrate to bearer tokens |

The default installation works with `Authorization: Bearer default`.

All operational data is tenant-scoped. The authorization model:

- `IsAdmin = true`: global system admin with access to every tenant and object.
- `IsAdmin = false`, `IsTenantAdmin = true`: tenant admin with management access inside that tenant, including users and credentials.
- `IsAdmin = false`, `IsTenantAdmin = false`: regular user with tenant-scoped visibility plus self-service on their own account and credentials.

For full details, see [docs/REST_API.md](docs/REST_API.md#authentication).

## REST API

The Admiral exposes a REST API on port 7890. Endpoints are under `/api/v1/` and require authentication unless noted otherwise. Error responses use a standard format with `Error`, `Description`, `Message`, and `Data` fields; see [REST_API.md](docs/REST_API.md#error-responses) for details.

```bash
API="http://localhost:7890/api/v1"
AUTH="Authorization: Bearer default"

curl -H "$AUTH" $API/status              # System status
curl -H "$AUTH" $API/fleets              # List fleets
curl -H "$AUTH" $API/vessels             # List vessels
curl -H "$AUTH" $API/missions            # List missions
curl -H "$AUTH" $API/captains            # List captains
curl $API/status/health                  # Health check (no auth required)
```

Full CRUD or workflow endpoints are available for fleets, vessels, missions, voyages, captains, signals, events, playbooks, prompt templates, personas, pipelines, tenants, users, credentials, workspace files, request history, workflow profiles, check runs, objectives/backlog, releases, environments, deployments, incidents, runbooks, and timeline history.

Start the Admiral as a standalone server:

```bash
armada server start
```

## MCP Integration

Armada also exposes an MCP (Model Context Protocol) server so Claude Code and other MCP-compatible clients can call Armada tools directly.

```bash
armada mcp install    # Configure Claude Code, Codex, Gemini, and Cursor for Armada MCP
armada mcp remove     # Remove those Armada MCP entries again
```

If you are working from source, MCP helper entrypoints are available under `scripts/windows/`, `scripts/linux/`, and `scripts/macos/`. Codex MCP setup uses the native `codex mcp add` command and writes the current Codex `config.toml` format. Source checkouts register a stdio command that runs Armada through `dotnet`, which avoids stale HTTP/client startup assumptions in new Codex sessions.

Once installed, your MCP client can call tools like `armada_status`, `armada_dispatch`, `armada_enumerate`, `armada_voyage_status`, and `armada_cancel_voyage`. There are also tool groups for playbook, persona, pipeline, prompt-template, code-index, backlog/objective, check-run, release, deployment, runbook, reflection, and audit management. Mission enumeration and status tools intentionally return lightweight summaries; use mission log/diff tools when you need large text. If AgentWake uses `runtime: "Auto"`, call `armada_register_agentwake_session` from the active Codex/Claude orchestrator session so future one-shot wakes resume that runtime/session. For repository discovery before dispatch, use `armada_context_pack` and pass its returned `prestagedFiles` entry into the mission.

### AI-Powered Orchestration

If you connect Claude Code, Codex, or another MCP-capable client to Armada, that client can act as the orchestrator. Armada handles the worktrees, state, and process management underneath.

```
Claude Code (orchestrator) --MCP--> Armada Server --spawns--> Captain agents (workers)
```

For detailed setup and examples, see:
- [Claude Code as Orchestrator](docs/CLAUDE_CODE_AS_ORCHESTRATOR.md)
- [Codex as Orchestrator](docs/CODEX_AS_ORCHESTRATOR.md)

## Running Locally (without Docker)

### Prerequisites

- [.NET 8.0+ SDK](https://dot.net/download)
- At least one AI agent runtime on your PATH (Claude Code, Codex, Gemini, or Cursor)

### Scripted Local Deployment

For a local machine deployment managed from this checkout, use the platform scripts shown in the Quick Start table above. The install scripts register a user-scoped startup entry or service, the update scripts republish from source and restart it, and the remove scripts unregister it again.

Use the health-check helper after install or update:

Linux: `./scripts/linux/healthcheck-server.sh`

macOS: `./scripts/macos/healthcheck-server.sh`

Windows: `scripts\windows\healthcheck-server.bat`

Repo-relative startup helpers: `scripts/linux/install-systemd-user.sh`, `scripts/linux/update-systemd-user.sh`, `scripts/linux/healthcheck-server.sh`, `scripts/macos/install-launchd-agent.sh`, `scripts/macos/update-launchd-agent.sh`, `scripts/macos/healthcheck-server.sh`, `scripts/windows/install-windows-task.bat`, `scripts/windows/update-windows-task.bat`, `scripts/windows/healthcheck-server.bat`.

### Foreground Development Run

```bash
git clone https://github.com/jchristn/armada.git
cd armada

# Build the solution
dotnet build src/Armada.sln

# Run the server directly for a foreground dev session
dotnet run --project src/Armada.Server
```

The server starts on the following ports:

| Port | Protocol | Description |
|------|----------|-------------|
| 7890 | HTTP | REST API + embedded dashboard (WebSocket at /ws) |
| 7891 | JSON-RPC | MCP server |

Open `http://localhost:7890/dashboard` in your browser. Configuration is stored in `armada.json` in the working directory. On first run, Armada creates the SQLite database, applies migrations, and seeds default data.

### Install the CLI (optional)

```bash
dotnet pack src/Armada.Helm -o ./nupkg
dotnet tool install --global --add-source ./nupkg Armada.Helm

# Then use the CLI from any directory
armada doctor
armada go "your task here"
```

### Run Tests

```bash
dotnet run --project test/Armada.Test.Unit
```

## Running Locally (with Docker)

Docker Compose can run the server and the optional React dashboard in containers, so the host does not need the .NET SDK.

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/) with Docker Compose v2

### Start

```bash
cd docker
docker compose up -d
```

### Services

| Service | Port | URL | Description |
|---------|------|-----|-------------|
| `armada-server` | 7890 | `http://localhost:7890/dashboard` | REST API, MCP, WebSocket, embedded dashboard |
| `armada-dashboard` | 3000 | `http://localhost:3000` | Standalone React dashboard |

Both dashboards connect to the same server. The embedded dashboard at port 7890 is always available. The React dashboard at port 3000 is an optional separate frontend.

### Data Persistence

Docker volumes are mapped to `docker/armada/`:

```
docker/
+-- armada/
|   +-- db/          # SQLite database (persistent across restarts)
|   +-- logs/        # Server logs
+-- server/
|   +-- armada.json  # Server configuration
+-- compose.yaml
```

To change settings, edit `docker/server/armada.json` and restart:

```bash
docker compose restart armada-server
```

### Factory Reset

To delete all data and start fresh (preserves configuration):

```bash
cd docker/factory

# Linux/macOS
./reset.sh

# Windows
reset.bat
```

### Stop

```bash
cd docker
docker compose down
```

### Build Images Locally

To build the Docker images from source instead of pulling from Docker Hub:

```bash
# Build server image
docker build -f src/Armada.Server/Dockerfile -t armada-server:local .

# Build dashboard image
docker build -f src/Armada.Dashboard/Dockerfile -t armada-dashboard:local .
```

Build scripts for multi-platform images are provided under `scripts/windows/`, `scripts/linux/`, and `scripts/macos/`.

## Upgrading / Migration

When upgrading between major versions, your `settings.json` may need to be updated.

### v0.1.0 to v0.2.0

**Breaking change:** The `settings.json` format changed. Armada v0.2.0 will fail to start with a v0.1.0 `settings.json`.

The `databasePath` string property was replaced with a `database` object supporting multiple backends (SQLite, PostgreSQL, SQL Server, MySQL).

#### Before (v0.1.0)

```json
{
  "databasePath": "armada.db",
  "admiralPort": 7890,
  "maxCaptains": 5
}
```

#### After (v0.2.0)

```json
{
  "database": {
    "type": "Sqlite",
    "filename": "armada.db"
  },
  "admiralPort": 7890,
  "maxCaptains": 5
}
```

#### Minimal change for SQLite users

Replace:

```json
"databasePath": "path/to/armada.db"
```

With:

```json
"database": {
  "type": "Sqlite",
  "filename": "path/to/armada.db"
}
```

No other changes are required -- all other settings remain the same.

#### Switching to PostgreSQL

```json
"database": {
  "type": "Postgresql",
  "hostname": "localhost",
  "port": 5432,
  "username": "armada",
  "password": "your-password",
  "databaseName": "armada",
  "schema": "public",
  "minPoolSize": 1,
  "maxPoolSize": 25,
  "connectionLifetimeSeconds": 300,
  "connectionIdleTimeoutSeconds": 60
}
```

#### Switching to SQL Server

```json
"database": {
  "type": "SqlServer",
  "hostname": "localhost",
  "port": 1433,
  "username": "armada",
  "password": "your-password",
  "databaseName": "armada",
  "minPoolSize": 1,
  "maxPoolSize": 25,
  "connectionLifetimeSeconds": 300,
  "connectionIdleTimeoutSeconds": 60
}
```

#### Switching to MySQL

```json
"database": {
  "type": "Mysql",
  "hostname": "localhost",
  "port": 3306,
  "username": "armada",
  "password": "your-password",
  "databaseName": "armada",
  "minPoolSize": 1,
  "maxPoolSize": 25,
  "connectionLifetimeSeconds": 300,
  "connectionIdleTimeoutSeconds": 60
}
```

#### Additional notes

- **Port auto-detection:** Setting `port` to `0` (or omitting it) auto-detects the default port for each database type (PostgreSQL: 5432, SQL Server: 1433, MySQL: 3306).
- **Connection pooling:** All non-SQLite backends support connection pooling via `minPoolSize` (0-100), `maxPoolSize` (1-200), `connectionLifetimeSeconds` (minimum 30), and `connectionIdleTimeoutSeconds` (minimum 10).
- **Encryption:** Set `requireEncryption` to `true` to require encrypted connections for PostgreSQL, SQL Server, or MySQL.
- **Backup/restore:** The `armada_backup` and `armada_restore` MCP tools are only available when using SQLite. If you switch to PostgreSQL, SQL Server, or MySQL, use your database's native backup tools instead.

#### Automated migration script

For existing v0.1.0 deployments, run the migration script to automatically convert your `settings.json`:

**Windows:**
```
migrations\migrate_v0.1.0_to_v0.2.0.bat
# or with a custom path:
migrations\migrate_v0.1.0_to_v0.2.0.bat C:\path\to\settings.json
```

**Linux/macOS:**
```
./migrations/migrate_v0.1.0_to_v0.2.0.sh
# or with a custom path:
./migrations/migrate_v0.1.0_to_v0.2.0.sh /path/to/settings.json
```

The script backs up your original file to `settings.json.v0.1.0.bak` before making changes.

**Requires:** jq (Linux/macOS) -- install via `apt install jq`, `brew install jq`, etc.

### v0.2.0 to v0.3.0

v0.3.0 introduces multi-tenant support. The database schema is automatically migrated on first startup. Key changes:

- **New tables:** `TenantMetadata`, `UserMaster`, `Credential` are created automatically
- **Default data seeded:** A default tenant (`default`), user (`admin@armada` / `password`), and credential (bearer token `default`) are created if no tenants exist
- **All operational tables gain `TenantId`:** Existing rows are assigned to the `default` tenant during migration
- **All operational tables gain `UserId`:** Existing rows are assigned to the earliest user in their tenant during migration
- **Ownership integrity:** Operational `TenantId` and `UserId` columns are indexed and protected by database foreign keys across all supported backends
- **Protected auth resources:** The default tenant, its default user/credential, and the synthetic system records are seeded as protected and cannot be deleted directly
- **Role model:** `IsAdmin` now means global system admin. `IsTenantAdmin` means tenant-scoped admin. Regular users are limited to their own tenant, own account, and own credentials
- **Password management:** User create/update APIs accept plaintext `Password`; the server hashes it before persistence. Leaving `Password` blank on update preserves the existing password. The dashboard exposes this through the Users edit modal for both admin-managed and self-service password changes
- **Protected resources:** `IsProtected` is server-controlled on tenants, users, and credentials. Protected objects cannot be deleted directly, and immutable identifiers/timestamps/ownership fields are preserved on update
- **Tenant-created seed admin:** Creating a tenant also creates `admin@armada` with password `password` plus a default credential inside that tenant; that seeded user is tenant admin only (`IsAdmin = false`, `IsTenantAdmin = true`) and those child resources are protected from direct delete
- **Authentication required:** All REST API endpoints now require authentication. Use `Authorization: Bearer default` for backward-compatible access
- **`X-Api-Key` deprecated:** The `X-Api-Key` header still works but is deprecated. If configured, it maps to a synthetic admin identity. Migrate to bearer tokens
- **New settings:** `AllowSelfRegistration` (default: `true`), `RequireAuthForShutdown` (default: `false`), `SessionTokenEncryptionKey` (auto-generated)

No manual changes to `settings.json` are required. Existing `ApiKey` settings continue to work.

### v0.3.0 to v0.4.0

v0.4.0 adds personas, pipelines, and prompt templates. The database schema is automatically migrated on first startup (migrations 19-23). Key changes:

- New tables: `prompt_templates`, `personas`, `pipelines`, `pipeline_stages`
- New columns: `captains.allowed_personas`, `captains.preferred_persona`, `missions.persona`, `missions.depends_on_mission_id`, `fleets.default_pipeline_id`, `vessels.default_pipeline_id`
- Built-in personas (Worker, Architect, Judge, TestEngineer, and the six specialist reviewers) and pipelines (WorkerOnly, Reviewed, Tested, FullPipeline, and the six specialist tested pipelines) are seeded automatically
- 24 built-in prompt templates are seeded automatically
- Standalone migration scripts available in `migrations/` for manual execution

### v0.4.0 to v0.5.0

v0.5.0 is focused on dispatch and pipeline stability. It adds captain model selection, startup model validation, mission runtime tracking, and a broad set of handoff, landing, cleanup, and workflow reliability improvements. The database schema is automatically migrated on first startup (migrations 24-27). Key changes:

- New columns: `captains.model`, `missions.total_runtime_ms`
- Captain model overrides are persisted across SQLite, MySQL, PostgreSQL, and SQL Server
- REST and MCP captain create/update operations validate configured models before saving
- React dashboard captain detail now exposes the captain model field and shows validation errors in a modal
- Mission detail now shows total runtime, and dispatch cleanup removes the redundant parsed-task UI
- Docker image tags, release metadata, and API documentation are updated for `v0.5.0`

### v0.6.0 to v0.7.0

v0.7.0 is focused on remote access. This release adds the local outbound tunnel client, the first shipped `Armada.Proxy` service, tunnel telemetry, server/dashboard configuration surfaces, and a bounded remote management shell for day-one operator workflows. No database schema migration is required for this release.

Key changes:

- New `RemoteControl` settings in `settings.json`, exposed through `GET /api/v1/settings` and `PUT /api/v1/settings`
- New `RemoteTunnel` health/status telemetry, exposed through `/api/v1/status`, `/api/v1/status/health`, the React dashboard, the legacy dashboard, and `armada status`
- Experimental outbound websocket tunnel client with URL normalization, handshake, heartbeat, reconnect, request/response handling, and event forwarding
- New `Armada.Proxy` service with websocket tunnel termination, a mobile-first remote operations shell, focused instance inspection APIs, live forwarded status/health/detail requests, and bounded remote management for fleets, vessels, voyages, missions, and captain stop
- The embedded server host now runs on Watson Webserver 7 for both HTTP and WebSocket traffic, replacing the standalone `WatsonWebsocket` dependency and fixing foreground startup handoff
- The dashboard setup wizard was rebuilt into a contained first-run workflow with direct dispatch, richer guidance, and improved server/settings ergonomics
- Dashboard internationalization now includes login language selection, persistent locale preference, route-level React coverage, legacy embedded dashboard coverage, and locale-aware date/time/number formatting
- New operator docs: `docs/REMOTE_MGMT.md`, `docs/TUNNEL_PROTOCOL.md`, `docs/PROXY_API.md`, and `docs/TUNNEL_OPERATIONS.md`
- Release metadata, Docker image tags, Postman examples, and API documentation are updated for `v0.7.0`
- Standalone no-op release scripts are available in `migrations/` for `v0.6.0 -> v0.7.0`

### v0.7.0 to v0.8.0

v0.8.0 is focused on backlog-first delivery management and operator evidence. It adds durable objectives/backlog, delivery workflow records, request history, Workspace, and pipeline review gates. The database schema is automatically migrated on first startup; standalone migration scripts are available in `migrations/` for all supported backends.

Key changes:

- New objective/backlog and objective-refinement tables normalize scope, priority, acceptance criteria, rollout constraints, source lineage, and planning/dispatch/delivery evidence.
- New workflow profile, check run, release, environment, deployment, incident, runbook, and request-history tables make delivery operations queryable instead of leaving them in logs or dashboard-only state.
- Pipeline stages and missions now support `requires_review`, `review_deny_action`, review comments, reviewer identity, and review timestamps. Review-gated missions pause in `Review` until approved or denied.
- Workspace, API Explorer, request history, objectives, workflow profiles, check runs, releases, environments, deployments, incidents, runbooks, and history are exposed through the React dashboard.
- Optional GitHub token settings connect GitHub issue/PR scope, GitHub Actions runs, PR review state, and check evidence back into Armada records.
- MCP operation tools now cover objectives/backlog, check runs, releases, deployments, runbooks, and runbook executions alongside the existing core `armada_enumerate` entity browser.

## Issues and Discussions

- **Bug reports and feature requests**: [Open an issue](https://github.com/jchristn/armada/issues) on GitHub. Please include your OS, .NET version, agent runtime, and steps to reproduce.
- **Questions and discussions**: [Start a discussion](https://github.com/jchristn/armada/discussions) on GitHub for general questions, ideas, or feedback.

When filing an issue, include:

1. What you expected to happen
2. What actually happened
3. Output of `armada doctor`
4. Relevant log output (`armada log <captain>`)

## License

Armada is released under the [MIT License](LICENSE.md). See the LICENSE.md file for details.
