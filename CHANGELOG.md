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
  landing handler.

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
  failed synchronization without discarding its commits.
- **Recovery and captain lifecycle:** recovery preserves accepted source commits,
  respects dispatch holds, distinguishes provider and test failures, bounds retries,
  and checks rescue effectiveness against the declared deliverable. Terminal
  markers end stages, and every marker in a multi-line output record is routed,
  so a message or papercut cannot hide a result or verdict. A captain's
  `[ARMADA:STATUS]` marker sets only InProgress, Testing, or Review, so post-work
  and terminal states always pass the completion and landing checks. Interrupted
  and duplicate completions retain launch identity.
- **Captain administration:** REST, MCP, WebSocket and the dashboard share one
  service for emergency stop, deletion and restart. Stop all covers working
  captains, planning sessions and refinement sessions and reports stopped and
  failed counts with each failure named. Single and batch deletion refuse Working,
  Planning, Refining and mission-owning captains and remove the captain's events,
  planning sessions and refinement sessions. Restart resets runtime state in place
  and keeps the captain's identity, configuration, credentials and holds.
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
- **Indexing:** source chunks follow declaration boundaries, embedding clients use
  configured endpoints, captains search their own vessel, duplicate groups have an
  operator report, and dispatch staleness considers source relevance.
- **Persistence and operations:** all database providers preserve delivery and
  ownership fields, validate migration prerequisites, and support expiry and backup
  evidence. Restore checks provider compatibility and retains local secrets.
  Status, inbox, jobs, and logs report bounded evidence and explicit failure reasons.
- **Helm server control:** `server stop`, `server restart`, `reset` and `config init`
  share one authenticated stop through `POST /api/v1/server/stop` and count the
  Admiral as stopped only when its health route no longer answers. A refused or
  unfinished stop exits non-zero, cancels a restart, and blocks every data deletion.
- **Lease, job, and shutdown lifecycle:** a dock lease release removes only the
  zero-count entry it observed, so a concurrent acquire keeps its lease. Sibling
  lease reconciliation takes the per-target lease lock and keeps a lease whose
  holder dock record is missing until the stale-lease grace has passed. A reaped
  background job is cancelled and its terminal status is final in memory and in
  the journal. Admiral stop runs once, waits (bounded) for its background loops
  before disposing the database, and disposes it even when an earlier shutdown
  step fails.
- **Capacity admission:** one global workload reservation covers the active-workload
  count through the durable Assigned write, so parallel assignments in different lanes
  launch no more captains than `MaxConcurrentCaptainWorkloads`. Fleet-capacity
  admission reads the active-work footprint in one query on every database provider
  instead of enumerating retained voyage and mission history.
- **Dashboard and clients:** responsive navigation, scoped controls, complete entity
  forms, structured mission evidence, routing and account controls, endpoint health,
  tool activity, and consistent REST/MCP/WebSocket contracts.
- **Operator entry-point parity:** voyage cancel on REST, WebSocket, MCP and remote
  control runs one operation that stops running missions' agent processes and
  cancels InProgress missions. Batch dock delete refuses active docks per ID like
  single delete. REST mission create merges vessel default playbooks like MCP.
  Batch merge-queue purge is limited to a tenant administrator's own tenant, and
  merge-queue auth refusals name `NotAuthorized` or `Forbidden` to match the status.
  The Helm stdio MCP host supplies mission status transitions.
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
  separately from a timeout.
- **Papercut merge:** the typed-decision merge works on copies of the listed
  groups, so an unavailable answer after an earlier merge returns the original
  groups with their original counts.
- **Documentation:** current contracts replace stale counts, rollout claims, and
  duplicate instructions. Product references are separate from deployment guides;
  the changelog records only the net delta from the upstream merge baseline.
