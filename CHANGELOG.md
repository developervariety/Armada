# Changelog

All notable changes to Armada are documented in this file.

---

## Unreleased

Focus: operator signal fidelity - make a failure say what actually failed.

### Objectives and the scheduler
- An objective can carry a `StartFromRef` (`startFromRef` on `create_objective` / `update_objective`): the first stage of a voyage dispatched for it is cut from that ref instead of the vessel default branch, so a requeue from a `recover/<name>-<sha>` ref continues accepted work instead of rebuilding it. A ref that does not resolve refuses the dispatch before any voyage row exists (`start_from_ref_missing`, reported as the scheduler skip reason and an `objective_scheduler.start_from_ref_missing` event), and a ref that has gone by assignment time fails the mission by name. There is no fallback to the default branch.
- A requeued objective whose linked voyages have all ended dispatches again instead of sitting in `Dispatched` for ever.
- A scheduler pause is attributed to its session (`pausedBy`, `pauseReason`); the autonomy layer may clear only a stale pause whose owner has been absent longer than the configured threshold.
- An engaged dispatch hold is reported as `dispatch_hold`, once per sweep, never as `dispatch_error`; a sweep that dispatches nothing names the constraint with counts (`vessel_count`, `vessel_concurrency`, `objective_skipped`, `no_eligible_objectives`).
- Incident links are annotations: linking an objective to an incident no longer copies the incident's voyage and mission onto the objective, which had silently removed the objective from auto-dispatch.
- Campaign status projects roots the same way as lanes; slices are opt-in and the lane rollup is the contract.

### Checks and the Judge gate
- A Judge PASS is held when a green Check measured an older commit than the reviewed tip; the check executor supersedes the stale record (`check.superseded`, Canceled, naming its successor) and arms a fresh one at the tip. A failed Check for an older commit is treated the same way, as stale rather than as a rejection, on Open voyages as well as InProgress ones.
- Rescue voyages ARM their Build and UnitTest Checks instead of running them at rescue dispatch against the default branch, and the scheduler counts rescue voyages toward its ceilings.
- A single deterministic assertion failure classifies as `TestFail`, not `Infra`, even when the output also carries a NuGet warning or the word "dependency".
- Judge rules: accepted work must be an ancestor of the reviewed tip or present in the diff; delivery is proven by the diff under review, not by presence at the tip; a deliverable that lives in a stage's final response is never NOT DELIVERED for being absent from the tree; a citation inaccuracy inside a remark is a Suggested Follow-up, not a NEEDS_REVISION.

### Dispatch, assignment and rescue
- Branch continuation is decided by equality with the dependency's branch name, never by a name merely being present on the row, so a leftover name from an aborted assignment no longer fails a fan-out worker with `stage_base_missing`.
- A planner stage that committed code fails before any fan-out worker is created, instead of every worker failing two stages later.
- A selected captain is reserved in-process before provisioning, so two assignment passes cannot both provision a dock for one idle captain.
- A Worker that failed inside a voyage is rescued through a rescue voyage, never as a standalone mission.
- Rescue briefs: a Judge's narration preamble is dropped before any size budget applies; a Judge report's Suggested Follow-ups and Verdict stay whole in an over-cap brief; a documentation-only rescue under a Research objective is the work, not an `ineffective_rescue`.
- Architect handoff: a front-matter block is titled from its title line, and every spawned brief carries the plan-block-label rule.
- A stale sibling extraction-artifact copy in a shared sibling worktree is refreshed atomically instead of skipped.
- A dock that reuses a shared sibling worktree behind its branch tip (another dock holds a lease, so the worktree cannot move) logs both commits and emits `dock.sibling_stale`, instead of measuring the older tree silently. The per-dock sibling layout that would remove the condition is an open decision.

### Captain lifecycle
- The handled-exit marker is kept until completion handling (DoD gate, handoff, dock provisioning) finishes. The captain health check re-reads the mission and ignores a vanished PID for a post-work or terminal mission (`captain.process_exit_ignored`) instead of synthesising exit code -1, failing the finished stage and cancelling the Judge with no incident.
- The Cursor runtime passes `--approve-mcps` so the dock's Armada MCP server loads in `--print` runs.
- The one-line AI-Memory pointer is seeded in the runtime's auto-memory folder before launch.

### Storage
- PostgreSQL and SQL Server: token-usage records bind `Estimated` as an integer and `CreatedUtc` as an ISO-8601 string, matching the columns; every insert and every summary read had failed, so the Activity page's Token Usage tab showed nothing. A duplicate token-usage route registration is removed.
- PostgreSQL: the request-history truncation flags are BOOLEAN and the request-history tables are created when absent (migration 81). Every request-history capture had failed with 42804 because the live columns were INTEGER while the model binds a bool, so no request was recorded.

### MCP surface
- Tool-call arguments are normalised against the tool schema once at the transport seam; an empty string for any optional argument is treated as omitted, and string-spelled booleans and numbers are converted.
- The six oversized list tools preview long free-text fields and long primitive arrays, and the default list page size fits a tool result.
- Directed wakes are delivered on any MCP tool result for a session that sends `X-Armada-Participant`; the effective AgentWake participant may acknowledge an unaddressed Wake; long broadcast notes are previewed on a board read while directed mail always arrives whole.
- The inbox lists open incidents and hides failed missions whose voyage has halted.

### Dashboard
- Login: the form uses email autofill semantics, sends bearer and session auth headers, and its layout is repaired; the chat view preserves its reading position.

### Autonomous lead
- The operator's blocking poll is replaced by a WebSocket subscription watcher and bounded autonomous lead cycles: prompts are passed on stdin, the cycle runs under a scoped permission policy, the whole cycle is logged with a 30-minute cap, it runs from the Armada checkout whoever started it, survives a redeploy, and is routed through the configured Fable captain.
- A delegate helper class with permission-enforced limits; the lead posts its handoff from the completion gate exactly once, defaults the room key to `fleet`, refuses a cycle while an operator holds the work, and treats rows another participant owns as read-only.
- A Grok lead integration foundation with its evaluation notes.

### Coordination board (chatroom)
- A shared coordination board keeps concurrent operator sessions on the same page: rooms hold short notes about who is doing what, so a session that reads the board before dispatching no longer mistakes another session's voyage for unowned work or double-dispatches a rescue
- Three entities back it - rooms keyed by a unique slug, messages carrying an author type (Operator / Captain / System) plus optional voyage, mission, vessel, and incident references, and per-room participant presence refreshed by heartbeats - with full SQLite and PostgreSQL implementations and migrations v75/v76; MySQL and SQL Server follow the existing stub convention for planning sessions
- Three MCP tools expose it to operator sessions: `armada_coordination_post` to claim work before starting it and report outcomes, `armada_coordination_read` for recent notes plus active participants, and `armada_coordination_heartbeat` for presence. REST counterparts live under `/api/v1/coordination/`
- The admiral mirrors selected fleet events onto the default room as system notes (`voyage.dispatched`, `voyage.cancelled`, `mission.completed`, `mission.failed`, `mission.cancelled`) through the central event choke point, so new voyages announce themselves without anyone posting manually
- The dashboard gains a `/chatroom` page: room list, presence chips with last-seen times, a chronological stream, a composer, live WebSocket delivery with a polling fallback, and a per-browser heartbeat so an open dashboard shows as present
- Board notes are advisory context only. They never inject into captain briefs; signals remain the handoff-boundary mechanism. Captains can add one-line `[ARMADA:NOTE]` milestones from mission output, and the admiral links those notes to the mission on the board

### Coordination claims
- Sessions can now RESERVE work instead of only posting about it: `armada_coordination_claim` creates a reservation against a vessel or objective with a named holder and an expiry (default 4 hours, clamped 0.5-72). Heartbeats keep a live session's claims alive automatically; a lapsed claim disappears without anyone cleaning up
- A dispatch that overlaps an active claim someone else holds proceeds, but announces the overlap on the board as a system note naming both parties and the claim's expiry - reservations are named and visible, not locks
- The Needs-You inbox gains a StalePeer warning when a session has gone silent for over 15 minutes while still holding an unexpired claim, so an absent peer's reserved work surfaces for adoption instead of quietly blocking everyone
- The dashboard chatroom renders active reservations as amber chips above the message stream - holder, subject, and time-to-expiry - refreshed on board activity so claim and release announcements update the strip immediately
- Claims ride the same SQLite/PostgreSQL implementations as the board (migrations v77/v78) with MySQL/SQL Server stubs; on stubbed backends the conflict check and inbox scan skip gracefully

### Campaign system and captain voice
- Captains get a voice on the board: an `[ARMADA:NOTE] one-line note` marker in agent output becomes a Captain-author board message linked to their mission (20 per mission, credential-redacted through the papercut scrubber). Every mission brief now teaches the channel; the porting playbook asks for milestone notes explicitly. A judge's notes are dropped like its papercuts - it already has the verdict channel
- `armada_campaign_status` answers "where does this campaign stand" in one call: the objective tree under a tag or root objective resolved two levels (hub -> lanes/programs -> slices) with statuses, active claims, and recent board notes
- `armada board` lands in Helm: recent notes, active reservations, and active sessions from the terminal
- Addressed notes now EMIT a Wake signal (`[to=<key>]` payload prefix), so a helper session's next heartbeat or read surfaces a targeted UnreadWakes list instead of requiring a full room re-read - the pause-and-read nudge for sessions inside blocking loops. Acknowledge with armada_mark_signal_read
- An addressed note to the participant key of the registered AgentWake session now also starts that session in `SpawnProcess` or `Both` delivery mode while always retaining the Wake signal row. OpenCode starts a fresh session instead of resuming, so the wake text carries the task and the session reconstructs state from the board and durable memory
- Notes can be addressed to one participant (`toParticipantKey`), turning the board into a work-handoff channel between operator sessions: a new session told only to "join the chatroom" reads broadcast plus its own mail and picks up addressed asks. Voyage-tagged notes now also reach captain briefs - at each stage handoff, notes naming the voyage created since the prior stage started are appended under a dedicated heading; general fleet chatter remains advisory
- The autonomous lead kit now has a bounded, tested host-helper launcher plus a reusable fresh-cycle prompt. It enforces process caps and timeouts, injects the read-only board/Wake contract, records participant keys, and provides explicit list, kill, and cull operations; the operator guide also separates Armada's built-in objective scheduler from optional externally started lead cycles and prevents one participant key from being owned by both a resident helper and AgentWake
- AgentWake can now retain a stable lead `participantKey` in settings across Admiral restarts, its status tool reports configured and effective ownership, helper offer mode allows a bounded reassignment window before fallback work, Claude helpers receive the explicit local Armada MCP config required by strict mode, and the autonomy guide defines controlled multi-voyage lane refill instead of treating one global voyage as the normal throughput limit
- The objective scheduler now has a persisted `maxConcurrentVoyagesPerVessel` ceiling (default 1), so a fleet-wide capacity of three can dispatch independent vessels without starting three conflicting voyages on the same vessel; operator-linked voyages count toward both ceilings
- Supported mission captains now receive the local Armada MCP URL by default. Claude strict mode receives an explicit `--mcp-config`, Codex receives a per-process URL override without hiding its authentication, Gemini and Cursor receive their project files, Mux receives an isolated config directory, and OpenCode receives the remote endpoint in its dock config
- The tri-source porting campaign is structured as the first instance of the campaign pattern: hub with `port:jpro` / `port:otr` / `port:dxp` lanes, four strategic programs re-parented beneath, thirteen active items re-parented, completed history left flat. The pattern is opt-in grouping; plain objectives outside campaigns stay the default for ordinary features and fixes

### Dispatch hold
- `armada_dispatch_hold` engages, clears, or inspects a fleet-wide dispatch hold so an operator working on Armada itself can stop new voyages before a rebuild or redeploy. While engaged, every voyage and mission dispatch through the admiral is refused - operator MCP, REST, standalone missions, and the autonomous objective scheduler alike - while in-flight voyages continue
- The refusal names when the hold started, who set it, why, and the exact call that clears it, instead of a generic failure an operator has to decode
- Engaging or clearing posts a system note to the coordination board automatically, so peer sessions learn about the freeze without polling anything
- The guard sits at the admiral's dispatch entries (`DispatchVoyageAsync`, `DispatchVoyageQueuedAsync`, `DispatchMissionAsync`, `DispatchMissionQueuedAsync`), so every current and future caller inherits it rather than re-deriving it
- The hold is runtime state and an admiral restart clears it deliberately: a successful redeploy resumes dispatching on its own, failing open where failing closed would strand a fleet behind a forgotten flag

### Definition-of-done gate
- A passing gate now also builds every vessel that declares the mission's vessel as a sibling repository, so a public-API break is caught while the producer's change is still unlanded instead of surfacing on whatever builds next. The consumer edge is derived from the existing `SiblingRepos` declarations read in reverse, so nothing new has to be configured
- Each consumer is provisioned under a scratch root private to that verification. A shared sibling checkout owned by another dock is reused rather than re-pointed, so verifying through one could compile the consumer against a different commit than the one being reported on
- Consumers are built, not tested: a build catches the break that leaves a target branch red, while running every consumer's suite inside every producer gate would cost more wall time than the gate itself
- A consumer that fails to compile fails the gate; a consumer that cannot be prepared is reported and the gate passes, since a missing profile or repository is a fault in the verification rather than evidence about the change. `DefinitionOfDone.FailOnConsumerVerificationError` reverses that, and `DefinitionOfDone.VerifyDeclaredConsumers` disables the step

### Pipeline
- A downstream pipeline stage now PROVES its checkout contains the commit its predecessor produced, before a captain is allowed to work in it. Inheriting a branch name is not inheriting its commit: a local ref can predate the upstream stage's push, and the worktree then looks correct while missing the work. One Worker's dock was cut without the preceding stage's commit, rebuilt on a base still carrying errors that stage had already fixed, failed on them, and took ten downstream missions with it - and every symptom pointed at the Worker's own code
- A stage whose checkout demonstrably lacks the upstream commit fails with `stage_base_missing`, naming the commit, the branch, and the upstream mission, and stating that this is a provisioning fault rather than a defect in the stage's work
- A base that cannot be PROVED is not treated as one that was: an unresolvable ancestry probe or an upstream that produced no commit is recorded as unverified and the stage proceeds. Cross-vessel dependencies are exempt, since commits are not shared across repositories
- `IGitService` gained a three-state ancestry probe whose default answer is unknown rather than true, so an implementation that does not consult a real repository cannot report a verification it never performed

### Recovery
- An autonomous rescue is now judged by what it CHANGED, not by whether it ran. A rescue whose change set is empty, or consists only of documentation, fails with `ineffective_rescue` and the change set named, instead of being accepted because the process stayed alive. The case this addresses ran for twenty-four hours, drew escalating stall nudges, died on a runtime crash, and left one changed documentation file behind - and every liveness measure the platform kept called that a working rescue
- Only rescues are assessed, and only in Implementation mode. A first-attempt mission may legitimately have been dispatched to write documentation, and an Audit or Research mission delivers a report and is never expected to change code - judging those by a diff is the same mistake in the other direction
- The assessment reads changed paths from the diff's `diff --git` headers only, so a hunk body containing a line that looks like a header cannot make a change set describe itself
- It deliberately does NOT compare the rescue's paths against the original mission's: a rescue is expected to rewrite the prior branch from scratch over the same files, so an overlapping path set would flag the normal case
- The autonomous-rescue marker had two definitions in two files; both now delegate to one, so the rule cannot drift apart

### MCP

- `tools/list` now accepts the protocol-valid request form that omits `params`, which restores tool discovery for clients that use parameterless initial discovery
- Remote-trigger and AgentWake settings now apply through the settings-file watcher, including enable, disable, runtime, participant ownership, delivery mode, and throttle changes, without an Admiral restart
- `run_check`, `retry_check_run`, and `get_check_run` now return a bounded view of a check run - status, exit code, parsed test and coverage totals, artifacts, and the last 40 output lines - instead of the complete command log. A build or test log is routinely one to several megabytes, which overran the tool output limit and returned a truncation error in place of the verdict, forcing a parse step out of band on every call
- The complete log is still available deliberately: `get_check_run` takes `includeOutput=true` for the whole record, and `outputTailLines` to widen the tail. A truncated view reports the full log's size and names the call that fetches it, so nothing is silently withheld
- The tail is whole lines taken from the END of the log, which is where a failure's cause almost always is

### Dispatch
- Dispatch now arms the new voyage's Build and UnitTest Checks itself, so a voyage no longer reaches its Judge stage carrying none. A Judge PASS is rejected without a green independent Check, so a bare voyage was already condemned when it started and nothing said so until the whole pipeline had run. Cancelling a voyage discards its Checks, so a re-dispatch previously started bare again
- Armed Checks are created `Pending`, not executed. They become runnable after a stage commits work, and the executor records the branch and commit before running the check against that exact work, so arming costs nothing at dispatch and never measures the default branch by accident
- A type is armed only when the resolved workflow profile defines its command, and never twice for one voyage - a second Build beside a failed one would leave a green and a red attached, and one failed Check rejects a PASS however many green ones sit beside it
- Arming failures are logged and never fail the dispatch; `VoyageCheckArming.Enabled`, `ArmBuild`, and `ArmUnitTest` control the behavior

### Checks
- A Judge PASS rejected by the real-signal gate now NAMES the Checks that blocked it (id, type and label) in the mission's `FailureReason`, and states that every failed Check must be resolved. The message previously named only the rule, so an operator could not tell which record to inspect; when several Checks failed for one environmental cause, resolving all but one left a leftover that silently rejected the PASS hours later

### Recent dashboard and session updates
- Re-aligned the dashboard with the consolidated navigation while retaining fork-specific Code Index, Notifications, coordination, token-usage, and captain-assignment surfaces
- Added addressed-note wake delivery, registered-session spawning, participant-key visibility, and documentation for helper-session handoffs
- Added bearer and session-auth headers to dashboard requests where the relay requires both forms of authentication
- Repaired dark readiness cards, workspace layout, responsive shell controls, mobile sidebar navigation, and Ask Armada control sizing

---

## v0.9.0

Focus: upstream v0.9.0 feature ports on top of the fork's delivery-management core.

### Delivery
- Added CD webhook integration: when a release transitions to Shipped (operator approval), the admiral POSTs a `release.shipped` JSON evidence payload (release identity, version, tag, linked voyages/missions/checks) to a configured external endpoint, so any continuous-delivery system can pick up deployment
- Configured under the optional `cdWebhook` key in `~/.armada/settings.json` (`enabled`, `url`, optional `bearerToken`, `timeoutSeconds`, `maxRetries`, `retryBackoffSeconds`); absent or disabled means no behavior change
- Retriable webhook failures (5xx, network, timeout, auth) are retried up to `maxRetries` times with a fixed backoff; non-retriable 4xx responses return immediately
- Added the MCP `test_release_webhook` tool (registered only when the CD webhook is configured): sends a synthetic payload and returns the delivery outcome, for verifying endpoint reachability and authentication before approving releases
- Added `GET /api/v1/releases/{id}/webhook-events` and a CD Webhook Delivery card on the release dashboard page showing each delivered/failed attempt with HTTP status, message, and timestamp; the card supports auto-refresh
- Fixed dashboard tests on hosts where Node's global `localStorage` binding shadows jsdom's (in-memory polyfill in the test setup) and excluded macOS AppleDouble `._*` sidecar files from vitest discovery
- Dispatch outcomes are recorded as `release.webhook.delivered` / `release.webhook.failed` events on the release; transport failures never block or fail the release update

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
