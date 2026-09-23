# Changelog

Net changes since the latest full upstream merge on 2026-05-24
([merge baseline](https://github.com/developervariety/Armada/commit/cb9030bd5cbaca71efe738b53807f048afd05cd7)).
This compares the merged tree with the current fork. It includes later selected
upstream integrations and excludes changes already present at that baseline.

## Added

- **Typed decisions:** provider-neutral closed-question classification behind
  deterministic rules; conservative Gate behavior, key-based availability,
  redaction and egress exclusions, custom decisions, evaluation on model changes,
  host-local retained samples with redacted question definitions, request hashes,
  returned model versions and answer distributions; redactor-version cohorts and
  operator reversals. Sample counts are separate from verified training readiness.
  Captain tools provide premise, prior-art, change-quality, memory, and per-item
  readings. Operator tools report evaluations and route findings for review.
- **Native captain memory:** scoped episodic, semantic, and procedural records,
  versioned writes, recall, Recorder review, and operator-managed memory proposals.
- **Context retrieval:** a generated context index, core bundle, scoped leaf
  retrieval, optional metadata sidecar, and optional brief slimming with a full
  memory-reading fallback.
- **Coordination:** shared room messages, claims, presence, directed wakes,
  persistent AgentWake targets, campaign views, a WebSocket watcher, and bounded
  helper processes.
- **Dispatch admission:** objective preparation claims and preflight, dependency
  diagnostics, environment requirements, fleet and sibling-lane capacity leases,
  operator-confirmed stage skips, and durable dispatch jobs with restart-loss
  records.
- **Review and delivery evidence:** Linter and Recorder stages, acceptance-criterion
  evidence checks, operator review holds, .NET Slop and configurable banned-diff
  checks, declared-consumer checks, and scoped admission, dock-anchor,
  definition-of-done, recovery, and landing reports.
- **Account-aware routing:** subscription account usage, separate captain logins,
  usage previews, reserve policies, per-persona model lists and restrictions,
  account failure holds, and an optional required-login gate.
- **Remote execution and server control:** Harbor identity, runner enrollment and
  mission execution; guarded vessel branch controls; in-place server restart;
  supervised self-deployment preflight, retained-image rollback, and build identity.
- **Operational measurements:** production summaries, attempt and lane-time facts,
  host-slot wait, preparation observations, and post-land regression links.
- **Verification tooling:** a four-runner Linux gate, balanced unit shards,
  explicit test registration and result manifests, provider migration scenarios,
  API collection coverage, isolated test data directories, one owner suite
  for review-diff coverage, and a local-merge landing case that runs the real
  landing handler. Mission list projection (REST, MCP and the SQLite summary
  query), planning reply bounds, captain-override dispatch, project-profile
  briefs and process liveness are proven by behavior in their owner suites, not
  by source-text checks, and repeated stage-base and merge-queue Git cases run once.

## Removed

- Reflection-memory bootstrap, consolidation and learned-fact/pack-hint storage.
  Native captain memory and the external rules repository have separate owners.
- Hardcoded deployment-specific reviewer personas, pipelines, routing lists, and
  domain rules from general product defaults. Deployment settings supply these.
- Inline provider credentials on captain configuration. Captains reference
  inference endpoints or configured account logins.
- Obsolete per-runtime instruction copies and the upstream-review planning folder.
  Current subsystem guides and repository history retain the useful reference.
- Tracked operator-specific guide chapters, context metadata, and local reports.
  The repository supplies templates; deployments keep filled files locally.
- Unreferenced services and helpers: the feature-specific remote-control tunnel
  handlers (the tunnel serves the generic dashboard relay only), unwired
  resource-admission, no-op-completion, reasoning-effort, git-anchor, auto-land
  and dock-boundary helpers, the unused dock state enum, and test-only runtime
  and MCP wrappers. Their tests now exercise the live paths: the operator
  transition service against the whole transition table, the Claude Code launch
  environment, and the OpenCode output transform. Source files hold no literal
  NUL bytes, and the solution gives each automated test project its own name.
- The code-index `signatureModel` setting. Nothing read it: file signatures use
  the summarizer model. A settings file that still carries the key loads
  normally, and the next save drops it.

## Changed

- **Authentication and scope:** REST, MCP, WebSocket, chat, planning, and captain
  launch paths apply caller ownership. Captains receive scoped credentials;
  administrative tools and cross-tenant events remain restricted. Server-owned
  captain fields and write-only secrets have consistent write contracts.
- **Dispatch and pipelines:** operator and scheduler paths share objective defaults,
  admission, and Check arming. Root missions resolve and persist explicit or
  inherited start commits, return `MissionStartRefs`, and record resolution events.
  Alias dependencies, parallel-stage barriers, review gates, report-only modes,
  and cancellation use consistent lifecycle rules.
- **Review and landing:** Judge PASS requires distinct evidence for every criterion
  and matching immutable Check results. Brief trimming retains the full contract.
  Landing verifies ancestry, preserves a diverged working checkout, and reports
  failed synchronization without discarding its commits. Direct landing and the
  merge queue read the vessel, changed paths and diff through one evidence
  collector and refuse with a named `landing_evidence_unavailable` reason when a
  read fails. Changed paths are read NUL-separated, and one diff-path reader
  decodes Git quoting and keeps the old path of deletions and both paths of
  renames for protected paths, auto-land predicates, critical triggers and
  consumer-test triggers. Branch diffs surface unexpected Git errors instead of
  substituting a working-tree diff. Rollback after a failed landing is a
  conditional push against the inspected head, and merge-queue test commands
  drain both output streams and stop their process tree at
  `MergeQueueTestTimeoutSeconds`.
- **Consumer-test triggers:** reading a producer's changed paths returns a
  result that separates an unreadable change from a verified empty one. When the
  paths cannot be read, the definition-of-done gate runs every consumer suite that
  has trigger prefixes and logs the named `changed_paths_unavailable` reason.
- **Recovery and captain lifecycle:** recovery preserves accepted source commits,
  respects dispatch holds, distinguishes provider and test failures, bounds retries,
  and checks rescue effectiveness against the declared deliverable. Terminal
  markers end stages, and every marker in a multi-line output record is routed,
  so a message or papercut cannot hide a result or verdict. A captain's
  `[ARMADA:STATUS]` marker sets only InProgress, Testing, or Review, so post-work
  and terminal states always pass the completion and landing checks. Interrupted
  and duplicate completions retain launch identity. Cancelling a voyage also
  cancels its Testing and Review missions, including one waiting for a review
  decision, and recalls a captain that still holds one.
- **Dispatch hold:** automatic Checks obey the hold. The heartbeat runs no
  Pending Check while the hold is engaged, records one named deferral event per
  engagement, and runs the waiting Checks after the hold clears. Operator check
  runs are not held.
- **Captain administration:** REST, MCP, WebSocket and the dashboard share one
  service for single stop, emergency stop, deletion and restart. A single stop ends
  a Planning or Refining captain's session and stops and recalls any other captain.
  Stop all covers working captains, planning sessions and refinement sessions and
  reports stopped and failed counts with each failure named; a session kind the
  database provider does not store is named in `UnavailableSources` and the stop
  continues with the other kinds; Helm `captain stop-all`
  calls it once and prints those counts. Single and batch deletion refuse Working,
  Planning, Refining and mission-owning captains and remove the captain's events,
  planning sessions and refinement sessions; a dependent that cannot be removed is
  logged, counted and named in the result. Restart resets runtime state in place
  and keeps the captain's identity, configuration, credentials and holds.
- **Planning sessions on PostgreSQL:** PostgreSQL stores planning sessions and
  their transcript messages (schema migration 108) with the same scoping,
  ordering and message cascade as SQLite, so the dashboard planning page, the
  planning REST routes and MCP tools, planning timeline entries and stop all work
  on PostgreSQL deployments. MySQL and SQL Server still do not store planning
  sessions; their refusal names the provider.
- **Captain names:** captain create on REST, MCP and WebSocket applies one name
  rule: a name another captain already has is refused before anything is written.
  REST answers `409 Conflict` with a `Conflict` error instead of a database error,
  and MCP and WebSocket return the same message as their error result.
- **Dock and ref handling:** active owners protect their worktrees; idle orphan
  processes cannot block reclaim indefinitely. Persistent collisions expose the
  holding path and process IDs and fail after a bounded retry. Managed ref deletion
  records its caller and refuses user and recovery refs. New bare clones enable
  reflogs. A Git transaction hook records raw ref deletions with durable,
  restart-safe event import and OS or verified mission attribution.
- **Model selection:** one capability-tier rule drives eligibility and preview.
  Smart Routing filters and orders eligible captains by account usage and persona
  preferences; dead model-list entries have visible diagnostics and fallback.
- **Settings reload:** the manual reload endpoint and the settings-file watcher share
  one reload path bound to the server's own settings file. Settings updates and both
  reloads share one candidate validator for account key paths and captain runtime
  bindings. A missing, unreadable, or invalid file is refused and the current
  settings stay live.
- **Runtime execution:** inference-endpoint captains resolve endpoint credentials;
  API-endpoint missions have bounded command execution and deterministic conversation
  compaction, Anthropic stable-prefix caching, and provider cache/reasoning usage
  counters. Large output keeps diagnostic content and archives its full form.
  Runtime logs expose command and failure context, and launch configuration delivers
  the correct scoped MCP credential for each supported runtime.
- **API-endpoint tool bounds:** `run_command` owns a process group and kills it on
  timeout or cancellation, including children the shell left behind, and caps
  output while reading instead of after a whole line. `edit_file` and `multi_edit`
  refuse empty search text, observe cancellation, and list at most 20 candidate
  lines; each `multi_edit` step must be unique in the content earlier steps produced.
- **Agent process identity:** runtime stop and liveness act only on the process the
  admiral launched, verified by its recorded start time, and dispose the handles
  they open. Stop sends no shutdown request: a 3-second grace period, then a tree
  kill. A cancelled launch starts nothing, and a launch that fails after start
  kills its child. Gemini, Cursor, and Mux keep JSON error events in the mission log.
- **Launch and test races:** the liveness heartbeat reads its cancellation token
  before it registers the loop, so a process exit that stops the heartbeat while a
  launch starts it no longer fails the launch with a disposed-source error, and a
  loop removes only its own registration. A self-deploy child that exits and is
  reaped before its start time is read is recorded as started (with its launch
  time) and then reads as exited, instead of failing the launch with
  `launch_failed`. The shared and automated test hosts repeat a server start on new
  loopback ports when a found port is taken before the bind; request-history scope
  cases wait for the user's own capture, which is written after the response; the
  scheduler refill case waits for the event-triggered sweep to complete.
- **Indexing:** source chunks follow declaration boundaries, embedding clients use
  configured endpoints, captains search their own vessel, duplicate groups have an
  operator report, and dispatch staleness considers source relevance.
- **Index validity and enrollment:** semantic vectors belong to one embedding
  provider (model, endpoint and vector length); a provider change replaces every
  vector, including those on unchanged files. Index status reports embedding
  completeness apart from lexical freshness, and an update on the same commit
  retries missing vectors while it reuses valid ones. Every automatic refresh
  (landing, merge queue, stale dispatch, staleness sweep) shares one enrollment
  check and never indexes a vessel for the first time; only an explicit update
  does. The Admiral and the stdio MCP host select the inference client through one
  factory.
- **Search never indexes a vessel:** code search, fleet search, context packs and
  the baseline pack cache create no index for a vessel that has never been
  indexed and send nothing to the embedding provider. Vessel search and context
  packs return `Available: false` with `UnavailableReason: not_indexed`; fleet
  search and fleet packs list such vessels in `NotIndexedVesselIds`. An `auto`
  dispatch proceeds without code context and logs the reason; `force` fails and
  names it. A failed index update keeps the last successfully indexed commit, so
  the vessel stays enrolled and the staleness sweep still compares it.
- **Persistence and operations:** all database providers preserve delivery and
  ownership fields, validate migration prerequisites, and support expiry and backup
  evidence. Restore checks provider compatibility and retains local secrets.
  Status, inbox, jobs, and logs report bounded evidence and explicit failure reasons.
- **Stored-record fidelity:** every provider reads back all nine merge-entry audit
  fields through one shared column contract, and signal and event reads return their
  owning user. Pipeline create, update and delete write the pipeline and its stages
  in one transaction, so a failed write leaves the stored pipeline unchanged. The
  driver wiring check covers every method set.
- **Retention and history:** data expiry removes captured request history and its
  detail older than `requestHistoryRetentionDays` (default 30; `0` keeps it).
  PostgreSQL stores request capture times as ISO-8601 text, upgrades older rows,
  and indexes request history like the other providers, so same-day time filters
  and retention compare correctly. The history timeline leaves out a source the
  database provider does not store, names it in `UnavailableSources`, and logs it;
  captain deletion logs a skipped or failed dependent cleanup.
- **Delivery history filters:** PostgreSQL check-run, release and deployment
  queries bind UTC date bounds to their timestamp columns. Inclusive date ranges
  and paged totals work without a timestamp/text comparison error.
- **Provider test coverage:** the database runner checks the active-work footprint
  query on all four providers: active voyages count every mission, a mission
  without a voyage counts only while active, and terminal work and other tenants
  are excluded.
- **Helm server control:** `server stop`, `server restart`, `reset` and `config init`
  share one authenticated stop through `POST /api/v1/server/stop` and count the
  Admiral as stopped only when its health route no longer answers. A refused or
  unfinished stop exits non-zero, cancels a restart, and blocks every data deletion.
- **Lease, job, and shutdown lifecycle:** a dock lease release removes only the
  zero-count entry it observed, so a concurrent acquire keeps its lease. Sibling
  lease reconciliation takes the per-target lease lock and keeps a lease whose
  holder dock record is missing until the stale-lease grace has passed. A reaped
  background job is cancelled and its terminal status is final in memory and in
  the journal; each job status is journalled before it is visible. Admiral stop runs once, waits (bounded) for its background loops
  before disposing the database, and disposes it even when an earlier shutdown
  step fails.
- **Capacity admission:** one global workload reservation covers the active-workload
  count through the durable Assigned write, so parallel assignments in different lanes
  launch no more captains than `MaxConcurrentCaptainWorkloads`. Fleet-capacity
  admission reads the active-work footprint in one query on every database provider
  instead of enumerating retained voyage and mission history.
- **Recovery bookkeeping:** active-incident lookups filter out closed and
  rolled-back incidents first and read every match, so newer closed incidents
  cannot hide an open one. Voyage cancellation, including captain recovery under a
  cancelled voyage, cancels only Pending, Assigned and InProgress missions.
  Per-mission recovery gates and nudge-suppression records are released once the
  mission is no longer in use.
- **Dashboard and clients:** responsive navigation, scoped controls, complete entity
  forms, structured mission evidence, routing and account controls, endpoint health,
  tool activity, and consistent REST/MCP/WebSocket contracts.
- **Operator entry-point parity:** voyage cancel on REST, WebSocket, MCP and remote
  control runs one operation that stops running missions' agent processes and
  cancels InProgress missions. Batch dock delete refuses active docks per ID like
  single delete. Mission create on REST, MCP and WebSocket merges vessel
  default playbooks through one helper.
  Batch merge-queue purge is limited to a tenant administrator's own tenant.
  Every REST route's auth refusal names `NotAuthorized` (401) or `Forbidden` (403)
  in the body to match the status, through one shared mapping.
  The Helm stdio MCP host supplies mission status transitions and the record-backed
  services the admiral endpoint supplies (review hold, AgentWake, releases,
  deployments, runbooks, captain bench, unlanded branches, terminal-voyage
  reconciliation, disk lifecycle and captain process stop).
- **MCP tool results:** one rule decides that a tool result is an error: an
  explicit `isError: true` result, or a top-level non-empty `Error` string. HTTP
  returns such a result with `isError: true` and audits it as `Failed` with its
  message; local stdio applies the same rule. A null or nested `Error` is a
  successful result. `armada_job_status` reports a failed or lost job's reason in
  `FailureMessage`.
- **Remote proxy:** dashboard asset paths must resolve to the bundle directory or
  below it, so an encoded traversal cannot read a sibling directory with the same
  name prefix. A tunnelled request has one deadline covering send and response,
  releases its pending entry on every exit, and reports caller cancellation
  separately from a timeout. The tunnel protocol guide names the six advertised
  features, the two the proxy reads, and the `unsupported_method` answer to any
  method other than the generic relay.
- **Path containment:** one rule decides that a path is inside a root: it is the
  root itself or lies below the root plus a directory separator, compared
  ordinally after normalization. The server and proxy dashboards, check-run
  artifact collection and summary parsing, Workspace browsing, captain runtime
  configuration writes, Harbor directory mapping, dock occupant listing, and the
  API-endpoint workspace tools all apply it, so a sibling directory whose name
  starts with the root's name is outside.
- **Papercut merge:** the typed-decision merge works on copies of the listed
  groups, so an unavailable answer after an earlier merge returns the original
  groups with their original counts.
- **Dashboard loading:** a list refresh keeps the bulk selection for rows that still
  exist and drops only rows that vanished. Fleet and vessel pickers on project
  profiles and pipelines read every page. Mission, captain, vessel and merge-entry
  detail pages apply only the newest load, show the spinner and any load failure
  when the id changes, and never show the previous record. Only the current
  WebSocket drives connection state and reconnects; a replaced socket's late events
  are ignored. The home page's bounded mission-summary refresh has a dashboard test
  in place of source-text checks in the unit suite.
- **Mission brief model context:** the generated instruction file renders the
  vessel model context once as a `## Model Context` section when
  `EnableModelContext` is on and the text is not blank, on the direct and the
  template-resolved paths. The section is read-only background, is counted in the
  prompt-budget telemetry, and may be elided by the total-budget backstop; the
  persona memory guidance tells the captain to read it when the brief carries one.
- **Test ownership:** each behaviour has one executed test implementation. Shared
  copies of executed legacy cases and legacy copies of executed shared cases are
  deleted instead of skipped. Seeded-persona, model-context and default Claude
  argument cases assert the current contract, and the shared runner records no
  named skips.
- **Authentication and API contract tests:** the anonymous, wrong-key and
  empty-key cases probe one shared protected-route table and require exactly 401
  with a `NotAuthorized` body and no protected content; each refused write is
  checked with an authenticated read to prove nothing was stored or changed. The
  captain not-found cases require 404 with a `NotFound` error, the duplicate-name
  case requires 409 and one stored row, and the mission metadata binding cases run
  in the mission update section. Repeated CORS, valid-key and status auth cases
  are removed.
- **Documentation:** current contracts replace stale counts, rollout claims, and
  duplicate instructions. Product references are separate from deployment guides;
  the changelog records only the net delta from the upstream merge baseline.
