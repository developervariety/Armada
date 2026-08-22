# Changelog

All notable changes to Armada are documented in this file.

---

## Unreleased

Focus: operator signal fidelity - make a failure say what actually failed.

### Definition-of-done gate
- A passing gate now also builds every vessel that declares the mission's vessel as a sibling repository, so a public-API break is caught while the producer's change is still unlanded instead of surfacing on whatever builds next. The consumer edge is derived from the existing `SiblingRepos` declarations read in reverse, so nothing new has to be configured
- Each consumer is provisioned under a scratch root private to that verification. A shared sibling checkout owned by another dock is reused rather than re-pointed, so verifying through one could compile the consumer against a different commit than the one being reported on
- Consumers are built, not tested: a build catches the break that leaves a target branch red, while running every consumer's suite inside every producer gate would cost more wall time than the gate itself
- A consumer that fails to compile fails the gate; a consumer that cannot be prepared is reported and the gate passes, since a missing profile or repository is a fault in the verification rather than evidence about the change. `DefinitionOfDone.FailOnConsumerVerificationError` reverses that, and `DefinitionOfDone.VerifyDeclaredConsumers` disables the step

### MCP
- `run_check`, `retry_check_run`, and `get_check_run` now return a bounded view of a check run - status, exit code, parsed test and coverage totals, artifacts, and the last 40 output lines - instead of the complete command log. A build or test log is routinely one to several megabytes, which overran the tool output limit and returned a truncation error in place of the verdict, forcing a parse step out of band on every call
- The complete log is still available deliberately: `get_check_run` takes `includeOutput=true` for the whole record, and `outputTailLines` to widen the tail. A truncated view reports the full log's size and names the call that fetches it, so nothing is silently withheld
- The tail is whole lines taken from the END of the log, which is where a failure's cause almost always is

### Dispatch
- Dispatch now arms the new voyage's Build and UnitTest Checks itself, so a voyage no longer reaches its Judge stage carrying none. A Judge PASS is rejected without a green independent Check, so a bare voyage was already condemned when it started and nothing said so until the whole pipeline had run. Cancelling a voyage discards its Checks, so a re-dispatch previously started bare again
- Armed Checks are created `Pending`, not executed: a Pending Check attached to a voyage is run in place at the Judge stage, so arming costs nothing at dispatch instead of loading the host as the first captain starts
- A type is armed only when the resolved workflow profile defines its command, and never twice for one voyage - a second Build beside a failed one would leave a green and a red attached, and one failed Check rejects a PASS however many green ones sit beside it
- Arming failures are logged and never fail the dispatch; `VoyageCheckArming.Enabled`, `ArmBuild`, and `ArmUnitTest` control the behavior

### Checks
- A Judge PASS rejected by the real-signal gate now NAMES the Checks that blocked it (id, type and label) in the mission's `FailureReason`, and states that every failed Check must be resolved. The message previously named only the rule, so an operator could not tell which record to inspect; when several Checks failed for one environmental cause, resolving all but one left a leftover that silently rejected the PASS hours later

---

## v0.9.0

Focus: upstream v0.9.0 feature ports on top of the fork's delivery-management core.

### Workspace
- Added an in-browser dock terminal: `WorkspaceService.ExecAsync` runs a bounded shell command in the vessel working tree (tenant-admins only, killed with its process tree on timeout), exposed as `POST /api/v1/workspace/vessels/{vesselId}/exec` and a dashboard Terminal panel
- Added an in-app review diff: `WorkspaceService.GetDiffAsync` returns a unified working-tree git diff, exposed as `GET /api/v1/workspace/vessels/{vesselId}/diff` and a dashboard Review Diff panel
- Hardened every workspace git invocation with a 30-second timeout, process-tree kill, and pager/credential suppression, so a wedged git cannot hang the workspace endpoints

### Inbox
- Added the needs-you inbox: `InboxService` aggregates missions in Review, failed landings, failed missions, stalled captains, failed merges, and deployments pending approval or failed/verification-failed, ordered most-urgent first
- Exposed the inbox as the MCP `inbox` tool, `GET /api/v1/inbox`, the dashboard Needs You page, the `armada inbox` CLI command (`--critical` filters), and `ArmadaApiClient.GetInboxAsync`

### Landing
- A landing retry that fails now records the conflicted-file list in the mission's `FailureReason` (`IGitService.GetConflictedFilesAsync`), so the operator sees exactly which paths to fix

### Reliability
- Background jobs left Accepted or Running past the stale threshold (a hung or dead worker) are reaped as failed on the health-loop cadence instead of reading as in-flight forever

### Captains
- Added `POST /api/v1/captains/{id}/unquarantine`, giving the MCP `armada_unbench_captain` tool a REST counterpart

---

## v0.8.0

Focus: backlog-first delivery management.

### Backlog and Objectives
- Added normalized first-class objective storage with ranked backlog metadata, lifecycle fields, source lineage, and continued `objective.snapshot` event emission
- Added backlog alias REST routes, ranked reorder support, dashboard/.NET client request models, and MCP backlog CRUD plus reorder aliases
- Added objective refinement sessions and transcript messages with explicit captain selection, captain availability checks, summary generation, apply-to-objective support, and server startup wiring

### Delivery Lineage
- Added automatic objective linkage through deployment and incident create/update flows, including inference from linked release, mission, voyage, and deployment context
- Added deployment and incident objective-link helpers so the same objective remains the record of truth as work moves from release into rollout and response

### Release and Migration
- Bumped shared product/package metadata to `0.8.0` across .NET, Helm, dashboard, Postman, and current-version API/documentation surfaces
- Added versioned `v0.7.0 -> v0.8.0` migration handoff scripts with backlog/objective and refinement table guidance for all supported backends
- Updated schema/version verification coverage for the new backlog schema baseline

### Reflection Memory
- Added the vessel reflection schema, threshold settings, and auto-dispatch of reflection missions during the audit-queue drain
- Added the MemoryConsolidator persona, reflection pipelines (single and dual-Judge), output-contract parsing, and consolidate / accept / reject memory-proposal MCP tools
- Added reorganize mode with soft-validation and a dual-Judge gate, quality metrics, and threshold persistence across vessel APIs
- Added v2-F1 pack curation: vessel pack-hint schema, pack-usage mining from captain logs, pack-curate briefs and auto-trigger, and a context-pack pre-selection pass
- Added v2-F2 persona and captain identity memory: persona/captain-learned playbooks, cross-vessel habit-pattern mining, six consolidation modes, and dispatch tooling
- Added v2-F3 fleet memory: fleet-learned playbook schema, fleet-curate threshold and fan-out, and stale-anchor detection for accepted memory notes

### Model-Tier Routing and Provider Neutrality
- Reworked preferred-model routing into two effective tiers (`mid` for Worker missions, `high` for specialist personas); the legacy `low` selector maps to `mid`
- Moved tier membership and provider routes into a configurable registry instead of hard-coded model names
- Added non-native-first selection: an idle external-provider captain is preferred over native models in any tier, unranked models are equal random peers, and OpenCode-runtime captains count as native for the preference
- Added Codex external-provider routing through a per-endpoint `--profile` config layer, OpenCode inline provider overlays, per-captain provider credential overrides masked on the MCP surface, and hot-reloadable settings for admission and tier policy

### Captain Prompt Shaping and Budgets
- Added a hard total-budget backstop that elides lower-priority content modules when a brief exceeds the captain budget; persisted mission descriptions and stage handoffs stay under the same budget
- Added measured prompt-budget telemetry on mission status alongside authoritative runtime token usage
- Added a `mission.git_anchors` brief module: the commit the work starts from, the target branch tip, the recent commits touching each path the mission names, and whether the mission's subject terms already exist on the checkout. A negative prior-art result is stated explicitly and anchored to the commit the search ran against, and prior art counts matching files rather than lines so the number is exact rather than a bounded sample
- Added `MissionSubjectExtractor`, which derives the anchored paths and terms from mission text deterministically and without git, so the selection a brief was built from is reproducible from the mission alone
- Added four anchor queries to `IGitService`, each defaulting to "nothing resolved"; anchors enrich a brief and never gate a dispatch. An unresolved block renders nothing and logs the reason, a partial block is marked `INCOMPLETE` so silence is not read as absence, and the block is capped, line-boundary truncated, and tracked in the prompt-budget ledger
- Added no-op completion rejection: a sub-minute empty-diff completion with a bare marker fails with `no_op_completion_detected` and re-dispatches to a different captain
- Persisted pipeline stage order with barrier parallel stages; the objective-scope brief module is emitted once per dispatch and read-only dispatches stay single-stage

### Recovery, Verification, and Gate Hardening
- Serialized the in-dock DoD gate host-wide under a dock lease so concurrent gates cannot crash the test host
- Attached Build and UnitTest checks to rescue voyages; a Judge PASS without green independent checks is rejected with a named failure reason
- Resolved rescue model tier against the rescue persona so a Worker rescue never strands on a high-tier-only roster
- Required a Judge verdict line before exit; an explicit agent verdict wins over a non-zero process exit, and a rejected PASS cannot loop into a fresh rescue
- Added distinct Judge review lenses, data-diff scoping, and an index-staleness sweep
- Added queryable long-running jobs, unlanded-branch visibility, mission stage-order persistence, and build-drift evaluation
- Fixed cross-tenant objective-link validation, ISO-8601 timestamp binding in five PostgreSQL drivers, and duplicate-reflection dispatch suppression

### Operations and Platform
- Added captain papercuts: structured friction reports collapsed by vessel, category, and problem
- Added a bounded disk-lifecycle scan and reconcile with sibling-worktree leases, and a maintenance sweep that prunes merged mission branches per policy
- Added per-captain provider credential overrides, papercut triage guidance, and authoritative token-usage telemetry through `/api/v1/events/token-usage`
- Added one-shot server provisioning (`bootstrap-server.sh`), virtual-CAN recreation on container start, and admiral image hardening (OpenCode and Mux CLIs, dotnet SDK, bounded build cache)

---

## v0.7.0

Focus: remote access.

### Remote Access
- Added an experimental outbound remote-control tunnel foundation in `Armada.Server`
- New `RemoteControl` settings are persisted in `settings.json` and exposed through `GET/PUT /api/v1/settings`
- Health and status responses now expose `RemoteTunnel` telemetry including state, instance ID, latency, and last error
- React dashboard, legacy dashboard, and `armada status` now surface remote tunnel configuration and live state
- Added request/response handling and server event forwarding on the tunnel contract
- Added `Armada.Proxy` with websocket tunnel termination, instance summaries, recent-event inspection, and live `armada.status.snapshot` / `armada.status.health` forwarding
- Added focused tunnel-backed remote inspection routes for recent activity, missions, voyages, captains, logs, and diffs
- Added bounded tunnel-backed management routes for fleets, vessels, voyages, missions, and captain stop
- Added a proxy-hosted remote operations shell at `/` for mobile-first remote triage, fleet and vessel management, voyage dispatch, mission editing, and captain control
- Added `docs/TUNNEL_PROTOCOL.md`, `docs/PROXY_API.md`, and `docs/TUNNEL_OPERATIONS.md` for the shipped tunnel and proxy contract

### Runtime and Hosting
- Updated the embedded server stack to Watson Webserver 7 for both HTTP and WebSocket handling
- Removed the standalone `WatsonWebsocket` dependency in favor of Watson 7's built-in WebSocket capability
- Fixed interactive server startup so `update.bat` and normal foreground launches no longer hang on startup handoff

### Dashboard and UX
- Reworked the setup wizard into a contained first-run workflow that uses dispatch directly instead of sending users into separate dashboard pages
- Expanded server settings with remote tunnel controls, MCP client references, system path inspection, database backup actions, and clearer hover guidance
- Added press-and-hold reveal controls for remote-control secrets and other protected login/setup inputs
- Added a full playbook management surface in the dashboard with list, detail, editing, delete, and ordered selection UX on voyage dispatch flows
- Added explicit success and warning toast feedback across dashboard mutation flows so save, delete, cancel, stop, and update actions acknowledge completion visibly
- Added a first-class `Workspace` experience with vessel-aware file browsing, editing, search, git status, context curation, and direct planning/dispatch handoff
- Added `System > Requests` and `System > API Explorer` so captured REST traffic, OpenAPI-backed live execution, and replay all live inside the Armada dashboard
- Added a first-class `Delivery` section in the dashboard for workflow-profile management, structured check-run inspection, and release drafting/detail flows
- Added first-class `Operations > Objectives`, `Delivery > Environments`, `Delivery > Deployments`, `Activity > Incidents`, and `System > Runbooks` dashboard surfaces for scoping, rollout, incident response, and guided operational execution
- Added `Activity > History` with saved views and export so cross-entity delivery memory spans objectives, planning, dispatch, checks, releases, deployments, incidents, events, merge activity, and request history

### Playbooks
- Added tenant-scoped markdown playbooks with CRUD across REST, MCP, proxy remote management, dashboard, CLI, SDK, and Postman
- Voyages and standalone missions can now carry ordered playbook selections with per-selection delivery mode: `InlineFullContent`, `InstructionWithReference`, or `AttachIntoWorktree`
- Mission dispatch now snapshots selected playbooks and injects them into mission instructions with resolved path metadata when file-based delivery is requested
- Added playbook persistence tables and schema migration support for SQLite, PostgreSQL, SQL Server, and MySQL
- Added reproducible mission-time storage of playbook filename, markdown content, selection order, and resolved delivery mode so later playbook edits do not rewrite historical execution context
- Added dashboard selection tooling that scales to larger playbook libraries through filtering, batch add/remove, and explicit ordering controls instead of one-card-per-playbook dispatch UI

### Internationalization
- Added a dashboard locale runtime, translation catalog, and persistent language selection available from login and the authenticated shell
- Added initial translations for English, Spanish, Simplified Chinese, Traditional Chinese, Cantonese, Japanese, German, French, and Italian
- Localized shared shell surfaces including login, pagination, notifications, setup wizard flows, and server/settings management views
- Expanded route-level coverage across list, detail, admin, and setup flows so Spanish no longer falls back to English on common table headers, filters, actions, and confirmations
- Routed legacy dashboard confirms, alerts, toasts, pagination affordances, and key static view copy through the shared i18n runtime so non-React surfaces honor the selected locale
- Added locale-aware date, time, and number formatting for dashboard runtime data
- Extended localization coverage to newer operational pages and shared controls so the playbook, dispatch, and administrative flows follow the same runtime and persistence model as the rest of the dashboard

### Planning, Runtimes, and API Tooling
- Added planning-session REST endpoints for list, create, transcript detail, message turns, summarize-to-dispatch, direct dispatch, stop, and delete flows
- Added persistent request-history capture, summaries, scoped delete flows, and replay metadata across SQLite, PostgreSQL, SQL Server, and MySQL
- Added Mux captain runtime integration, Mux endpoint/config support on captains, and runtime helper APIs for saved endpoint discovery
- Added live OpenAPI publishing at `/openapi.json` and `/swagger` to back the dashboard API Explorer and external tooling

### Delivery Workflows
- Added workflow profiles as first-class vessel/fleet delivery recipes for lint, build, unit test, integration test, e2e test, package, publish artifact, release versioning, changelog, deploy, rollback, smoke-test, and health-check flows
- Added workflow-profile validation, scope-aware default resolution, and required secret/config reference declarations across SQLite, PostgreSQL, SQL Server, and MySQL
- Added workflow-profile CRUD, validation, resolve, and enumerate APIs plus `ArmadaApiClient` support and dashboard list/detail/edit flows
- Added vessel readiness, setup-checklist onboarding, and typed workflow-input preflight across Workspace, vessel detail, Planning, Dispatch, and Checks
- Added structured check runs with durable status, timings, logs, artifacts, parsed test/coverage summaries, compare-to-previous-run analysis, retry, branch/commit metadata, and mission/voyage/release linkage
- Added check-run execute/import/read/retry/delete/enumerate APIs, dashboard list/detail flows, and launch hooks from Workspace, vessel detail, mission detail, voyage detail, and release detail
- Added first-class release records with version inference, draft/candidate/shipped state, artifact aggregation, linked work, and refreshable derived notes
- Added first-class objective records with linked vessels, planning sessions, voyages, checks, releases, deployments, incidents, and acceptance criteria
- Added first-class environments and deployments with approval, verification, rollback, request-history evidence, and default-environment seeding on startup
- Added first-class incidents, hotfix handoff, and playbook-backed runbooks with execution history
- Added optional server-global `GitHubToken` configuration plus per-vessel `GitHubTokenOverride` fallback with write-only update semantics, request-history redaction, and `hasGitHubTokenOverride` read models across REST, MCP, WebSocket, and dashboard surfaces
- Added pull-based GitHub delivery integration for objective import from issues or PR scope, GitHub Actions sync into structured checks, and GitHub PR review/check evidence on mission and release detail surfaces
- Added MCP enumeration support for `workflow_profiles`, `check_runs`, `releases`, `objectives`, `deployments`, `incidents`, `runbooks`, and `runbook_executions`
- Added MCP delivery and operations tools for `run_check`, `get_check_run`, `retry_check_run`, `create_release`, `get_release`, `create_objective`, `get_objective`, `create_deployment`, `get_deployment`, `approve_deployment`, `verify_deployment`, `rollback_deployment`, `get_runbook`, `get_runbook_execution`, and `start_runbook_execution`
- Added WebSocket delivery and operations events for `check-run.changed`, `objective.changed`, `deployment.changed`, `deployment.progress`, `environment.health`, and `approval-needed`

### Release and Docs
- Updated shared release metadata, docker tags, Postman examples, REST docs, MCP docs, and WebSocket docs to reflect the shipped objective, environment, deployment, incident, runbook, and history surfaces
- Promoted the shipped remote-management guide into `docs/REMOTE_MGMT.md` and archived the earlier planning doc
- Added no-op `v0.6.0 -> v0.7.0` migration scripts to reflect the release even though no database schema change is required
- Updated README and operator docs for the new playbook lifecycle, delivery modes, workflow profiles, readiness/onboarding, structured checks, releases, history, remote playbook management flows, workspace, request-history, planning-session, Mux, and internationalized dashboard behavior
- Expanded database, automated, MCP, WebSocket, request-history, and dashboard Vitest coverage around workflow profiles, checks, releases, and history

---

## v0.5.0

Focus: dispatch and pipeline stability.

### Dispatch and Pipeline Stability
- Hardened architect-to-worker handoff behavior, mission status freshness, branch cleanup, worktree cleanup, and landing paths
- Improved mission and voyage telemetry so active work reports current state more reliably
- Tightened dock/worktree safety to prevent dirty fresh docks and stale branch leakage

### Captains and Runtime Selection
- Added optional `Model` on captains across SQLite, MySQL, PostgreSQL, and SQL Server
- Captain model selection is exposed through dashboard, REST, MCP, and Postman examples
- Runtime launches now pass the configured model where supported, otherwise the runtime chooses its default
- Captain create/update now validates configured models before saving and returns a user-facing error when the model is invalid or unavailable

### Missions and Pipeline Reliability
- Added `TotalRuntimeMs` on missions, surfaced in API responses and the mission detail dashboard
- Mission create/update now touch parent voyage `LastUpdateUtc` so active voyages report fresh status
- Architect handoff text now strips trailing `[ARMADA:*]` control markers before passing instructions downstream
- Worktree creation now fails fast if a fresh dock is dirty, preventing unrelated tracked-file contamination
- Dock and mission branch cleanup was hardened across no-op landing and published-server worktree reclamation paths

### Git and Landing
- Worktree branch creation now creates the branch ref before attaching the worktree and keeps existing-branch docks on the named branch
- Merge handling now retries with `--allow-unrelated-histories` when needed
- Diff capture now falls back cleanly when there is no merge base instead of producing an empty snapshot
- Architect-only branches are cleaned up after successful fan-out instead of lingering indefinitely

### Dashboard and Docs
- Captain detail now supports editing and displaying the configured model
- Mission detail now uses a four-column layout and shows total runtime
- Login secret inputs now support a press-and-hold reveal control
- Dispatch page no longer shows the redundant detected-task UI or stale task-splitting guidance
- README, REST API, MCP API, compose.yaml, and release metadata are updated for `v0.5.0`

---

## v0.4.0

### Personas and Pipelines
- Added personas: named agent roles (Worker, Architect, Judge, TestEngineer) with custom persona support
- Added pipelines: ordered sequences of persona stages (WorkerOnly, Reviewed, Tested, FullPipeline) with custom pipeline support
- Pipeline resolution: dispatch param > vessel default > fleet default > WorkerOnly
- Architect stage special handling: parses [ARMADA:MISSION] markers to create multiple Worker missions
- Stage handoff: injects prior stage output (agent stdout + diff) into next stage description
- Persona-aware captain routing: AllowedPersonas and PreferredPersona on captains
- Mission dependency chain: DependsOnMissionId gates assignment until predecessor completes

### Prompt Templates
- Every prompt is now template-driven and user-editable (18 built-in templates)
- Categories: mission, persona, structure, commit, landing, agent
- Dashboard two-column editor with parameter reference panel
- MCP tools: get/update/reset_prompt_template
- REST endpoints: /api/v1/prompt-templates CRUD

### Dashboard
- Personas, Pipelines, Prompt Templates pages
- Pipeline dropdown on Dispatch, Voyage Create, Vessel, Fleet
- Mission detail: persona badge, depends-on link, failure reason display
- Captain detail: AllowedPersonas, PreferredPersona fields
- Vessel edit: 95% width, 3-column layout
- Log viewer: LIVE/DONE indicators, follow mode
- Toast notifications instead of layout-shifting banners
- Consistent CopyButton component across all pages
- Version display on login and sidebar

### Infrastructure
- Schema migrations 19-23 across SQLite, MySQL, PostgreSQL, SQL Server
- FailureReason field on missions (surfaced in dashboard)
- Vessel deletion cleanup: cancels missions, deletes docks, bare repo
- Empty repo auto-seed: creates README.md on first dispatch to empty GitHub repo
- Process.Dispose on agent exit to release Windows directory handles
- Crash logging: AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException
- Case-insensitive email login
- CLAUDE.md auto-gitignored in worktrees

### API
- 11 new MCP tools (persona, pipeline, prompt template CRUD)
- 17 new REST endpoints
- 12 new WebSocket commands
- enumerate supports personas, prompt_templates, pipelines
- dispatch accepts pipelineId and pipeline (name) parameters
- Voyage status considers LandingFailed, WorkProduced, PullRequestOpen as terminal

### Documentation
- PIPELINES.md: complete implementation reference
- PERSONAS_GUIDE.md: user-facing guide
- TESTING_PIPELINES.md: 6 end-to-end test examples
- OLLAMA_AS_CAPTAIN.md: implementation plan for Ollama runtime
- VLLM_AS_CAPTAIN.md: implementation plan for vLLM runtime

---

## v0.3.0

### Added

- **Multi-tenant support** -- all operational data (fleets, vessels, captains, missions, voyages, docks, signals, events, merge entries) is scoped by tenant
- **Tenant, user, and credential models** -- `TenantMetadata` (`ten_` prefix), `UserMaster` (`usr_` prefix), `Credential` (`crd_` prefix)
- **Bearer token authentication** -- 64-character random alphanumeric tokens linked to a specific tenant and user, sent via `Authorization: Bearer <token>` header
- **Encrypted session tokens** -- AES-256-CBC self-contained tokens with 24-hour lifetime, sent via `X-Token` header. No server-side session storage required
- **Authentication endpoints** -- `POST /api/v1/authenticate`, `GET /api/v1/whoami`, `POST /api/v1/tenants/lookup`
- **Onboarding endpoint** -- `POST /api/v1/onboarding` for self-registration (gated by `AllowSelfRegistration` setting)
- **Tenant CRUD endpoints** -- `GET/POST/PUT/DELETE /api/v1/tenants` (admin only, with self-read for non-admins)
- **User CRUD endpoints** -- `GET/POST/PUT/DELETE /api/v1/users` (admin only, with self-read for non-admins)
- **Credential CRUD endpoints** -- `GET/POST/PUT/DELETE /api/v1/credentials` (admins: all; non-admins: own credentials)
- **Admin vs non-admin access patterns** -- three-tier authorization: `NoAuthRequired`, `Authenticated`, `AdminOnly`
- **Default data seeding** -- on first boot, creates default tenant, user (`admin@armada` / `password`), and credential (bearer token `default`)
- **React dashboard** -- standalone React dashboard (`Armada.Dashboard`) as a separate deployment option for Docker/production
- **Docker Compose with dashboard** -- `compose.yaml` runs `armada-server` and `armada-dashboard` containers together
- **SQL Server support** -- added SQL Server as a database backend option alongside SQLite, PostgreSQL, and MySQL
- **`AllowSelfRegistration` setting** -- controls whether `POST /api/v1/onboarding` is enabled (default: `true`)
- **`SessionTokenEncryptionKey` setting** -- AES-256 key for session token encryption (auto-generated if not provided)

### Changed

- All REST API endpoints now require authentication (except health check, authenticate, tenant lookup, onboarding, and dashboard routes)
- All operational database queries are tenant-scoped for non-admin users
- Admin users see all data across all tenants
- CORS headers now include `Authorization` and `X-Token` in `Access-Control-Allow-Headers`

### Deprecated

- **`X-Api-Key` header** -- retained for backward compatibility but deprecated. When configured, the server creates a synthetic admin tenant (`ten_system`) and user (`usr_system`). Migrate to bearer tokens for new integrations

---

## v0.2.0

### Added

- Multi-database support (SQLite, PostgreSQL, MySQL) with connection pooling
- Structured `database` object in `settings.json` replacing flat `databasePath`
- Migration scripts for v0.1.0 to v0.2.0 settings conversion
- Merge queue system for automated branch merging
- WebSocket hub for real-time event streaming
- Embedded dashboard at `/dashboard`
- MCP server with full API parity (18 tools)
- Batch delete operations for all entity types
- Enumeration (POST) endpoints with JSON body filtering
- Mission diff and log retrieval endpoints
- Captain log streaming
- Dock (worktree) management endpoints
- Signal system for Admiral-captain communication
- Event audit trail

### Changed

- Settings format: `databasePath` string replaced with `database` object (breaking change)

---

## v0.1.0

### Added

- Initial release
- Core orchestration: fleets, vessels, captains, missions, voyages
- Git worktree isolation for parallel agent work
- Multi-runtime support: Claude Code, Codex, Gemini, Cursor
- Auto-recovery for crashed agents
- REST API on port 7890
- CLI (`armada`) with Spectre.Console
- SQLite database backend
- Zero-config startup with auto-detection
