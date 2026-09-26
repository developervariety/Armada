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
  account failure holds, an optional required-login gate, and Cursor usage from
  saved account API keys with legacy cookie support. Cursor's Composer and Grok
  captains use the Cursor model allowance, while other model IDs use the API
  allowance; the account summary reports partial exhaustion when one pool remains.
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
- The unused `jobs` table and its `idx_jobs_created` index. Nothing read or
  wrote it: long-running Admiral jobs live in the job journal under the data
  directory, and Harbor and landing jobs keep their own tables. A schema
  migration on every provider (SQLite v106, PostgreSQL v109, MySQL v98,
  SQL Server v101) drops the table with any rows it still holds, so upgraded
  and fresh databases end with the same schema.
- The code-index `signatureModel` setting. Nothing read it: file signatures use
  the summarizer model. A settings file that still carries the key loads
  normally, and the next save drops it.

## Changed

- **A consumer suite gets the flaky-test re-run:** when a consumer's unit-test
  suite fails inside the definition-of-done gate and the `flake_score` decision
  recommends it, only the failing classes run again in the same worktree, and
  that re-run is the result. The re-run builder now narrows one existing
  quoted `--filter "EXPR"` in place to `(EXPR)&(classes)`, so a wrapped command
  such as `bash -c '... dotnet test ... --filter "Category!=Integration"'` can
  be isolated; a compound command with no filter to narrow is declined.
- **A planner that committed code is rescued by a Worker:** after a
  `planner_committed_code` failure the automatic rescue is a Worker that
  starts from the planner's commit, with a re-Judge chained. Before, it was the
  same planner persona again, which repeated the failure.
- **Stage skips accept the persona identifier form:** `skipStages` ignores
  spaces as well as case, so `ProductManager` names the stage
  `Product Manager`. Before, the identifier form was refused as unknown.
- **A later pipeline stage re-verifies declared consumers:** when a stage the
  definition-of-done gate does not apply to commits production code, the
  consumer build and triggered consumer suites run again against the branch as
  that stage leaves it. A stage that changed only test files is not re-verified.
  Before, only the Worker stage verified consumers, so a later stage could land
  a consumer break. `DefinitionOfDone.VerifyConsumersAfterLaterStages` turns it
  off.
- **The sibling-tip preflight fact judges only a sibling's own commits:** a
  commit the brief cites is compared with a sibling tip only when that sibling
  holds it. Before, a brief citing the vessel's own commits (a start commit, a
  preserved tip) failed question 11 as a stale sibling.
- **A container stop runs the admiral's shutdown sequence:** SIGTERM is
  handled so the stop reaches `Stop` instead of the runtime exiting as soon as
  the unloading handlers return; before, a container stop killed the admiral
  without stopping agents or disposing the database. The run marker records
  when a stop was requested, and a stop that does not finish is reported as
  `admiral.stop_incomplete` rather than as an unclean exit.
- **The inbox and Ask read failed missions as summaries:** the inbox lists
  Failed and LandingFailed missions from the summary projection, and Ask counts
  missions by status from it. Both read every full Failed row before, with its
  description, diff snapshot and agent output; on a server with about 1300
  failed missions that was near 800 MB of text per inbox poll and raised the
  admiral's memory by 1.5 to 2 GB each time.
- **Consumer verification provisions a sibling's extraction artifacts:** the
  gate's consumer check links each sibling's declared `extractionArtifactPaths`
  from the sibling vessel's host working directory, as a mission dock copies
  them, moving aside a partial tree the sibling worktree already tracks at that
  path. Before, the consumer suite ran without its decompiled trees, so every
  tree-dependent test failed and the gate blamed the producer's change.
- **An admiral run that ends without a clean stop leaves evidence:** each run
  keeps `admiral-run.json` in the data directory with its start, its process id,
  and, every health tick, the time and the managed heap and container memory. A
  clean stop marks it. The next start reports a run with no clean mark in the
  log and as an `admiral.unclean_exit` event, with the last alive time and
  memory, since a kill by the OOM killer or the container runtime runs no
  shutdown code.
- **A Stalled captain with no mission returns to Idle:** the health loop
  releases a captain that has been Stalled for the stall threshold with no
  current mission and no live process, and records `captain.stall_recovered`.
  Before, nothing moved it back and it dropped out of routing until an operator
  intervened.
- **A Slop check with no reviewable work follows the unstamped-check rule:** on
  a voyage that ended before any stage produced reviewable work it is cancelled
  with the reason, like Build and UnitTest, instead of failing and raising a
  High incident about a diff that never existed; on a live voyage it waits.
- **Built-in template defaults reach live servers:** a committed hash history
  per built-in template lets the startup upgrader carry every row that still
  holds an earlier default forward. A unit guard fails when a default changes
  without its hash, and a row edited by an operator is reported once as drift
  instead of being skipped silently.
- **The scheduler skips unadmitted rows before previewing them:** a row whose
  recorded preflight does not admit dispatch is skipped on the record and
  counted as `dispatch_preflight`, without a dispatch preview and without
  spending the sweep's candidate or time bound. Before, each such row cost a
  preview of several seconds, so a sweep reached two of 240 candidates and the
  admitted rows behind them waited for hours.
- **Brief memory is delivered as files:** with brief slimming on, the core
  rules and the mission's retrieved leaves are written into the dock under
  `_briefing/memory/` as files that each fit one read, and the brief's Shared
  Memory section lists them. Memory no longer counts against the instruction
  file's byte budget, so the budget backstop stops eliding a mission's own
  scope, playbooks and description to make room for memory. The new
  `memory_relevance` typed decision sorts retrieved leaves into read-first and
  reference files without removing any.
- **Long mission descriptions are delivered whole:** a description over the
  metadata cap is still embedded as its head and newest handoff block, and the
  full text is written under `_briefing/mission/` as bounded files the elision
  marker names. `captainInstructionByteBudget` defaults to `0`, so the total
  budget backstop no longer elides mission text unless an operator sets a budget.
- **A watcher's reconciliation snapshot reads only linked Checks:** the global
  and scoped snapshots read Check runs by the id of each included voyage and
  mission. Before, they read every Check run created since the oldest active
  record, output included; an old `LandingFailed` mission stayed in the active
  set, so each watcher connect loaded several gigabytes of stored Check output
  and could push the admiral past its container memory limit.
- **A scope keeps its paragraphs in the objective brief:** the objective's
  description renders up to 6000 characters, cut to the space left in the
  brief with the truncation marker, instead of the 1200-character bound of one
  list item that dropped the last instructions of longer scopes.
- **Readiness does not probe a case terminator or a trap as a program:**
  `esac`, `trap`, `wait`, `local`, `readonly`, `declare`, `ulimit`, `umask` and
  `shopt` are shell builtins whose remaining tokens are arguments, so a
  workflow command using them is no longer refused as
  `command_dependency_missing`.
- **The Judge template states the acceptance-criteria grammar the checker
  enforces:** one line per criterion copying its exact text, then MET or NOT
  MET, then evidence on the same line as `path:LINE` or `command: \`...\``,
  with an example line. A test runs the template's own example through the
  acceptance checker, so the instructions and the check cannot drift apart.
  The checker itself is unchanged.
- **A failure while writing mission instructions releases the captain:**
  writing the brief into the dock is part of the launch, so an I/O error or
  an out-of-memory failure while the brief is built takes the launch-failure
  rollback (captain Idle, mission requeued, dock reclaimed, error signal)
  instead of leaving the captain Working with no process until the
  missing-process check marks it Stalled.
- **`armada_mission_status` can return the mission description:** the optional
  `includeDescription` flag returns the stored brief text, which the default
  summary read leaves out.
- **Every typed decision refuses an excluded vessel:** the log watch screen,
  change quality from a supplied diff, the merge-queue leak scan, and inbox and
  board-note triage now pass the vessel to the egress exclusion check, so an
  excluded vessel's content is refused before any provider call and the
  refusal is recorded, as for every other decision.
- **Typed-decision events name their subject:** every decision event and
  retained sample records the objective id and the mission id wherever the
  calling seam knows them, including preflight, prior art at preflight,
  criteria lint, stage necessity, owner digest, dispatch staleness, inbox and
  board-note triage, follow-up routing, log watch, change quality and the
  merge-queue leak scan. The ids are record links only and never enter the
  transmitted state, so the state hash and egress are unchanged.
- **Closing an incident needs a root cause the closer wrote:** REST, MCP and
  the dashboard close dialog refuse an empty cause
  (`incident_root_cause_required`) or the text the incident was opened with
  (`incident_root_cause_unchanged`). The opened text stays in `OpenedReason`;
  a written cause records `RootCauseWrittenBy` and `RootCauseWrittenUtc`.
  Lifecycle and recovery closes are allowed and set `ClosedAutomatically`.
- **The code-index summarizer names no model by default:** its model setting
  is empty until an operator sets one, and the HTTP inference client makes no
  call without a model and logs why. Startup warns when the summarizer is on
  without a model or without a chat endpoint, since an empty base URL sends
  completions to the embedding endpoint.
- **The automated runner owns end-to-end behaviour:** 733 shared end-to-end
  cases repeated an automated-runner case with the same requests and
  assertions, after any extra shared assertion moved to the automated case.
  They are removed, with ten shared end-to-end suites that held only such cases
  and the helpers only they used. The automated copies also run against
  PostgreSQL, MySQL and SQL Server. Shared cases with different input stay.
- **Automated-runner MCP and WebSocket cases carry the checks their shared
  copies made:** MCP entity-create helpers fail on a tool error payload and a
  merge-queue or dispatch job that never finishes, the tool list must name the
  check-run and release tools, every listed tool (not only `armada_` tools)
  needs a description, an input schema and a unique name, the merge-queue tool
  case waits for its job, and the empty mission list must deserialize.
- **One method set per table serves all four database providers:** check runs,
  deployments, deployment environments, releases, workflow profiles, token
  usage, request history, prompt templates, model endpoints, memories, skills
  and project profiles each have a single implementation built on a table
  descriptor (name, key, columns, reader, writer), a scope filter, and a
  provider dialect for first-row, any-row and paged reads. Scope conditions keep
  their text, order and parameter names on every provider, stored forms do not
  change, and no migration runs. Writes that compare and set or span tables (the
  model-endpoint health write, the versioned memory update, memory tags, request
  history detail) stay in their method set. Every paged read binds its page size
  and offset and reads 25 rows for an empty page size, and a creation-time bound
  without a kind reads as UTC on every provider, including PostgreSQL check runs,
  deployments and releases.

- **Dispatch refuses a captain assignment the captain can never take:** a
  `captainAssignments` entry whose captain's `AllowedPersonas`, runtime or tier
  excludes the stage persona, or whose captain is absent, in another tenant or
  benched, is refused before any voyage exists, with the captain, the persona
  and a code such as `captain_persona_not_allowed`. MCP, REST, WebSocket,
  alias and objective dispatch share the check, and the dispatch preview
  reports the same finding. A mission already waiting on such a captain names
  the code in its `mission.requested_captain` event, and a wait no captain can
  ever end records `mission.unassignable_by_construction`.
- **A Mux error event reaches the mission log:** Mux names its event in
  `eventType`, and the structured failure reader recognised only `type`, so a
  Mux provider failure (for example an HTTP 429 from a proxy at capacity) left
  an empty mission log and never reached the throttle detector. The reader
  accepts either field.
- **Stored values written through a shared binder:** every provider writes and
  filters each entity read through a shared column reader through
  `StoredValueBinder`, with one shared writer per row type beside its reader.
  Each provider states how it stores each timestamp column (ISO 8601 text, text
  the server renders from a typed timestamp, a zone-less timestamp, or a
  zone-aware one) and which booleans it stores as integers, and every value is
  sent explicitly typed in its column's stored form. A timestamp without a kind
  is the UTC instant it reads back as. A PostgreSQL zone-less timestamp column
  stores the UTC time whatever the session time zone, the PostgreSQL
  workflow-profile creation-time window compares its bounds as timestamps, and
  the mission admission and model-endpoint health compare-and-set writes match
  the stored update time after every write path. A PostgreSQL database whose
  planning tables predate the planning migration has their text times
  converted to TIMESTAMPTZ (migration 114). After migrations, startup compares
  every timestamp and integer-boolean column the binder writes with the live
  schema and refuses with an "Incompatible schema prerequisite" error that lists
  each mismatched column. Only tables the provider's own schema statements
  (migrations, operational prerequisites and ledgers) create are checked; any
  other table, such as an operator's backup copy, is ignored whatever its name.
- **Stored rows read through shared column readers:** every provider reads
  tenants, users, credentials, fleets, vessels, signals, events, captains,
  missions, mission summaries and history points, merge entries, landing jobs,
  Judge follow-ups, docks, voyages and their playbook selections, objectives,
  objective refinement sessions and messages, planning sessions and messages,
  workflow profiles, Checks, deployment environments, releases, deployments,
  memories, model endpoints, prompt templates, token-usage records,
  request-history entries and details, personas, pipelines and their stages,
  skills, project profiles, playbooks, mission playbook snapshots, Harbor runner
  enrollments and coordination leases, and PostgreSQL and SQLite read
  coordination rooms, participants, messages and claims, through one column
  reader per entity. Columns are read by name. Each
  provider configures only how it stores booleans; a timestamp converts by the
  value the driver returns, so one stored as text or as a zone-less timestamp
  reads as the same UTC instant, with its sub-second digits, whatever the host
  time zone. A required column that is missing, null or unconvertible, a stored
  enum name that is not a defined member where the model has no fallback, and a
  JSON column that holds invalid JSON raise `StoredRowException` naming the
  entity, column and provider instead of reading as a default value; objectives
  and refinement sessions raise their own stored-data exceptions naming the row
  and field, so a list read skips and reports that row. An empty stored
  captain quarantine reason and an empty voyage planning session or message id
  read as none on every provider.
- **Data expiry keeps the records that live only as events:** the newest
  snapshot of every incident, open or closed, and of every runbook execution
  survives the retention cutoff whatever its age, while their older snapshots
  still expire. Objective deletion tombstones and typed-decision reversals are
  never expired. The purge summary names each kept class
  (`kept_incident_latest`, `kept_runbook_latest`, `kept_tombstones`,
  `kept_reversals`, `kept_dispatch_attempts`) with its count.
- **Every CLI captain receives its prompt on stdin:** Claude Code, Codex
  (`exec -`), Gemini and Mux no longer pass the brief as a command-line
  argument, joining Cursor and OpenCode. A brief routinely exceeds the Windows
  command-line limit (32,767 characters, 8,191 through the cmd.exe that runs an
  npm `.cmd` shim), which made a Windows launch fail. Mux had sent the prompt
  both ways and ignored stdin.
- **Every auto-refreshing dashboard page reports a failing load once:** the
  list and detail pages that still reopened the error dialog on each failed
  refresh (among them Dashboard, Server, Incidents, Check Runs, History,
  Request History, Objectives, Vessels, Fleets, Docks and the fleet, dock and
  voyage detail pages) open it once while the load keeps failing and again
  only after a load succeeds. Request History tracks its summary apart from
  its list. The Incidents search box uses the shared search debounce.
- **Shared service and runtime cases carry the checks their unit-runner copies
  added:** twelve shared cases now also assert DataExpiry delete counts, dock
  MCP configs and git anchors, the launch prompt's perform-now wording,
  preserved objective preparation, planning objective lineage, the Judge and
  push-form template rules, the dashboard sibling-path probe, the autonomy
  scripts, and Claude Code `stream-json` output. The twelve unit-runner and
  runtime copies are removed.
- **The shared prompt case for sanitized existing instructions reads the
  generated file:** it read the dock-root `CLAUDE.md` the test itself wrote, so
  it passed even when sanitization was skipped. It now reads
  `.armada/instructions/CLAUDE.md`, and the identical unit-runner copy is
  removed.
- **Unit-runner database and service cases that a shared case already runs
  are removed:** 361 unit cases and 4 runtime cases repeated a shared case with
  the same setup, action and assertions. The shared copy runs in the same gate,
  under the NUnit and xUnit adapters, and against the server database providers.
  Twenty-five unit suites that held only such cases are removed with their
  registrations and shard weights. The shared end-to-end cases and their
  automated-runner copies both stay, because the two harnesses differ in server
  lifecycle and database provider.
- **A captain that ends BLOCKED and keeps running is finished:** the stall
  nudge is withheld and the process is stopped after the terminal-marker grace
  period, so the stage fails with its question instead of being told to
  continue without the owner's answer. BLOCKED is still not a completion
  claim.
- **A stage that ends `[ARMADA:RESULT] BLOCKED` waits for the owner:** every
  persona and mission mode reads a blocked result through one rule: the final
  result or verdict marker at the start of a line is `[ARMADA:RESULT] BLOCKED`.
  The mission fails with a `captain_blocked:` reason that carries the captain's
  question. It does not hand off, its later stages are cancelled, and the voyage
  fails instead of completing. A blocked Judge is not re-run as a missing
  verdict, and autonomous recovery dispatches no rescue. The question reaches the
  owner on an open incident and an owner-addressed board note. A BLOCKED marker
  followed by a later result, or prose that mentions BLOCKED, is not blocked. The
  Architect parser uses the same rule, and the log screen does not read a tail
  that already ends blocked.
- **Mux MCP servers files use the auth fields Mux reads:** a Mux captain's
  scoped `--mcp-config` file and the `armada mcp install` Mux entry write
  `auth` as `type` plus `bearerToken` (or `apiKeyHeader` and `apiKeyValue`).
  Mux ignores unknown auth fields, so the files sent no credential and the
  endpoint refused every Mux captain. The captain tool inventory reads the same
  file through the same model, so its probe sends exactly the headers Mux sends.
- **Missions on tenant-owned vessels carry an MCP credential:** a mission
  whose vessel has no user owner runs as the default user of its tenant, as
  the ownership policy assigns every record without a user. These missions
  launched with no Armada MCP credential, so no captain of any runtime reached
  the Armada tools. The owner must be an active user of the mission's tenant,
  so a user of one tenant never lends its privileges to another tenant.
- **Mux is built against a patched Voltaic:** the admiral image pins Mux to an
  exact commit and builds it against Voltaic with
  `docker/patches/voltaic-mcp-http-client.patch` applied. With the patch, the
  MCP client reads an event-stream POST response and accepts any JSON `ping`
  result, so a Mux captain loads the Armada MCP tools. The image build fails
  when the patch does not apply.
- **Dispatch captain assignments reach the first stage:** a voyage stores its
  `captainAssignments` when it is created, before any mission exists, so the
  root stage's first assignment resolves the named captain as its requested
  captain on plain, alias, REST and WebSocket dispatch. A single-stage dispatch
  stamps its stage persona (`Worker` when no pipeline resolves) on each mission,
  so a `Worker` assignment and the persona's default captain and default
  playbooks apply to it. An admiral without captain-assignment support refuses
  them instead of dropping them.
- **Planning sessions on every provider:** MySQL and SQL Server store planning
  sessions and their transcript messages with the same columns, indexes,
  per-session sequence uniqueness and session-to-message cascade as SQLite and
  PostgreSQL (MySQL migration 103, SQL Server migration 106), so the planning
  routes, tools and captain chat work on all four providers. On SQL Server the
  user reference carries no foreign key, because SQL Server refuses a second
  cascade path from tenants.
- **Named coordination refusal:** on MySQL and SQL Server every coordination
  board operation refuses with `The <provider> database provider does not store
  the coordination board; use SQLite or PostgreSQL.`, which the REST routes return
  as `501`. The automated suite asserts that refusal on those two providers.
- **Parallel pipeline stages keep their submitted order:** each stage stores its
  submitted position (SQLite migration 110, PostgreSQL 113, SQL Server 105,
  MySQL 102), and stages that share an order read back in that position on every
  provider. MySQL and SQL Server returned same-order siblings by stage id.
- **SQL Server tenant and user delete:** the tenant- and user-scoped dock lists
  order by creation time, newest first, like the other providers. They ordered by
  a column docks do not have, so deleting a tenant or a user failed with `500`.

- **Code-index status reads are side-effect free:** the code-index status route
  and tool never clone a missing repository or update the vessel record. The
  status names the case with `RepositoryState` (`Available` or `Missing`), and the
  explicit index update is what clones the repository.
- **The code index follows its settings' data directory:** when
  `codeIndex.indexDirectory` is not set, the index lives in `code-index` under the
  settings' own data directory, not the process-wide default. The runtimes runner
  and the shared-suite xUnit and NUnit hosts redirect the default data directory
  like the other runners, so no test indexes a vessel into the live Armada home.
- **Diff readers share one parser:** `GitDiffPaths` reads hunk bodies by their
  counts and returns each file's one name, line counts and (on request) hunk lines;
  it also reads `--name-status -z` records. Change substance (rescue and planner
  checks, custom-decision `changed_paths`) names a C-quoted non-ASCII file or a
  name holding ` b/` correctly instead of dropping it. The code index reads changed
  names with `-z`, so a change to a non-ASCII file is re-indexed and counts as
  relevant staleness. Auto-land and critical-trigger size limits, the convention
  and dock-boundary secret scans, the banned-diff guard, the leak and
  change-substance model states, and the Judge diff stat count and scan an added
  line whose content starts with `++`. The change-substance state labels each hunk
  with its own file, and the Judge review diff elides a bulk data file whose name
  is quoted or holds ` b/`.
- **Dock boundary hooks:** the pre-commit and pre-push hooks read changed names
  with `-z`, so a protected path with a non-ASCII name (`_briefing/**`,
  `**/CLAUDE.md`) is blocked at commit and push time, and they read added lines
  per hunk, so a `+++` file header is skipped and an added line starting with `++`
  is scanned.
- **Merge-queue conflict files:** the conflicted-file list is read before
  `merge --abort` (NUL-separated, so names arrive unquoted), so the failure
  classifier and the trivial-conflict recovery decision see the real files instead
  of an always-empty list.
- **Provider limits have one definition:** the captain bench decision and the
  crash-loop classifier read the same provider signatures. A throttle (HTTP 429,
  "too many requests") or an overload (HTTP 529, "overloaded") at a process exit
  benches the captain and re-routes the mission instead of failing it and halting
  its dependents; with no published retry time it takes the configured default
  backoff. Status codes count only in a status form, and credit, billing and
  authentication signatures are provider phrases, so a crash that mentions a pool
  capacity, a billing type, a process id, a line number or a filesystem
  permission stays a crash and reaches crash-loop quarantine. The terminal-exit
  "captain unavailable" check uses the same authentication rule.
- **Test failures keep their rescue:** autonomous recovery reads only the first
  line of a definition-of-done `Compile` or `TestFail` reason for its
  environmental and human-review markers, so a failing test named for
  authorization, quota or approval, or an assertion expecting `403 Forbidden`,
  no longer blocks the rescue.
- **Merge-queue host faults:** a merge-queue test run that the definition-of-done
  classifier reads as host trouble is classified `InfraTestFailure` and surfaced,
  not sent to a recovery captain. Every other post-merge test failure is surfaced to
  the operator as well and is never routed to a rebase captain.
- **Secret checks at landing:** the manifest-digest exemption covers only the
  digest, so a secret-shaped run beside a digest on a manifest line fails the
  landing scanner; the auto-land convention audit applies the same exemption and
  stores a CORE_RULE_5 violation with the secret replaced by `<redacted>`.
- **Typed-decision status:** the operator status view reads the one no-key
  effective-mode rule the settings use.

- **Process groups for AgentWake, self-deploy and Helm build steps:** AgentWake
  agent runs, self-deploy native commands and Helm `server start` build steps own
  a process group, so a timeout or cancellation also kills a background child that
  left the process tree. The bounded runner resolves the executable before it
  uses the group launcher; a missing or non-executable file starts without the
  group and fails to start exactly as it does without one.
- **Event types match their emitters:** `voyage.completed` is recorded once when
  the voyage completion rule writes a voyage Complete, `captain.stopped` when a
  captain stop stops the agent process, and `voyage.dispatched` also for rescue
  voyages and for voyages created without missions through REST or WebSocket.
  The known event type list names the stall evaluator's `captain.stall_confirmed`
  and `captain.stall_cleared` and drops `mission.created`, `captain.stalled` and
  `voyage.created`, which nothing writes; a unit test compares the list with the
  event types the source writes. The WebSocket generic-event example names types
  that are broadcast.
- **Regression objective links in the caller's scope:** a Check run, import or
  import-update and an incident create or update read `RegressionObjectiveId`
  with the caller's scope, like every other id in the body, and refuse an
  objective the caller cannot read.
- **Shutdown waits for the Harbor job expiry loop:** stop cancels the loop and
  waits for it with the same bounded wait as the health and endpoint-health loops,
  so an expiry pass never reads a disposed database.
- **Dashboard refresh cost and error dialogs:** Incidents, Check Runs and History read
  their name lookups (vessels, environments, deployments, releases, workflow profiles,
  objectives) once when the page opens; the refresh timer reloads only the list. On
  Captains, Prompt Templates, Users, Tenants, Credentials, Events, Signals, Merge
  Queue, Missions and Voyages, a failed load opens the error dialog once; while loads
  keep failing it stays closed after it is dismissed, and it opens again for the next
  failure after a successful load. The mission-history and token-usage charts share
  one time-label helper.
- **Dashboard controls for global administrators:** the workspace terminal and the
  check-run Command Override field show only to a global administrator, since the
  server runs either for no one else. The Credentials page offers no copy for a
  masked token (another user's credential) and does not send the mask back as the
  token when that credential is saved.
- **Dashboard check run page:** opening another run (the "Compared to" link, Retry)
  clears the previous run and shows the loading state; only the newest load writes,
  a failure to read the new run is reported, and a change event for another run no
  longer merges into the run on screen. A background reload failure stays quiet.
- **Dashboard Requests page:** a refresh keeps the selected requests it still returns,
  and the route, status code, principal, credential, tenant and user filters query the
  server once the user pauses typing, not once per keystroke.
- **Dashboard Events, Signals and Merge Queue column filters:** a column filter or a
  column sort covers every record, not only the loaded page. While one is active the
  page reads every record under its server filters and filters, sorts and pages them
  in the browser, as the Missions page does; without one it reads one server page.
  Missions and these three pages keep the selection to the rows their filters show.
- **Dashboard table selection:** a table's selection holds only rows its current
  search and filters show. Select-all takes the filtered rows; a row that a filter
  hides or a refresh no longer returns leaves the selection, so "Delete Selected"
  and other bulk actions never act on a row that is not on the list. Captains,
  Prompt Templates, Users, Tenants and Credentials use the shared table state
  (`useResourceTable`) with the other full-list tables, so the rule holds on each.
- **Dashboard server-paged lists:** Missions, Signals, Events, Merge Queue, Voyages,
  Memories, Check Runs, Incidents and Requests share one paging state
  (`useServerPaging`). When a reload finds the list now ends before the page on
  screen (rows deleted or expired), the page moves to the last page and reloads it
  instead of showing an empty table past the end.
- **Dashboard diff viewer:** the file list and per-file view read Git's diff headers by
  the same rules as the server: C-quoted names (non-ASCII or special characters) show
  the real name and open their own pane, a name containing ` b/` keeps it, and a rename
  or deletion is named from its rename and `---`/`+++` lines. Hunk lines are read by
  the hunk counts, so the `\ No newline at end of file` marker no longer shifts the
  gutter numbers and content that starts with `---` or `+++` is counted and numbered.
- **Dashboard pager:** the page-number box follows every page change the page makes
  (a filter change, a page-size change, a shrinking list), not only Prev and Next, on
  every paged table.
- **Image rebuild helper:** `rebuild-local-image.sh` passes `ARMADA_CLI_REFRESH`
  (digits only) as the Dockerfile's `CLI_REFRESH` build argument, so a rebuild can
  refresh the agent CLIs without editing the Dockerfile, and its behavioural test
  covers the `GIT_SHA` build argument the helper already sends.
- **Judge follow-up empty text:** every provider reads an empty optional Judge
  follow-up text column (tenant, user, voyage, vessel, merge entry, suggested
  follow-ups, audit notes, recommended action) as null, as it does for other
  records. The database runner's follow-up case checks this on every provider.
- **Scoped mission lists apply every filter:** tenant- and user-scoped mission
  lists apply the status, voyage, vessel, captain and mission filters on every
  provider; they applied only the creation-time filters and returned the rest
  of the tenant's missions. Full and summary mission lists at every scope use
  one filter set per provider. The database runner checks each filter at every
  scope, full and summary, on every provider.
- **Mission summary reads on every provider:** PostgreSQL, MySQL and SQL
  Server read Mission-shaped summaries (mission lists, the MCP and WebSocket
  mission reads, voyage status counts) without loading the description, diff
  snapshot or agent output, as SQLite did; they read full rows and discarded
  the text. Tenant- and user-scoped summary lists apply the voyage, vessel,
  captain, mission and status filters on those providers too. The SQLite
  summary read returns stage order and the review fields. All four providers
  share one summary column list. The database runner checks the light fields,
  the filters, the grouped voyage counts and that no heavy column is loaded,
  on every provider.
- **Database runner coverage for landing jobs and Judge follow-ups:** the
  database runner round-trips landing jobs (create, read by id and merge
  entry, state list, update, reopen, delete) and Judge follow-ups (upsert,
  the pending and unassociated lists, single association, audit completion,
  update, reopen) on every provider, comparing every property.
- **Coordination board storage:** the SQLite participant heartbeat is one
  insert-or-update statement, so two first heartbeats for the same key no
  longer race to a unique-constraint failure. Extending a participant's active
  claims returns the number of claims extended on SQLite and PostgreSQL; it
  returned 0 every time. The database runner has round-trip cases for
  coordination rooms, participants, messages and claims; MySQL and SQL Server,
  which do not store the board, report each case as a named skip.
- **SQL Server captain delete:** SQL Server clears the signal references to a
  captain and deletes it in one transaction, and clears only the references to
  captains the delete's tenant or user scope matches. A scoped delete that
  matches no captain, or a delete a referencing row refuses, leaves every
  signal reference in place. The database runner checks this on every
  provider.
- **PostgreSQL playbook empty text:** PostgreSQL reads an empty playbook
  tenant, user or description, and an empty snapshot playbook id, description,
  resolved path or worktree path, as null, as the other providers do. The
  database runner has a playbook round-trip case that checks this on every
  provider.
- **PostgreSQL runner enrollment compare-and-set:** a Harbor runner
  re-enrollment that expects a generation above zero updates the existing
  inactive row or is refused. It no longer inserts a new enrollment when the
  row is gone, which matches SQLite, MySQL and SQL Server.
- **PostgreSQL skill and project profile timestamps:** skill and project
  profile timestamps (stored as text) read through the shared PostgreSQL UTC
  reader, which honours the stored offset, so a host outside UTC no longer
  shifts them by its offset. The creation-time filters on both lists cast the
  stored text to `timestamptz` before comparing, instead of comparing text with
  a timestamp. The database runner has a create, read, update, reopen,
  creation-window and delete case for skills and for project profiles that
  compares every property on every provider.
- **PostgreSQL timestamps read as UTC:** objective `CreatedUtc` and
  `LastUpdateUtc`, model endpoint timestamps and Judge follow-up timestamps read
  through the shared PostgreSQL UTC reader, so they carry `DateTimeKind.Utc` as
  on the other providers and serialize with a `Z`. The database runner checks
  the kind and value of the objective and model endpoint timestamps after a
  reopen.
- **Protocol markers:** `[ARMADA:RESULT]` and `[ARMADA:VERDICT]` markers count
  only at the start of a line, after optional leading whitespace, for every
  reader: the completion claim used by no-op detection, refusal classification,
  the Architect `BLOCKED` result, the handoff-outcome check, the Judge verdict and
  its follow-up label, and progress parsing. A marker mentioned mid-line, glued to
  other text or wrapped in formatting is not a marker. A completion claim is one
  of the exact terminal values (`COMPLETE`, `PASS`, `FAIL`, `NEEDS_REVISION`).

- **Token usage input buckets:** every runtime records input tokens as three
  separate buckets, counted the same way for every provider: uncached input,
  cache-read input and cache-write input. A record's input is their sum and its
  total is input plus output. Records carry a counting-rule marker; rows and
  stored events from before the split read as the legacy rule, keep their stored
  counts and are never rewritten. Token-usage summaries on REST, MCP and the
  dashboard total each bucket from bucketed records only and report legacy input
  and the legacy record count apart. Every token count on `token_usage` is a
  64-bit integer on every provider. Migrations: SQLite 109, PostgreSQL 112, SQL
  Server 104, MySQL 101 (nullable bucket columns; existing counts widened to
  BIGINT in the same version).

- **Runtime provider failures:** every runtime writes one provider failure record,
  `[ARMADA:ACTIVITY] <runtime> error <message>`, for an error event, a failed turn
  (Codex `turn.failed`) and a terminal result that reports an error (Claude Code
  `is_error`, Cursor `is_error`, Gemini `status: "error"`), and the API-endpoint
  runtime writes it for a failed inference call. The provider's message is kept,
  redacted and bounded. Chat, planning and refinement fail the turn on this record
  from any runtime and record it as the failure reason; it never joins the reply.
  The displayed mission log never filters it out.
- **Runtime event parsing:** a tool argument whose value is not text reads as
  absent, so one mistyped argument no longer turns a Claude Code, Cursor or
  OpenCode event into its raw JSON line (tool output included) and no longer hides
  a protocol marker in the same event. Gemini and Mux streamed assistant text is
  joined into whole lines before it becomes a record, so the mission log holds
  one record per line instead of one per token, and a marker split across stream
  events still starts its own line; an unfinished line is written at the next
  tool, error or terminal event, or at process exit.

- **Voyage cancel reports alike on every surface:** REST, WebSocket and MCP
  cancel a voyage through one shared operation that writes one
  `voyage.cancelled` event and broadcasts the voyage and each cancelled mission
  (MCP broadcast nothing, and no surface wrote an event). MCP no longer stops a
  captain's process separately before the recall, which already stops it.
- **The same audit events on REST and MCP:** dock delete, purge, repair, unstick
  and batch delete, event delete and batch delete, merge entry purge and batch
  purge, and captain batch delete write the same `dock.*`, `event.*`, `merge.*`
  and `captain.batch_deleted` events on MCP as on REST, from one definition
  (MCP wrote none).
- **One vessel delete:** REST, WebSocket and MCP delete a vessel through one
  shared operation. Every surface cancelled live missions in the database only,
  leaving their agents running; the shared delete cancels them through the
  mission cancel, which recalls the captain. WebSocket cancelled fewer statuses,
  kept the missions (pointing at a deleted vessel) and removed worktrees with a
  raw directory delete; every surface now deletes all of the vessel's missions,
  purges docks through the dock service, removes the dock directory under the
  configured docks root (MCP used a fixed home path without the name check), and
  writes `vessel.deleted`.
- **One bare voyage create:** a voyage created with no vessel or missions goes
  through one shared create on REST and WebSocket. WebSocket dropped the
  selected playbooks; REST created the voyage before it checked the selections,
  so an unknown playbook left a cancelled voyage behind. Both now resolve the
  selections first and refuse an unknown playbook (REST `400`) without creating
  anything, and the new voyage is broadcast.
- **One mission metadata update:** REST `PUT /api/v1/missions/{id}`, WebSocket
  `update_mission` and MCP `armada_update_mission` change only the fields the
  request names. REST and WebSocket cleared an omitted title or description and
  ignored `Persona`; WebSocket stored a dependency on a mission that did not
  exist; MCP read the dependency outside the caller's scope. Every surface now
  checks a changed dependency or parent in the caller's scope and broadcasts the
  change.
- **One mission diff reader:** REST, WebSocket and MCP read a mission diff through
  one reader: the saved diff file, then the stored diff snapshot, then the live
  worktree. MCP read the mission's summary row, which carries no snapshot, so it
  reported no diff for a mission whose worktree was reclaimed. REST returns `404`
  when there is no diff instead of an error body under HTTP 200.
- **One captain create and update:** REST, WebSocket and MCP create and update
  captains through `CaptainAdministrationService`, which owns the name rule,
  runtime-option normalization for whole-body writes, and model validation
  (including an endpoint change on MCP updates). WebSocket skipped validation and
  normalization, so it created an API-endpoint captain with no model endpoint
  that REST and MCP refuse. The route tests that asserted on the handler source
  text are replaced by behavioural tests of the shared rule.
- **A halted voyage stops its running captains:** when a mission fails without
  recoverable work, its voyage is halted through the one voyage cancel, so the
  captain of every parallel mission still running is recalled (its agent process
  stops and the captain is released) before the mission is written Cancelled.
  The halt used to cancel the missions in the database only, leaving their
  processes running and their captains Working.
- **A retried landing decides its voyage:** REST `retry-landing` and MCP
  `armada_retry_landing` apply the voyage completion rule after the landing, so
  a Failed voyage whose failed work a retry later lands becomes Complete and
  raises the completion hook however old the failure is (the periodic sweeps
  revisit a Failed voyage only for 24 hours, so an older one stayed Failed).
- **One merge cancel that never rewrites a finished entry:** REST
  `POST /api/v1/merge-queue/{id}/cancel` (new), the cancel branch of
  `DELETE /api/v1/merge-queue/{id}`, WebSocket `cancel_merge` and MCP
  `armada_cancel_merge` run one shared cancel. A Landed, Failed or Cancelled
  entry keeps its outcome and completion time (WebSocket and MCP rewrote a
  Landed entry to Cancelled), an unknown entry is refused instead of reported
  cancelled, and a cancel writes `merge.cancelled`. `MergeQueueService.CancelAsync`
  itself leaves a finished entry unchanged, and the merge entry terminal set has
  one definition, `MergeStatusRules.IsTerminal`.
- **One mission restart, and LandingFailed is not restarted:** REST, WebSocket and
  MCP restart run one shared operation whose eligibility lives in
  `MissionRestartService`. A LandingFailed mission is refused on every surface
  and the refusal names retry-landing (WebSocket restarted it and discarded its
  branch and commit). Every surface records a restart signal owned by the
  mission's owner, writes `mission.restarted`, and broadcasts the change. REST
  refuses with `409` instead of a `400` body under an HTTP 200, and rejects an
  unreadable restart body instead of ignoring it.
- **One mission and voyage purge:** mission purge, batch mission delete, voyage
  purge and batch voyage delete run one shared rule on REST, WebSocket and MCP.
  A mission a captain is working is refused (mission purge had no guard, and MCP
  and WebSocket deleted the running captain's worktree). Every surface removes a
  purged mission's dock record and worktree through the dock service and its
  ownership guard, and deletes its log files and saved diff (REST left all of
  them behind); every surface writes the `mission.deleted`, `voyage.deleted` and
  batch events (only REST did).
- **One mission cancel:** REST `DELETE /api/v1/missions/{id}`, WebSocket
  `cancel_mission` and MCP `armada_cancel_mission` run one shared cancel. A
  Complete, Failed or Cancelled mission is refused and keeps its outcome (all
  three rewrote it to Cancelled). A running mission's captain is recalled, which
  stops its agent process (REST left it running; WebSocket released the captain
  in the database only). Stages waiting on the cancelled mission are cancelled
  with it, and every surface writes `mission.cancelled` and broadcasts the change.
- **One session log reader:** mission and captain log pages read the same way on
  REST, WebSocket and MCP. Every surface resolves a mission's newest non-empty
  sidecar log when its canonical log is empty, filters runtime noise, clamps the
  page, counts lines alike, and redacts secret-shaped values; the WebSocket
  `get_mission_log` and `get_captain_log` commands returned them unredacted.
- **Tenant scope for ids in request bodies:** voyage and mission create, mission
  update, vessel create and update, vessel build-context, merge-queue enqueue,
  incident create and update, and planning-session create read every record their
  body names by id with the caller's scope, as they read a path id, and return
  `404` (`400` for an incident create) for a record outside it; the MCP and
  WebSocket create surfaces and the MCP `armada_update_mission` tool apply the
  same rule. The checked ids include a vessel's `FleetId`, a mission's `DockId`
  and an incident's `RegressionObjectiveId`. Caller-less paths read linked
  records only inside the owning tenant: mission dependencies, Judge follow-up
  association, incident lifecycle evidence, and fleet and captain default
  playbooks.
- **User and credential privilege:** a tenant administrator manages the users of
  its own tenant except a global administrator and a protected user; changing,
  deactivating, deleting, resetting the password of, or minting, changing or
  deleting a credential for either returns `403`. Credential reads return the
  bearer token only to a global administrator and to the credential's own user;
  every other caller receives `********`, and a non-global caller's credential
  update keeps the stored token. The creation response still returns the new
  token once.
- **Shutdown authentication by default:** `RequireAuthForShutdown` defaults to
  `true`, so `POST /api/v1/server/stop` and `/restart` require a global
  administrator unless a deployment turns the setting off. Helm sends its
  configured credential, so local stop and restart keep working.
- **Host details and server-wide routes:** `GET /api/v1/doctor` shows the settings
  path, database location and runtime binary paths only to a global
  administrator; other callers receive the same checks without them. The Mux
  runtime routes are global-admin only. Production summary lane time counts, for
  a caller that is not a global administrator, only lanes whose every vessel
  belongs to the caller's tenant.
- **Ordinary-user reads and writes:** an ordinary user's recent signals, a
  captain's signals, vessel git status, the vessels of a fleet, the missions of a
  voyage and the active missions in workspace status are limited to its own
  records, as its lists are. Workspace file writes and token-usage
  delete-by-filter require a tenant administrator, like the vessel and event
  writes beside them. Approve, deny, restart and status-transition signals carry
  the mission's tenant and user. A tenant administrator no longer reads the
  captured requests of a global administrator in its tenant.
- **Enumerate routes:** every `POST .../enumerate` takes the permission level of
  the `GET` list it mirrors, so an ordinary user enumerates playbooks, workflow
  profiles, environments, objectives, backlog and every other listable
  collection it may already list.
- **Foreign ids read as missing:** for a caller that is not a global
  administrator, dispatch preview describes another tenant's captain override and
  pipeline override exactly like a missing one, and Harbor runner enrollment and
  revocation answer another tenant's credential or runner exactly like a missing
  one (an enrollment another tenant holds reads only as `runner_already_enrolled`).
  A deployment environment cannot move to a vessel in another tenant, and deleting
  an objective with no row purges only the caller's own tenant's orphan snapshots.
  update, vessel build-context, merge-queue enqueue, incident create and update,
  and planning-session create read every record their body names by id with the
  caller's scope, as they read a path id, and return `404` (`400` for an
  incident create) for a record outside it; the MCP and WebSocket create surfaces
  apply the same rule. Caller-less paths read linked records only inside the
  owning tenant: mission dependencies, Judge follow-up association, incident
  lifecycle evidence, and fleet and captain default playbooks.
- **Pipeline writes:** REST, MCP and WebSocket create, update and delete a
  pipeline through one service, so a payload is validated, defaulted and stored
  the same way on each. MCP `create_pipeline` and `update_pipeline` accept
  `requiresReview` and `reviewDenyAction`, and a replacement stage list keeps
  each omitted stage field from the existing stage for the same persona, so an
  update no longer turns a review gate off by leaving it out. A stage list without
  orders runs in list order on every surface; REST and WebSocket stored such a
  list with every stage at order 1, which ran them all as parallel siblings. MCP
  accepts `order`, so it can define parallel stages too, and a list that orders
  only some stages is refused. A missing name, an empty stage list (create or update) or a stage
  without a persona is refused on every surface, and a duplicate name returns
  `409` instead of a database error. REST and WebSocket read only the allow-listed
  fields, so an id, tenant, built-in flag or timestamp in the body is ignored. A
  global administrator's update or delete by name reaches its own tenant's record
  before another tenant's.
- **Objective writes:** REST objective and backlog routes and the MCP objective
  and backlog-item tools create, update and delete through the same objective
  service methods. MCP `update_objective` and `update_backlog_item` pass every
  field the service reads, so `autoDispatchEnabled` is no longer dropped, and the
  clearable text fields declare `emptyStringClears`, so `""` clears them on MCP as
  it does on REST. A missing title returns `400` on REST instead of a server
  error; a missing objective is `404` and a linked record outside the caller's
  scope is `400` (it read as `404`). MCP refusals carry `Outcome`, and
  `delete_objective` and `delete_backlog_item` return a tool error for an unknown
  id instead of a protocol error. New MCP tool `delete_backlog_refinement_session`
  matches `DELETE /api/v1/objective-refinement-sessions/{id}`; both remove the
  session from its objective's links through one method.
- **Persona writes:** REST, MCP and WebSocket create, update and delete a
  persona through one service. REST and WebSocket read only the allow-listed
  fields, so an id, tenant, built-in flag or timestamp in the body is ignored,
  and they accept `DefaultPlaybooks` on create and update (an array, or the JSON
  text a stored persona carries); an update through them had ignored it. A
  missing name or prompt template is refused on every surface instead of
  creating a persona named `Worker`, and a duplicate name returns `409`. A global
  administrator's update or delete by name reaches its own tenant's record
  before another tenant's. Deleting a built-in pipeline or persona returns `400`
  on every surface; a tenant administrator of another tenant got `404` on REST.
- **Workflow profile writes:** REST and MCP create, replace, delete and validate
  a workflow profile through one service. A REST replace keeps
  `EnvironmentVariables` instead of dropping them, and both surfaces trim ids and
  commands on create and replace. Validation answers exactly what a create would
  do for the same caller: a global administrator's profile belongs to the tenant
  it names, else the tenant of its fleet or vessel, so REST validate, MCP
  validate and create agree. A blank name returns `400` (MCP: code `invalid`)
  instead of a server error.
- **Validate routes do not reveal other tenants' records:** workflow-profile and
  project-profile validation (and create) read fleets and vessels within the
  caller's scope, so another tenant's fleet or vessel reads exactly as one that
  does not exist. Project-profile validate needs a tenant administrator, as its
  create does.
- **Playbook writes:** REST and MCP create, update and delete a playbook through
  one service. `PUT /api/v1/playbooks/{id}` changes only the fields the body
  names, as `update_playbook` does; it had replaced the whole record with model
  defaults, so a body naming only `FileName` failed. REST create reads only the
  allow-listed fields, so a body id or timestamp is ignored. A non-`.md` file
  name or missing content returns `400` (MCP: code `invalid`) instead of a server
  error, and a duplicate file name returns `409` (MCP: code `conflict`).
- **Prompt template writes:** REST, MCP and WebSocket create and update a prompt
  template through one service. MCP `update_prompt_template` no longer creates a
  missing template (it skipped the create checks); it returns code `not_found`,
  as REST returns `404`. MCP create trims the name and category as REST does and
  refuses blank content. REST create requires `Category` instead of defaulting it.
  Updates on every surface find the template through the caller scope and apply
  the edit rule, and accept `Category` and `Active`; an empty `Description`
  clears it.
- **Built-in personas and pipelines:** only a global administrator may change a
  built-in persona or pipeline, because every tenant uses them; a tenant
  administrator of the tenant that stores them receives `403`.
- **Health loop isolation:** each admiral health-check sub-step and each of the four
  background sweep triggers runs as its own isolated step. A step that throws is
  logged and counted by name, and dispatch, the captain pool, voyage completion,
  escalation and every sweep still run in the same cycle.
- **Coordination board fleet notes:** `[fleet]` notes for mirrored event types come
  from the event store itself, so admiral, mission-service and landing events reach
  the board. Voyage dispatch records a `voyage.dispatched` event.
- **Settings reload:** `crashLoopDetection` and `definitionOfDone` reload in place,
  so the crash-loop tracker and the definition-of-done gate use the new values
  without a restart. The gate is always constructed and records a "disabled" skip
  while off. `codeIndex.stalenessSweepIntervalCycles` and the model-endpoint health
  interval also follow a reload.
- **Disk lifecycle:** passes never overlap; a second pass waits for the running one.
  A folder that cannot be listed is logged and reported by path in the report's
  `Errors` and `ErrorCount` instead of reading as nothing to reclaim.
- **Authentication and scope:** REST, MCP, WebSocket, chat, planning, and captain
  launch paths apply caller ownership. Captains receive scoped credentials;
  administrative tools and cross-tenant events remain restricted. Server-owned
  captain fields and write-only secrets have consistent write contracts.
- **Host command execution:** a check-run `commandOverride`, retrying an imported
  check, and `POST /api/v1/workspace/vessels/{id}/exec` run as the server
  process, so they are global-admin only and refused with `403` for every other
  caller before any process starts. Check-run writes require a tenant
  administrator, and `Deploy` and `Rollback` checks run only through the
  deployment workflow.
- **Check-run links and gates:** check-run run and import refuse a mission,
  voyage or deployment outside the caller's tenant. The voyage, Judge, manual
  completion, supersession and recovery readers count only Checks in the gated
  record's tenant, so another tenant cannot write evidence into a gate.
- **Vessel server paths:** `LocalPath`, `WorkingDirectory` and a local-path or
  `file:` `RepoUrl` are set only by a global administrator on REST, MCP and
  WebSocket; other callers are refused on create and keep the stored paths on
  update. A vessel name must be one safe path segment, and vessel removal deletes
  the dock directory only for such a name.
- **Dispatch and pipelines:** operator and scheduler paths share objective defaults,
  admission, and Check arming. Root missions resolve and persist explicit or
  inherited start commits, return `MissionStartRefs`, and record resolution events.
  Alias dependencies, parallel-stage barriers, review gates, report-only modes,
  and cancellation use consistent lifecycle rules.
- **Voyage completion:** one rule decides when a voyage becomes `Complete` or
  `Failed`. Mission state changes, the health-loop completion check and the
  landing drain all apply it, so they reach the same answer for one voyage.
  Each path raises the voyage completion hook exactly once for each voyage it
  ends; the mission path did not raise it before. A `Complete` or `Cancelled`
  voyage is never rewritten by completion. A decision on a held review under a
  cancelled voyage leaves the voyage `Cancelled`. A `Failed` voyage moves only
  to `Complete`, when its failed work has since landed and its Checks are green.
  It is never written `Failed` again and never reopens. A Pending or Running Check holds completion on every path, and a
  failed Check fails the voyage on every path. A `PullRequestOpen` mission and a
  `WorkProduced` mission held for operator review keep their voyage open.
- **GitHub objective refresh:** re-importing a `Completed` or `Cancelled`
  objective keeps its status; a merged pull request no longer moves it back to
  `Released`. An explicit `StatusOverride` still sets any status.
- **Captain assignment commit:** assignment claims the captain with its
  compare-and-set first and records the dock on the mission only while the mission
  is still Assigned. A lost claim returns the stored mission to Pending with no
  captain, dock or new branch (`WaitingForIdleCaptain`, logged as
  `captain_claim_lost`) and deletes the provisioned dock. A mission that changed
  status keeps that status. Every assignment failure path, including an unresolved
  start ref, a failed dock provision and a failed base check, releases the captain
  only while it still records that mission, so a captain another mission claimed
  meanwhile stays Working. The database drivers carry no transaction wrapper.
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
- **Persona minimum tiers:** a persona carries an optional minimum capability
  tier in place of the specialist flag. Dispatch, the dispatch preview and
  model-list health combine it with the mission's request by taking the higher
  tier, and no request is capped below what it asks for. Existing specialist
  flags become a Premium floor at migration (Test Engineer: Standard). Persona
  writes that still send the retired `specialist` flag are refused as
  `specialist_retired` on REST, MCP and WebSocket instead of being ignored.
- **Dispatch hold:** automatic Checks obey the hold. The heartbeat runs no
  Pending Check while the hold is engaged, records one named deferral event per
  engagement, and runs the waiting Checks after the hold clears. Operator check
  runs are not held.
- **Stall recovery:** a stalled captain's process is registered as superseded
  before the heartbeat stops it, and the process-exit handler ignores that exit
  and any exit from a process that is no longer the captain's recorded process,
  so a stall kill is never read as a failure or an out-of-memory kill and never
  requeues or reclaims a relaunched mission. The terminal-marker stop uses the
  same registry. A relaunch is written back only while the mission keeps its
  status and captain; otherwise the relaunched process is stopped, the captain
  is released, and `captain.recovery_abandoned` is recorded. While the dispatch
  hold is engaged a stalled captain is stopped and not relaunched: the mission
  returns to Pending, the captain is released,
  `captain.recovery_deferred_dispatch_hold` is recorded once per mission and
  engagement, and assignment skips the mission until the hold clears.
- **Per-mission process state:** a mission's streamed output buffer,
  final-message artifact, first terminal marker and heartbeat throttle are owned
  by the launch that started the mission's current process. Each launch takes
  ownership and starts that state empty; a process exit releases it only while
  the exiting process's launch still owns it. A superseded or stale process's
  late exit, or the exit whose handling relaunched the mission, never clears the
  relaunched process's output or terminal marker.
- **Child processes:** one bounded runner in Core runs short-lived child
  processes. It reads both output streams at once, keeps each within a byte budget
  (beginning and end, with a marker and a count of the omitted bytes), kills the
  process tree on a timeout or a caller cancellation (and the process group for
  shell commands), and gives the readers a bounded drain window after exit or kill,
  so a background child holding a pipe cannot hang the call. `run_command`,
  merge-queue git and test commands, and self-deploy native commands use it.
  Containment differs by platform (see the next bullet).
- **Escaped descendants on Linux:** a group-owning run tags every process it
  starts with a run-unique `ARMADA_CONTAINMENT_ID` in its environment. A timeout
  or cancellation, after the group and tree kill, kills every process of the same
  user that still carries the identifier, so a descendant that called `setsid`
  and double-forked out of the tree and the group dies with the run. Processes
  without the identifier are never signalled; the result names the processes
  killed, the ones it could not read, and an incomplete sweep. macOS still lets
  such a descendant escape (its environment is not readable), and on Windows only
  the live tree is killed.
- **Git processes:** every admiral git command (GitService, pinned anchors, code
  index, workspace, branch writes, readiness, vessel routes, dock seeding and hook
  lookup, landing ref reads, ref audit, disk cleanup, check checkouts, the Slop
  check, git inference, diagnostics) and `gh`/`glab` runs through the bounded
  runner with prompts and the pager off. A hook or warning flood on stderr no
  longer blocks git until its timeout. Network git that had no bound (readiness and
  vessel-route fetch, dock seed push) uses the git timeout. The Slop check fails
  with a named reason when the reviewed diff is larger than 64 MiB instead of
  reading part of it.
- **Check and probe commands:** check runs, Definition-of-Done commands, workspace
  exec, Mux CLI calls, the Docker probe, runtime MCP listings, account login probes,
  version and `command -v` probes, and notifications run through the bounded
  runner. A caller cancellation kills the command instead of leaving it running,
  check and gate commands own their process group, and a failing-test set read
  from truncated output is marked incomplete.
- **Wake, daemon and Helm build output:** AgentWake agent runs and the Helm
  `server start` build steps (server build, `tsc`, `vite`) run through the bounded
  runner, so a child that fills its stderr pipe, or fills it before reading its
  input, no longer hangs the wake or the CLI; a build step that never finishes is
  killed after 15 minutes. The OpenCode server the admiral launches has both output
  streams drained at once into a fixed per-stream budget instead of being held
  whole for the life of the daemon. Helm's MCP client install and remove commands
  run through the bounded runner too, with a 2 minute kill, and `server start` on
  Unix sends the Admiral's console streams to `/dev/null` (the Admiral writes its
  own log), so the server never stops on a full pipe nobody reads.
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
- **Dispatch preview role coverage:** the preview reports the captains assignment
  could choose by calling the assignment rules themselves: the mission's tenant,
  quarantine deadlines, Smart Routing persona routes, the requested captain from
  an override or the persona's default captain with its fallback tier floor, and
  a pinned model no captain runs as a tier floor. A requested captain whose persona
  allow-list, runtime capability, or minimum tier excludes the persona is never
  assigned; assignment falls back at its tier and names the reason.
- **Policy refusal continuation:** the alternate captains for a mission continued
  after a policy refusal are the captains assignment could choose: quarantine,
  including a quarantine deadline still in the future, the mission's tenant, the
  Smart Routing persona routes when Smart Routing is on, and the assignment selector. A pinned model that no alternate runs is a tier floor, and a
  captain below the pinned model's tier is not approved because it runs the model.
- **Unassignable missions:** the unassignable-by-construction check counts a
  captain only when the assignment selector could choose it, so a captain that runs
  the pinned model below the persona's minimum tier does not count. A mission whose
  Smart Routing persona routes admit no captain of its tenant that allows its
  persona is named `mission.unassignable_by_construction` and escalates to an
  incident, as a mission with no such captain at all does.
- **Settings reload:** the manual reload endpoint and the settings-file watcher share
  one reload path bound to the server's own settings file. Settings updates and both
  reloads share one candidate validator for account key paths and captain runtime
  bindings. A missing, unreadable, or invalid file is refused and the current
  settings stay live.
- **Live scheduler settings:** the autonomous objective scheduler reads and writes
  the live `autonomousObjectiveScheduler` settings section on every use, so a
  settings-file edit to its enabled flag, pause, interval, ceilings or fair-share
  flag takes effect on the next sweep, and a later scheduler tool change writes the
  edited values back instead of older copies. The branch cleanup cadence and
  preserved-ref retention also hot-reload.
- **Runtime execution:** inference-endpoint captains resolve endpoint credentials;
  API-endpoint missions have bounded command execution and deterministic conversation
  compaction, Anthropic stable-prefix caching, and provider cache/reasoning usage
  counters. Large output keeps diagnostic content and archives its full form.
  Runtime logs expose command and failure context, and launch configuration delivers
  the correct scoped MCP credential for each supported runtime.
- **Mux captain MCP delivery:** a Mux mission or chat launch passes its own
  Armada server file with `--mcp-config` and `--strict-mcp-config`, because
  `mux print` loads MCP servers only from that flag. The captain's config
  directory (`--config-dir`, or `~/.mux`) keeps selecting its endpoints and
  settings, so captains that share a config directory no longer share MCP
  servers, and a launch sets no `MUX_CONFIG_ROOT` variable, which Mux never read.
- **Mux captain tool inventory:** a running Mux mission captain's inventory
  lists the MCP servers its launch delivers through `--mcp-config`, derived from
  the launch plan, instead of the config directory's servers that `mux print`
  never loads; its Armada probe presents the credential the launch resolves for
  the same mission, and a Mux CLI that cannot start no longer hides the
  delivered servers. It always lists a Mux Built-In Tools entry whose tool
  calling flag, base URL and adapter come from the endpoint the launch selects in
  `endpoints.json`, and it states that the built-in tool count and names are not
  reported by `mux --version`, because only `mux probe`, which calls the
  provider, reports them.
- **API-endpoint tool bounds:** `run_command` owns a process group and kills it on
  timeout or cancellation, including children the shell left behind, and caps
  output while reading instead of after a whole line. `edit_file` and `multi_edit`
  refuse empty search text, observe cancellation, and list at most 20 candidate
  lines; each `multi_edit` step must be unique in the content earlier steps produced.
- **Agent process identity:** every stop, kill and liveness path for a local agent
  process (runtime stop and liveness, the captain health check, orphaned-mission
  recovery, the liveness heartbeat, autonomous recovery, planning-session cleanup)
  goes through one identity-checked lookup that disposes its handle. A live
  process whose start time differs from the recorded launch, or is later than the
  mission's launch, is a reused PID: it reads as gone and is never killed
  (`process_identifier_reused`). A live process with no recorded launch in the
  current admiral process, or an unreadable start time, reads as running but is
  never killed (`process_identity_unverified`). Each agent's start time is stored
  next to its process ID on the captain and mission records
  (`process_started_utc`, migrations SQLite 108, PostgreSQL 111, SQL Server 103,
  MySQL 100) and restored at startup, so an agent launched before a restart is
  still verified and stopped; a row stored without a start time stays unverified.
  Runtime stop returns a result (stopped, not running, or refused with a named
  reason); a refused stop is logged and recorded as `captain.stop_refused`, and the
  terminal-marker stop no longer reports it as a stop. Stop sends no shutdown
  request: a 3-second grace period, then a tree kill. A cancelled launch starts nothing, and a launch that fails after start
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
- **Unreadable objective rows:** every provider reads objective list and document
  fields and enum columns through one shared rule. Malformed JSON or an undefined
  enum value is a read error that names the objective and field, never an empty
  list or a default that the next update would write back. Lists and the snapshot
  backfill skip such a row with a warning that counts and names it, and the
  scheduler never dispatches it.
- **Unreadable refinement session rows:** every provider reads the objective
  refinement session status through one shared rule. An unknown stored status is a
  read error that names the session and field, never a `Created` session; lists
  skip such a row with one warning that counts and names it.
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
- **Background jobs page:** the dashboard Jobs page and REST `GET /api/v1/jobs`
  and `GET /api/v1/jobs/{id}` read the Admiral's long-running jobs (dispatch,
  code index, merge processing, disk lifecycle and similar) from memory and the
  job journal, newest first, and require a global administrator, as
  `armada_job_status` does. The list counts journal records it could not read.
  The job-table service that nothing enqueued into, and
  `POST /api/v1/jobs/{id}/cancel`, are removed; long-running jobs have no cancel.
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
- **WebSocket voyage dispatch:** `create_voyage` with a vessel and missions runs the
  shared voyage dispatch service that REST and MCP run, and accepts the same fields:
  captain assignments, pipeline, playbooks, code-context settings, objective and
  forced preflight, and stage skips. A captain override sent over WebSocket is
  stored on the voyage and routes the persona's missions, and the code-index gate,
  code-context preparation and dispatch hold apply as on REST. A refusal returns the
  shared code, the REST status and the full refusal body.
- **Harbor owner lookup:** the runner session registry awaits the durable owner
  lookup for the handshake, heartbeats, launches, stops and runner events, so a slow
  database never blocks a thread. One lookup is bounded by
  `Harbor.OwnerLookupTimeoutSeconds` (default 5); a lookup past the bound is refused
  as `runner_owner_lookup_timeout`, and a failed lookup as `runner_owner_unavailable`.
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
- **MCP tool catalog:** pairs each objective tool with its backlog-named twin
  and states that both call the same operation.
- **Path containment:** one rule decides that a path is inside a root: it is the
  root itself or lies below the root plus a directory separator, compared
  ordinally after normalization. The server and proxy dashboards, check-run
  artifact collection and summary parsing, Workspace browsing, captain runtime
  configuration writes, Harbor directory mapping, dock occupant listing, and the
  API-endpoint workspace tools all apply it, so a sibling directory whose name
  starts with the root's name is outside. Prestaged-file copies, read-context
  staging and disk-lifecycle deletion apply it too, so a directory whose name
  differs from an allowed root only by letter case is not deleted. Self-deploy
  and build-drift reporting share one rule for which vessel holds the running
  server; it compares letter case the way the host's file system does.
- **Papercut merge:** the typed-decision merge works on copies of the listed
  groups, so an unavailable answer after an earlier merge returns the original
  groups with their original counts.
- **Dashboard loading:** a list refresh keeps the bulk selection for rows that still
  exist and drops only rows that vanished. Every read used as a complete set
  (picker options, id-to-name lookups, counts and filters over the whole set) reads
  every page through one `listAll` client wrapper per entity, so pickers and names
  stay complete past the server's page cap. A release lists its linked deployments
  through the release filter and names each linked check run by reading that run.
  A vessel's mission table shows the newest 1000 missions and says so when the
  vessel has more. Every list page whose query follows a filter, search, page or
  sort, and every detail page that follows its route id, applies only its newest
  load: a filter change, an auto-refresh tick, a manual refresh and a reload all
  start a load, and only the newest load writes the rows,
  totals, selection, spinner and errors, so a slower, older response never replaces
  a newer one. The Memories search sends one request with the final text after
  typing pauses, through a shared debounced-value hook, instead of one request
  per keystroke. A detail page shows the spinner and any load failure when the id
  changes and never shows the previous record. The Planning page reads the session
  list and the open session again after a reconnect or an event gap, and applies
  session, message and captain events that arrive while the open session is loading
  on top of the loaded session instead of dropping them. Only the current
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
- **Commit secret gates:** the server landing gate and the dock pre-commit and
  pre-push hooks judge a line alike. Every PEM private-key header (bare, RSA, EC,
  DSA, OPENSSH, ENCRYPTED) is blocked. A pattern the server matches without case
  carries an `(?i)` prefix in `.armada/boundary.patterns`, and the hook matches it
  with `grep -i`, so `Bearer` and `Password` are caught at commit time. The hook
  entropy gate strips `=` padding before measuring, as the server does.
- **Typed-decision redaction:** decision state loses every key shape the display
  redactor removes before it leaves the host: labelled secrets, Bearer tokens,
  provider tokens by prefix (now also `github_pat_`, `xox*-` and `glpat-`, which
  the display redactor removes too), and the string value of a property named as
  a secret. Both redactors apply one rule set. `DecisionStateRedactor.Version` is
  3, so samples redacted under earlier rules form their own cohort.
- **Typed-decision egress guard:** one guard decides whether decision state may
  leave the host, and every sending path asks it: the adapter skeleton, custom
  decisions, the captain tools, and the standalone adapters `prior_art`,
  `memory_review`, `papercut_merge`, `memory_candidate`, `followup_routing`,
  `preflight`, `criteria_lint` and `inbox_triage`. A state about an excluded
  vessel or naming an excluded marker is not sent and records
  `egress_excluded_vessel` or `egress_excluded_content`; in a multi-item pass the
  other items are still decided.
- **Egress exclusion covers objectives:** a typed decision whose state concerns
  an objective that lists an excluded vessel in its `VesselIds` sends nothing and
  records `egress_excluded_vessel`, even when the resolved target vessel is
  allowed. The objective-scoped decisions (`preflight`, `prior_art` at
  preflight, `criteria_lint`, `stage_necessity`, `owner_digest`,
  `dispatch_staleness`) pass the objective's and the target's vessels to the
  shared guard.
- **Typed-decision kill switch:** the global `typedDecisions.mode` stops the
  captain tools too. With it `Off`, the general tool, the list tool, the
  pre-shaped helpers and the custom runner make no provider call, record one
  event, and return `unavailable` with reason `typed_decisions_off`
  (`typed_decisions_no_key` when no key resolves). `captainTool.enabled: false`
  still turns the tools off on its own.
- **Documentation:** current contracts replace stale counts, rollout claims, and
  duplicate instructions. Product references are separate from deployment guides;
  the changelog records only the net delta from the upstream merge baseline.
