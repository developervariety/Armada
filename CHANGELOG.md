# Changelog

All notable changes to Armada are documented in this file.

---

## Unreleased

This section is the fork delta on top of v0.9.0. It includes fork-only
capabilities and isolated upstream hunks that were still missing after the
first absorb. Fork-owned routing, the coordination board, autonomy, recovery,
and the code index stay richer than the upstream copies; those were not
replaced.

Focus: operator signal fidelity - make a failure say what actually failed.

### Vessel updates keep what the form does not edit

- `PUT /api/v1/vessels/{id}` keeps the stored tenant, user, creation time,
  auto-land calibration count and last reflection mission. Before, an update
  wrote them from the body, so a normal save reset the tenant and user to null.
- Vessel create and update accept the `autoLandPredicate` object under a key
  in any letter case. Before, the route bound the body to the vessel model
  before the handler ran, so a PascalCase key returned `400`.
- Dashboard vessel saves send the vessel as loaded with the form changes
  applied, so protected paths, sibling repositories, default playbooks and
  thresholds survive a save. Auto-land rules are edited as the vessel's
  `autoLandPredicate`. The copied flat auto-land fields the server never
  stored were removed; before, each save cleared the stored predicate.
- Workspace context saves use `PATCH /api/v1/vessels/{id}/context`. Before,
  they sent a PUT with only the context fields, which reset the rest of the
  vessel.

### Vessel auto-land and landing mode display

- Vessel detail shows the configured auto-land rules from the stored
  predicate: on with its file, added-line and path limits, off with the rules
  it keeps, not configured, or a stored predicate that cannot be parsed.
  Per-vessel Definition of Done controls were not imported; Definition of Done
  stays a global setting.
- The vessel form states what the selected landing mode does, from one shared
  list of modes. Local Merge merges into the managed repository and
  fast-forwards the working checkout without a push; Default uses the global
  mode, and a voyage landing mode takes priority over the vessel.

### Captain assignment for inherited pipelines

- A voyage captain assignment with persona `*` (or an empty persona) now applies
  to every step that has no exact persona assignment; an exact assignment still
  wins. Mission captain resolution and the objective dispatch preview use the
  same rule. Before, both matched persona names exactly, so a wildcard
  assignment was stored and ignored.
- Dispatch with an inherited pipeline offers one "All steps" captain assignment
  row that sends the `*` persona.

### Incident recovery detail

- An incident that names a mission shows that mission's recovery report from
  `GET /api/v1/missions/{id}/recovery`: recovery attempts against the budget,
  an exhausted budget, landing retries, the failure reason, and each rescue
  mission as a link with its status and commit. Truncated sections and
  unavailable reasons are stated, and a load failure is shown instead of an
  empty report. Upstream's per-incident failure kind and rescue count were not
  imported; the mission recovery report is the richer source.

### Dock starting point

- Dock detail shows the Git evidence captured at provisioning: the provisioned
  commit, target branch and tip, the snapshot state (Seeded, Complete or
  Incomplete), recent commits on the mission's paths, paths not on the
  provisioned revision, external source trees, and prior-art search results.
  An incomplete snapshot shows its sanitized error code, truncation and
  resolution error; a dock without a snapshot says so. The snapshot is context
  evidence, not a landing gate.

### Mission modes in the dashboard

- The mission create form offers Implementation, Audit (read-only) and Research
  (read-only), and mission detail shows the stored mode. The server already
  stores the mode sent on create; an API case now pins that contract.

### Mission markdown

- Mission detail renders the mission description as GitHub-flavored markdown,
  with a "Copy raw markdown" button that copies the stored source.
- The shared log viewer takes a `markdown` option; the mission instructions view
  uses it. Copy still copies the raw text. The mission log stays plain text,
  because runtime logs are event lines, not markdown. Raw HTML in markdown is
  not rendered as elements.

### Readable captain logs

- Captain detail reads its log with `formatted=true` and shows typed entries by
  default. Thinking, tool call, tool result and status entries carry a label, so
  they stay distinct from answer text; redacted and truncated entries carry a
  chip, dropped noise is hidden, and a page cut at the server limit says so.
  "Show Raw" returns the redacted raw text view. Kinds are runtime
  observations, not mission outcomes.
- The mission log viewer uses the same readable entries by default, keeps
  follow mode and line-count selection in both views, and copies the raw text.

### Bounded dashboard home

- The home page reads the 10 newest mission summaries instead of 200 full
  missions, and reads each active voyage's vessels from
  `GET /api/v1/voyages/{id}/mission-summary`. Before, a voyage whose missions
  were older than the recent slice showed no vessel.
- Home refresh uses one path: WebSocket events, the timer and the refresh button
  fold into at most one follow-up load while a load runs. Before, every event
  started another full reload. The timer is now the shared auto-refresh
  selector (default 30 seconds, "None" stops it). A mission's full JSON is read
  only when opened.
- `GET /api/v1/status` adds `MissionsWaitingForResourcePressure`: the current
  number of pending missions whose stored assignment state is
  `WaitingForResourcePressure`. The home Active Voyages card shows it when above
  zero. It is not a count of refused admission checks and does not reset on
  restart.

### Events carry their owner's scope

- The generic admiral and server event helpers, the architect over-cap event
  and papercut events now write the owner's tenant and user, so tenant admins
  and members see them in `GET /api/v1/events` and scoped reports. Before, those
  events had no tenant or user and only an unscoped administrator saw them.
- One shared rule (`EventOwnerScope`) resolves the owner from the mission, then
  voyage, vessel, captain and the event's entity. Copied tenant and user
  assignments in the landing handler, recovery orchestrator, landing retry and
  lifecycle events were replaced by it. Event types, messages and broadcast
  payloads are unchanged; earlier events are not backfilled.

### Mission auto-land detail

- `GET /api/v1/missions/{id}/auto-land` returns the vessel's current auto-land
  predicate, the latest recorded auto-land decision (outcome, merge entry,
  redacted reason, predicate at decision time) and the latest merge entry audit
  fields in the caller's scope. It evaluates no predicate and reads no diff; a
  malformed or tied latest decision reads `Unavailable`. The DoD report now uses
  the same shared history state, and its DoD-specific enum was removed.
- Scoped merge entry reads on SQLite and SQL Server (tenant, and tenant plus
  user) now apply the mission, vessel and status filters. Before, a tenant admin
  or member listing one mission's merge entries also got other missions' entries.

### Shared, ownership-safe captain quarantine

- Manual quarantine and release use one scoped service for REST, MCP and the
  dashboard. New `POST /api/v1/captains/{id}/quarantine` takes a required
  reason and an optional expiry or duration; `unquarantine` now goes through the
  same service and returns a typed `CaptainQuarantineResult` (Quarantined,
  Released, NotQuarantined, Busy, NotFound, InvalidRequest).
- The hold is a conditional database write on all four providers: it applies
  only while the captain is Idle or already held and owns no mission, dock or
  process. Before, a manual bench cleared a running captain's mission, dock and
  process, and a release forced any captain, including a Working one, to Idle.
  The REST release no longer writes captain state directly, and the id-only
  `BenchAsync`/`UnbenchAsync` and whole-row `ClearQuarantineAsync` service
  methods were removed in favor of the scoped API.
- The expiry sweep and quota probe release only a hold that is still timed, so
  an indefinite operator hold placed while a probe runs is no longer cleared
  from the sweep's earlier read.
- A provider spend cap no longer strips work from other captains on the capped
  model: the failing captain and idle siblings are held, and a busy sibling keeps
  its mission, dock and process until its own run returns the cap.
- The captains list shows the hold reason and expiry and offers Quarantine and
  Lift Quarantine; Captain Detail adds Quarantine. Both use one dialog (reason
  plus a duration, an expiry, or until released) and report the server's typed
  outcome, including Busy and NotQuarantined.

### Mission recovery detail

- `GET /api/v1/missions/{id}/recovery` returns recorded recovery counters
  against the current rescue budget, landing retries, rescue missions, linked
  incidents with their runbook executions, and recent recovery events, in the
  caller's scope. It reuses the scoped incident and runbook services, redacts
  and bounds free text, dispatches nothing, and never reads a rescue's Complete
  status as a landing.

### Landing records carry the mission owner's scope

- The landing retry event (`mission.landing_retry`) now carries the mission's
  tenant and user, so the mission owner's scoped reads and the recovery report
  include it. A failed retry-event write is logged instead of silently
  discarded; retry authorization and landing gates are unchanged.

- The landing handler now writes the mission's tenant and user on the merge
  entry it enqueues and on all of its landing events (pull request open,
  already integrated, auto-land triggered and skipped, enqueued, completed,
  no commits, landing failed, branch cleanup failed). The landing-drain safety
  net also records the merge entry user and scopes its events. Merge-queue
  processing events (target advanced on failure, branch cleanup failed, target
  ref sync skipped, working directory synced or skipped, bare HEAD restored or
  skipped) now carry the entry user as well as its tenant. Before this, a
  scoped (non-admin) read of the mission's own merge entry or landing events
  returned nothing. A merge entry carries a tenant only when the mission and
  vessel share it, because queue processing reads both the vessel and the
  mission in the entry's tenant; otherwise the entry stays unscoped as before.
  The landing-drain safety net follows the same rule (it previously used the
  mission tenant, then the vessel tenant). Deleting a user or tenant now also
  deletes the merge entries attributed to it. Records written before this
  change are not backfilled and remain visible only to unscoped administrators.

### Definition-of-done evaluation history

- Mission completion records each definition-of-done result as a scoped
  `mission.definition_of_done_evaluated` event with a typed, versioned
  payload: Passed, Skipped, NotVerifiable, Failed or EvaluationError, with
  captain, dock, branch, known commit, attempt count, times, and redacted,
  bounded failure output. A failed event write is logged and does not change
  the gate outcome or the mission decision.
- `GET /api/v1/missions/{id}/definition-of-done` returns the configuration of
  the gate mission completion actually runs, or reports that no gate is active
  (a settings reload does not replace a gate built at startup), as the gate
  resolves it (enablement, persona and doc-only
  rules, selected workflow profile and scope, command presence without command
  text, consumer settings) and the latest recorded evaluation in the caller's
  scope. Reading it runs no gate and no diff. No record reads `NotRecorded`; a
  malformed, unknown-version or same-timestamp latest record reads
  `Unavailable` and never falls back to an older result.

### Skipped definition-of-done results

- The mission activity log records a skipped definition-of-done gate
  (disabled, persona not applied, doc-only marker) as `validation skipped`
  with its reason. It no longer writes `validation passed` for a gate that ran
  no build or test. Completion policy is unchanged: a skipped gate still
  accepts the work.

### Effective landing previews

- Landing previews share the handler's voyage, vessel, global and legacy
  configuration resolution. Responses expose the selected source and flags.
  Unreadable voyage overrides report unavailable configuration. Check summaries
  are redacted and bounded; Failed status with exit code zero is not Passed.
  Existing execution gates retain authority.

### Recorder stage in the ProductDevelopment pipeline

- The built-in `ProductDevelopment` pipeline now ends with a `Recorder` stage, so
  finished product work distils its durable findings into native captain memory.
  The stage runs at the `mid` tier so it never competes for the scarce high-tier
  specialist and Judge captains, and it produces no commit by design.
- The no-op completion gate and the ineffective-rescue gate now exempt the
  `Recorder` persona the same way they exempt the `Architect`: a persona whose
  deliverable is not a repository diff is not read as a false-complete. The
  `Recorder` also runs detached, like the reviewer personas, because it reads the
  finished work and commits nothing. Only the `ProductDevelopment` pipeline gains
  the stage; no other built-in pipeline changes.

### Retired three unused specialist pipelines

- Retired the `FrontendWorkflowTested`, `MigrationDataTested`, and
  `PerformanceMemoryTested` pipelines, which had no vessel default and no use. The
  fleet routing example and the routing fixture no longer define them, so they are
  no longer created or reconciled. Their reviewer personas and prompt templates
  stay defined, so an operator can still compose an ad-hoc pipeline from them.
  Existing pipeline rows in a live database are operator data and are retired
  separately.

### Consumer test gate on behavior-breaking waves

- The definition-of-done gate now RUNS a declared consumer's unit-test suite,
  not only its build, when the producer change can break the consumer's
  behavior. "Can break" is decided from the producer diff against its default
  branch: the consumer suite runs when a changed non-test file falls under a
  triggering path prefix, taken from the producer's sibling declaration
  (`ConsumerTestTriggerPaths`) or the `DefinitionOfDone.ConsumerTestTriggerPaths`
  default. A change to tests, docs, or outside every prefix builds the consumer
  but runs no suite, so the producer pays for the consumer suite only on the
  changes that matter. A failing consumer suite fails the gate with the named
  reason `consumer_tests_failed: <consumer>`, distinct from a consumer build
  failure. `DefinitionOfDone.RunConsumerTests` (default true) switches it off.

### Recorded mission admission

- Persist the actual global workload and resource-pressure decision in mission
  details and summaries. Reads do not run admission checks. Revision checks
  reject stale writes; a refusal and its waiting state are written together.
  Preserve Unicode IDs, existing policy limits and process ownership.
### Removed the learned-facts / Reflections implementation

- Removed the fork learned-facts and Reflections memory feature. Native captain
  memory (the Recorder persona and the Recorded pipeline) is the replacement and
  is unchanged.
- Deleted the reflection dispatcher, the reflection memory service and its
  bootstrap, the reflection sweeper, and the whole learned-facts library
  (learned-facts file, memory anchors, pack and habit miners, the reflection
  output parser, and the curate candidate helpers).
- Removed the four learned-facts MCP tools: `armada_consolidate_memory`,
  `armada_accept_memory_proposal`, `armada_reject_memory_proposal`, and
  `armada_check_stale_memory`.
- Removed the `MemoryConsolidator` persona seed and the `Reflections` and
  `ReflectionsDualJudge` pipeline seeds. Inactive learned playbooks and any
  existing reflection pipeline rows remain as data for an operator to retire.
- Removed the learned-facts settings (the enable flag, thresholds, token
  budgets, curate settings and prune options) and the `[LEARNED-FACT-PROPOSAL]`
  mission guidance. The native "Recall Existing Memory" guidance stays.
- Removed the reflection-threshold vessel routes and MCP arguments and the
  fleet curate MCP arguments.
- Reflection-coupled database columns (the vessel reflection and reorganize
  thresholds, the last-reflection anchor, the per-scope curate thresholds and
  learned-playbook references, and the pack-hint table) are retained as inert
  data. No destructive migration is added; an operator retires the leftover
  rows separately.

### Captain brief AI-Memory repository folder

- Match a vessel to its folder under the AI-Memory `repos/` directory by
  reducing both the vessel name and each folder name to lower-case letters and
  digits, and use the folder's real name. A folder whose name carries
  separators (`some-vessel` for vessel `SomeVessel`) now reaches the brief and
  the deferred-facts lookup instead of silently resolving to nothing.
- When two or more folders reduce to the same name, choose none and state the
  ambiguity in the brief.
- When no folder resolves, the brief says why, and the admiral logs the reason
  once per vessel per process (Info for no matching folder, Warn for an
  ambiguous match or a memory root that cannot be probed). An unreadable root
  still does not fail the dispatch.
### Native captain memory: store

- Added a native captain memory store: a `Memory` record (`mem_` prefix) with a type
  (Episodic, Semantic, Procedural), an optional topic, an optional stable key, a one-line
  summary, content, a salience used to order recall, a version counter, provenance
  (source kind plus voyage, mission and vessel identifiers), a vessel association, and tags.
- Records are owned by a tenant and a user and carry a scope (tenant-wide or user-specific).
  Storage reads, updates and deletes are fenced by tenant, and a key is unique inside a
  tenant, so two writers cannot create the same key at once.
- An update applies only at the version the caller read, so a concurrent change is refused
  instead of overwriting the other writer.
- New migrations add the `memories` and `memory_tags` tables: SQLite 86, PostgreSQL 87,
  MySQL 78 and SQL Server 81. No existing table changes shape.
- Native memory holds captain working memory. An external durable memory rule delivered in
  a mission brief still wins over a native record on conflict.

### Native captain memory: service, MCP tools and REST API

- Added `MemoryService`: caller-scoped create, read, update, delete and search. A caller
  sees the tenant-wide records of its own tenant plus its own user-specific records, and
  never a record of another tenant. Only a tenant or global administrator changes a
  tenant-wide record.
- Recall orders by salience, then by recency, and filters by type, topic, vessel, voyage,
  mission, creation date and free text over content, summary, topic, key and tags.
- A write that names a key updates the record holding that key. A key held by a record the
  caller may not change is refused as a conflict, and the refusal does not disclose that
  record. A write may carry the version it read, so a concurrent change is refused instead
  of overwritten.
- Validation keeps a record a distilled finding: content is required and bounded, and keys
  and tags are lowercase slugs, so one record is addressable by the same string on every
  database provider.
- New MCP tools `search_memory`, `get_memory`, `create_memory`, `update_memory` and
  `delete_memory`, plus a `memories` entity type on `armada_enumerate`. The MCP surface
  carries no per-request identity, so the tools act as an administrator of the default
  tenant and reach no other tenant. A refusal returns a code an agent can act on: invalid,
  not_found, forbidden or conflict.
- New REST API under `/api/v1/memories`: list and search, create or update by key, read,
  change and delete, with 409 on a conflict.
- Native memory is independent of the learned-facts feature and reads none of its settings.

### Native captain memory: the Recorder persona and memory recall

- Corrected the MCP catalog size in the operator guide and removed the stale tool count from
  the MCP guide. The guide now states how the number is counted, so it can be re-measured.

- Added the built-in `Recorder` persona with its own editable prompt. It reviews the
  finished work of a voyage, classifies what is worth keeping, reconciles it against what
  is already recorded, and writes it to native memory. It writes memory only: it changes
  no repository file, no vessel model context, and no shared external memory repository,
  and it hands anything that belongs in shared memory to the operator as a proposal.
- Added the built-in `Recorded` pipeline: Worker, then Recorder. It is seeded only when no
  pipeline of that name exists, so an operator pipeline with that name is kept as it is.
- No existing pipeline gains a Recorder stage. Where the Recorder belongs in a pipeline is
  an owner decision, so startup changes no other pipeline.
- Every other built-in persona template now carries a Recall Existing Memory section that
  tells the agent to read the vessel model context and search memory before acting, to
  treat a record as working memory rather than proof, and to let a rule from the brief's
  Shared Memory section win over a memory record on conflict. Startup adds that section once
  to a built-in persona template that lacks it and changes nothing else, so an operator edit
  is kept. The Recorder and the learned-facts memory consolidator do not receive it.

### Judge Check gate on queued armed Checks

- Treat an armed, not-yet-run voyage Check as queued work once the voyage has a
  commit under review. The Judge gate holds the PASS for it and the voyage
  completion gate holds completion, instead of reading it as "no green
  independent Checks attached" and rejecting a valid PASS.
- Stamp such a Check with the branch and commit the Judge reviewed before the
  gate classifies it, so it measures the reviewed tip even when no stage in the
  voyage committed anything new.
- A failed Check at the reviewed commit still rejects a PASS, a green for
  another commit still does not count, and fully report-only (Audit or
  Research) voyages still need no code Checks. One rule in `CheckRunGateRules`
  decides this for both gates.

### Landing past a worktree that holds the target branch

- Create the LocalMerge integration worktree detached at the target tip, so a
  worktree elsewhere that has the target branch checked out no longer blocks
  every landing for the vessel.
- Advance the target branch by a compare-and-swap from the tip the merge
  started at. A target that moved during the merge is reported as drift and
  retried, never overwritten. Push modes push the merged commit to the named
  target branch.
- Report a git refusal with its own words and a failure class:
  `worktree_conflict:` names the blocking worktree path, and other refusals
  read `integration_merge_failed:` with git's message, instead of the generic
  "Integration worktree merge failed".

### Historical PostgreSQL upgrade repair

- Convert the known operational text timestamps, integer duration and approval
  fields, and captain foreign-key delete rule before prerequisite validation.
- Add `--validate-database` to apply and check schema changes without starting
  Admiral services or dispatch. Use an isolated restore for deployment preflight.
- Reject ambiguous or incompatible source data and roll back the complete repair.
  Keep applied migration history and the original prerequisite checksum intact.

### Resource admission evidence

- Capture typed pressure reasons, evaluation time, limits and the OOM deadline
  in the policy decision without running the policy again. Persistence follows
  separately after the historical PostgreSQL upgrade repair.

### Admission refusal handling

- Honor a refused resource-pressure decision even when its explanation is empty.
  Keep the mission waiting before captain selection and use a fallback message.

### Readable runtime log responses

- Add typed text, thinking and tool entries to formatted captain and mission logs,
  with explicit display limits and omission flags.
- Keep legacy text responses and the shared redaction entry point. Protect tool
  names, raw REST/MCP log output and escaped JSON credential forms.

### Durable dock Git anchors

- Record the actual provisioning commit and reuse bounded typed evidence during
  prompt generation. Keep old missing evidence unavailable.
- Pin repository queries to that commit. Preserve ownership during conditional
  completion and clear evidence on dock reuse or reclaim.
- Append provider migrations and preserve PostgreSQL/MySQL dock user ownership.

### Scoped voyage mission summaries

- Add a read endpoint for voyage mission status counts and paged distinct vessel
  IDs. Counts cover all visible missions, including missions outside the vessel
  page, without loading descriptions or captured output.
- Apply authenticated user, tenant administrator or global administrator scope.
  Reject invalid page bounds. Dashboard adoption remains separate work.

### Backend metadata persistence

- Preserve nullable captain tier, mission requested captain and tier, vessel scan
  preferences, and voyage planning provenance across provider reloads.
- Append schema versions without changing applied history. Retain PostgreSQL's
  existing integer scanner column and reject restricted MySQL provenance encoding.
- Add non-default create/update/clear/reopen cases and interrupted migration proof.
  Routing selection, process ownership and active landing protections are preserved.

### Test discovery and provider persistence

- Register 12 existing Unit suites and eight existing API suites. Retain the
  existing executables and reject missing registrations, empty runs, duplicate
  identities and unmatched filters. Record named skips and build source evidence.
- Correct exception assertions that could accept their own missing-exception
  failure. Keep completed results when suite setup or teardown fails.
- Preserve review requirements when expanding pipelines, retain terminal review
  docks until approval, and resolve relative deployment health URLs correctly.
- Isolate proxy assets and Git fixtures; verify request-history cleanup and wait
  for captured records before asserting or deleting them.
- Persist six vessel preview settings on all four providers through new additive
  migrations. Reject incompatible columns, including restricted MySQL text
  encodings. These settings describe previews; existing landing gates still apply.

### Upstream preservation gates

- Repair server startup prerequisites and pending DDL without rewriting applied
  migration history. Add catalog checks, schema locks and MySQL statement recovery.
- Preserve Unicode identifiers and full-value MySQL tenant uniqueness with native
  unique indexes over an internal tenant key and the complete original value.
- Restore missing user-scope read mappings, SQL Server Mission retry exclusions,
  PostgreSQL deployment boolean binding and MySQL native timestamp parameters.
- Add isolated fresh, interrupted, historical-upgrade and concurrent-startup
  fixtures, plus full-value Unicode uniqueness and lease lifecycle tests.
- Make first-boot identity creation atomic, reject incompatible pre-staged
  SQL Server corrections, and prevent concurrent SQLite migration replay.
- Keep tied-time tenant pages stable and restore MySQL captain provider reads.
- Build shared test dependencies in sequence before parallel suite execution.


- Add a fixed four-provider migration manifest and a source check that rejects
  changed history, reused numbers and altered initial SQL inputs.
- Add repeat-startup and non-default Mission persistence cases to the existing
  database runner. Raise schema checks to the preserved fork baseline.
- Record fresh-install failures for three server providers and unresolved
  field mappings in the [foundation checkpoint](docs/upstream-review/foundation.md).
  The initial failures are retained as baseline evidence; provider repairs are
  described separately. No deployment is claimed.
- Remove stale instructions that described the retired standalone lead as an
  available operator tool. Keep generic wakes and bounded helpers.

### Routing V2

- Preserve omitted vessel and voyage bindings during mission metadata updates; reject explicit rebinding or clearing. API tests now isolate their capacity fixtures and report failed creates directly.
- Replace legacy preference overrides with opt-in persona account routes and usage reserves. Missing persona routes wait unless an explicit default is configured.
- Add optional account usage collection for Codex, Claude, Cursor, and OpenCode Go, plus file and manual snapshots. Keep persona preferences until allowance runs low; reserve capacity for important work and queue missions when no approved account is available.
- Add Dashboard policy editing, usage status, budget planning, and an admin draft preview API. Defaults contain no accounts or personal subscription data. See [usage routing](docs/USAGE_ROUTING.md).

### Retired standalone lead integration
- Archived the standalone lead launcher, timer, deployment assets, and guides.
- Removed the Grok listener, OAuth proof-of-concept broker, lead configuration,
  lead-control REST routes, and lead-cycle MCP tools. Existing deployments must
  remove their old listener, gateway, and lead-specific wake configuration.
- Preserved the objective scheduler, shared coordination, generic AgentWake,
  helper launcher, watcher, and generic MCP authentication and audit controls.

### Operator documentation
- Incident mitigation now acknowledges existing failure evidence. Lifecycle
  reconciliation reopens a mitigated incident only for a newer failure.
- Added a fixed-source upstream capability review, full change inventory and
  phased selective-integration plan. Corrected the README comparison and
  captain MCP guidance. The review does not claim that planned ports shipped.
- Autonomous objective sweeps now preflight candidates only as capacity needs
  them. Each pass has candidate and elapsed-time limits, resumes after its last
  examined objective, and stops as soon as fleet capacity is full. Scheduler
  status now reports active, completed, progress, bounded, and error state, and
  concurrent triggers coalesce into one follow-up pass.
- Autonomous recovery no longer lets old, already-handled failures consume the
  ten-item sweep budget. New failures behind a large handled backlog now reach
  their incident, follow-up, or rescue policy in the same sweep.
- Autonomous recovery now rebuilds its review graph from the owning objective's
  selected pipeline. Recovery keeps specialist stages, the reviewed start tip,
  mission mode, review settings, and immutable playbook context, and a repeated
  outcome callback does not create a second graph.
- Missed Judge follow-up captures can now be repaired with the bounded,
  idempotent `armada_backfill_judge_followups` operator tool. It distinguishes
  actionable sections from explicit `(none)` evidence and reports incomplete
  searches. Its parser also accepts anchored legacy `Suggested`, `Recommended`,
  and `Tracked` follow-up labels while rejecting prose mentions. Read-only
  recovery capture now has an injected test seam, preserves
  non-cancellation fault containment, and propagates requested cancellation.
- Objective create and update MCP schemas now expose `suggestedPlaybooks`.
  Scheduler-created voyages can receive the same validated playbook selections
  that the objective model and dispatcher already supported.
- All voyage, mission, recovery, reflection, Architect, and restart entry points
  now share one tenant-scoped durable capacity admission lease. The gate counts
  actual work-bearing voyages and standalone missions, resolves transitive
  sibling lanes, returns typed capacity refusals, verifies lease ownership at
  the commit boundary, and cancels partial graphs when creation fails.
- Objective dispatch preparation can now be mandatory. Preview verifies full
  immutable source and target commits, evidence and timestamps for required
  claims, and structured sibling repository and artifact requirements. Runtime
  refinement, MCP schemas, and the Dashboard carry the same preparation shape.
- Terminal failed-voyage missions now enter normal recovery policy instead of
  remaining stranded. Recovery incidents link to objectives that own the
  failed mission or voyage, and rescue checkout priority is produced commit,
  original mission start ref, then same-vessel dependency commit.
- Pipeline dispatch and objective preview now share persona-aware model-tier
  resolution. Non-specialist stages no longer inherit an unassignable `high`
  selector, specialist and Judge stages remain `high`, literal pins stay exact,
  and multi-mission previews evaluate each distinct model requirement.
- Autonomous recovery now preserves Audit and Research scope. It records an
  incident and runbook execution but does not dispatch an Implementation
  rescue. Only a failed Judge creates a durable Judge follow-up; reference
  analysts and test reviewers do not impersonate a Judge. Existing system
  recovery runbooks gain the required mission-mode parameter in place.
- Audit and Research missions now preserve every stage declared by the
  selected pipeline. Reference analysts, test reviewers, and Judges all run
  read-only and pass reports through the configured graph. A fully report-only
  voyage does not arm Build or UnitTest Checks and can accept a no-commit
  report without a Check-exclusion marker. Any Implementation stage keeps the
  normal code and Check gates.
- Monitoring guidance now uses the WebSocket watcher instead of a foreground
  polling loop. It also states that Mail and Nudge reach a Pending downstream
  stage at handoff and cannot change a running stage's frozen brief.
- Production measurement now has one metric contract and a dated first
  baseline. The record separates 127 raw completed leaf slices from a verified
  landed result that is not yet computed, reports coverage and unavailable
  measures, and keeps concurrency unchanged until verified evidence exists.
- A bounded production summary is available through
  `GET /api/v1/production/summary` and `armada_production_summary`. It reports
  verified landing evidence, declared-ready delay, armed-to-start Check delay,
  execution time, rescue share, closeout delay, and explicit unavailable
  states for host-slot delay and other facts that Armada does not yet record.

### Upstream absorb (isolated)
- PostgreSQL and MySQL timestamp reads now tag stored UTC values as UTC without a local-time shift. Npgsql and MySqlConnector return `DateTimeKind.Unspecified`; `ToUniversalTime()` treated that as local time. MySQL also keeps `DATETIME(6)` fractions instead of dropping them through `ToString()`.
- Remaining MySQL delivery/workflow readers (check runs, releases, deployments, request history, workflow profiles) and local `FromIso8601Nullable` helpers use the same UTC-kind path. PostgreSQL and SQL Server token-usage `CreatedUtc` reads no longer go through `ToString()`.
- SQL Server captain delete nulls `signals.from_captain_id` / `to_captain_id` first. SQL Server cannot declare two `ON DELETE SET NULL` FKs to the same parent, so a captain referenced by a signal previously failed to delete.
- Install, update, reinstall, publish-server, MCP, and dashboard-deploy scripts accept `--insecure` / `-k` so npm works behind a TLS-inspecting proxy, and they copy the committed `src/Armada.Dashboard/dist/` when Node.js is not installed.
- Docs: `claude mcp add` plus the enterprise `allowedMcpServers` caveat. Remaining unused-async CS1998 sites that did not already await real work now return `Task.FromResult`. Package bumps: `Microsoft.Data.Sqlite` 10.0.11, `MySqlConnector` 2.6.2, `SyslogLogging` 2.2.1. Helm packs symbols (`snupkg`).
- Mission briefs teach `[ARMADA:TOKENS]` so OpenCode/Cursor runs can report real counts; the parser already existed.
- Planning dispatch releases the reserved captain and dock (server `DispatchAsync` plus the in-place Dashboard Dispatch button). `POST /api/v1/planning-sessions/{id}/stop-turn` aborts an in-flight turn without ending the session.
- Vessel readiness and landing-preview REST routes are wired to the existing evaluators (`GET /api/v1/vessels/{id}/readiness`, `GET /api/v1/vessels/{id}/landing-preview`).
- Dock repair and unstick are exposed on REST and MCP (`armada_repair_dock`, `armada_unstick_dock`). Unstick releases a held captain to Idle and reclaims the worktree.
- Setup-wizard objective container is taller and pins step actions so Register Vessel / Create Captain stay visible.
- Admiral shutdown now kills working agent processes before the token cancel and database dispose, so Helm stop/start does not leave orphan PIDs. Stale-captain cleanup treats a recycled OS PID as dead (`ProcessSupervisor.IsTrackedProcessAlive`).
- Dashboard Chat Stop is an abort, not a timeout. Captain detail can lift quarantine. The captain-tools probe waits 120s. Windows update scripts stop every `Armada.Server` process, including a repo-launched host.

Skipped from upstream (already equal or richer here): captain-map, token-usage charts, dashboard nav, fork-parity cores, Touchstone/test move, Mux install, `POST /api/v1/server/restart`, heartbeat 30s→10s, and the AutoLandPredicate alternate evaluator.

### Routing and settings (code to config)
- Captain routing, specialist-persona reservation, model-family classification, the stage-persona title guard, and the six specialist reviewer personas/pipelines/templates are no longer hardcoded. Product defaults are empty and policy-neutral (random within an unconfigured pool, guard off, no family or persona assumption). The former behavior lives in `factory/settings.fleet.example.json` and `Test.Shared.Infrastructure.FleetRoutingSettings`.
- Dashboard Settings now edits tier lists, family rules, within-tier strategy, non-native preference, specialist personas, the dispatch title-prefix guard, model providers, and additional personas/pipelines/templates. `modelTier` and `voyageDispatch` hot-reload; `modelProviders` and additional assets load at startup (the Settings page says so).
- When no tier list and no family rule is configured, `SelectModel` picks randomly among idle persona-eligible captains so a fresh clone still assigns work. Low still maps to mid (platform two-tier architecture).

### Dispatch and model selection
- Long pipeline reports now use a bounded head/tail preview with a durable
  `mission-output:<id>` reference, total length, full UTF-8 SHA-256 digest, and
  explicit completeness state. Authenticated REST and default-tenant MCP readers
  page the redacted persisted output so downstream stages can verify the complete
  safe artifact.
- Start-ref validation and branch creation now verify that the requested revision
  names a commit object in the dock-owning repository and keep its full object
  ID. A raw hexadecimal name for an absent object can no longer pass
  `rev-parse --short` and fail later during branch creation.
- Within a tier, models absent from `withinTierPreferenceOrder` are now chosen at RANDOM among the eligible peers instead of being returned in captain-enumeration order. Previously the ordering helper appended unranked models after the ranked ones and the caller returned the first, so the "random among unranked peers" path was dead whenever any preference order existed - two specialist models (e.g. `gpt-5.6-sol` vs `claude-opus-5`) always resolved to the first-enumerated captain. Ranked models still win by rank (Judges); only genuinely unranked peers are randomized. (`PreferredModelTierSelector.SelectModel`; covered by `SelectModel_High_UnrankedPeers_UseRandomPick_NotEnumerationOrder` and `SelectModel_High_RankedModel_PreferredOverUnranked_RegardlessOfRandom`.)
- Within-tier preference RANK now dominates the native/external captain split for tier selection. `PreferredModelTierSelector.SelectModel` previously split eligible models into external-provider-served vs native and pre-filtered to the external pool BEFORE applying `withinTierPreferenceOrder`, so a lower-ranked external model was chosen over a higher-ranked native one - a high-tier Judge ranked first would lose to a lower-ranked model merely because the latter had an external captain. The ranked search now runs across ALL eligible models (native and external), so the earliest-ranked model with an eligible captain wins regardless of the native/external boundary; the external-first / random tiebreak survives only for genuinely unranked peers (the fix in `39c6d1c3` is preserved). (`PreferredModelTierSelector.SelectModel`; covered by `SelectModel_High_RankedNativeModel_BeatsExternalLowerRankedModel`.)
- The admiral image installs every agent CLI (`claude-code`, `codex`, `gemini-cli`, `cursor-agent-cli`, `opencode-ai`) at `@latest`, with a `CLI_REFRESH` build-arg that busts the install layer so `@latest` actually re-pulls: `docker compose build --build-arg CLI_REFRESH=$(date +%s) armada` refreshes the CLIs, a plain rebuild keeps the last-built versions. `codex@latest` always satisfies the `gpt-6-astra` requirement (>= 0.153.4).

### Objectives and the scheduler
- Objective-linked scheduler, operator, bare REST, and remote-control dispatches now reserve a tenant-scoped durable admission lease before voyage creation. The lease renews while creation and linking run, and the losing request names the active voyage without creating a duplicate. Link failures cancel the new voyage and all active mission rows. The existing link-time check remains a last defense, terminal voyages still permit an intentional successor, and recovery keeps its explicit rescue-link exemption.
- The objective scheduler now has optional persisted campaign fair-share, disabled by default. When enabled, it keeps owner priority bands strict, rotates successful dispatches across roots tagged `campaign:<name>`, preserves rank and ID order inside each campaign and for plain work, and exposes its process-local last-served cursor in scheduler status.
- The objective scheduler now requests a debounced immediate refill when an auto-dispatch objective becomes ready, a dependency objective completes, or a terminal mission or voyage can free a shared lane. Event requests bypass the periodic interval, coalesce burst traffic, wait behind an active sweep, and keep the periodic health-loop sweep as a recovery path. Scheduler status reports the process-local `eventTriggeredSweepCount`.
- Mission assignment now uses one durable admission gate for every dispatch path. It resolves the existing transitive `buildParticipant` sibling lane, reserves each member with database coordination leases, checks actual active mission rows, and persists the winner before it releases the reservation. This prevents concurrent server instances from provisioning two sibling writers. Unlinked operator voyages now count in scheduler occupancy when they contain repository work, and lane derivation is tenant-local and shared by the scheduler and assignment code.
- Objective dispatch now has one read-only preflight for operator and autonomous paths, including linked bare-voyage creation and Helm stdio MCP. It reports the exact target vessel, effective pipeline, configured captain coverage, fallback tiers, required Build and UnitTest commands and inputs, objective and per-mission start refs, preparation refs, sibling inputs, brief completeness, and a complete typed dependency graph with bounded diagnostic chains before Armada creates fleet state. Busy but compatible captains are capacity information, not a readiness failure. REST exposes the same result at `GET /api/v1/objectives/{id}/dispatch-preview` and its backlog alias; MCP exposes `preview_objective_dispatch`. Objective writes reject new dependency cycles, including concurrent opposing writes and cycles through completed rows, with the complete closed path. Scheduler dependency diagnostics now run before capacity, hold, and lane gates instead of hiding blocked rows.
- Operator and autonomous objective dispatch now use one server-authoritative, bounded objective brief. It carries the scope, acceptance criteria, non-goals, refinement summary, rollout constraints, evidence links, verified source and target anchors, reusable types, entry points, inputs, response rules, cleanup, consumer and ledger duties, uncertainty, and owner decisions into each mission. Preparation claims record whether they depend on the source, target, both, or neither; an anchor or execution-target change marks only the affected claims `NeedsRecheck` and keeps the evidence for review. Linked operator missions also inherit the objective start ref when they do not set an explicit ref. Planning sessions and remote-control proxy dispatch preserve the same objective linkage and brief. The Dashboard now supplies only a short editable instruction and lets the server add the durable brief.
- An objective can carry a `StartFromRef` (`startFromRef` on `create_objective` / `update_objective`): the first stage of a voyage dispatched for it is cut from that ref instead of the vessel default branch, so a requeue from a `recover/<name>-<sha>` ref continues accepted work instead of rebuilding it. A ref that does not resolve refuses the dispatch before any voyage row exists (`start_from_ref_missing`, reported as the scheduler skip reason and an `objective_scheduler.start_from_ref_missing` event), and a ref that has gone by assignment time fails the mission by name. There is no fallback to the default branch.
- A requeued objective whose linked voyages have all ended dispatches again instead of sitting in `Dispatched` for ever.
- Objective recovery reconciliation is now failure-chain specific. A terminal original voyage completes the objective only when every independent `Failed` or `LandingFailed` chain root has its own fully landed linked rescue; one rescued branch cannot hide another unresolved branch. Failed historical rescue attempts are evidence, not new objective obligations, so a later successful attempt can close the original chain. Missing linked voyages, malformed mission graphs, unlanded work, and cancelled-only originals fail closed.
- A scheduler pause is attributed to its session (`pausedBy`, `pauseReason`); the autonomy layer may clear only a stale pause whose owner has been absent longer than the configured threshold.
- An engaged dispatch hold is reported as `dispatch_hold`, once per sweep, never as `dispatch_error`; a sweep that dispatches nothing names the constraint with counts (`vessel_count`, `vessel_concurrency`, `objective_skipped`, `no_eligible_objectives`).
- Incident links are annotations: linking an objective to an incident no longer copies the incident's voyage and mission onto the objective, which had silently removed the objective from auto-dispatch.
- Campaign status projects roots the same way as lanes; slices are opt-in and the lane rollup is the contract.
- `armada_add_vessel` / `armada_update_vessel` sibling entries accept `buildParticipant`; an entry that omits it keeps the stored value, as `extractionArtifactPaths` already did.
- Scheduling lanes are derived from sibling declarations: a sibling entry with `buildParticipant: true` (a consumer that builds the sibling through a project reference) joins the two vessels into one lane, and `maxConcurrentVoyagesPerVessel` is applied to the whole lane. A skip is reported as `lane_busy:<vesselA>+<vesselB>` with an `objective_scheduler.skipped_lane_busy` event; a read-only sibling (decompiled or artifact tree) joins no lane. Hand-set blockers were the only serialiser for a cross-vessel wave before.
- Autonomous (scheduler) dispatch derives the mission mode from the objective `Kind`: a `Research` objective dispatches read-only missions (no commit required, an unchanged branch is accepted as success), so a report-only autonomous objective is no longer failed at the Judge for an empty diff. Every other Kind keeps the `Implementation` default.
- Operator dispatch (`armada_dispatch` / REST) now derives the mission mode from a linked objective's `Kind` the same way the scheduler does: a mission that did not state its own mode inherits the objective's read-only mode, so a `Research` objective dispatched through an Implementation pipeline (for example `Tested`) drops the diff-dependent Test Engineer stage and its Judge accepts a sound no-commit report instead of failing the correct empty diff. An explicit per-mission `mode` still wins, and a non-Research objective is unchanged. The scheduler and operator paths call one shared rule (`MissionModes.FromObjectiveKind`), so an objective is judged the same way however it is dispatched.
- Provider branding is neutralized: the per-captain provider-credential dashboard UI and the provider-routing tests no longer name a specific external provider; the labels, comments, and placeholders read as generic provider wording, and the test example uses example.com. Server code was already provider-neutral (the key env var resolves from `ModelProviderSettings.ApiKeyEnv`).
- Provider-brand neutralization continues: the `cun-ai` and `hetzner` example/provider names are removed from source doc-comments, MCP tool descriptions, and tests (generic `example-provider` wording; example domains use `example-provider`/`example.com`). Server behaviour is unchanged — provider resolution was already generic from config; these were naming examples only.

### Checks and the Judge gate
- WebSocket broadcasts now have a process stream ID and monotonic cursor. A reconnect can replay a bounded 128-event, 1 MiB suffix, reports an explicit gap when replay is incomplete, and receives a bounded authoritative fleet or voyage snapshot before live delivery starts. The operator watcher reconciles voyages, missions, captains, and Checks on every connect, detects cursor discontinuities, and can detect a terminal scoped voyage from the snapshot. Existing route-only subscribers remain compatible.
- WebSocket clients now have independent bounded output queues. A slow or failed monitor can no longer block the event producer or delay healthy clients; each client keeps ordered delivery, and overflow causes that client to disconnect explicitly.
- Fleet monitoring now receives `voyageId` on every mission-change payload, reads the captain payload's real `state` field, and reports Check lifecycle changes with voyage filtering. Checks remain `Pending` while they wait for the shared host command slot and become `Running` only when execution starts. The durable `queueDurationMs` projection separates ready/slot delay from command-only `durationMs`; the automatic selection event is now `check.auto_queued`.
- Judge follow-ups are durable mission-linked audit items before merge association. A valid follow-up from any Judge verdict no longer disappears when the reviewed mission has no merge entry yet; the audit drain returns unassociated items, later merge enqueue reconciles them, and verdict recording supports `followUpId` while preserving the existing `entryId` path.
- The automatic Check executor searches stable pages of all Pending Armada Checks until it finds its bounded execution set or reaches the end. An eligible Check older than 200 ineligible records can no longer wait forever, and an interrupted search reports its page and scanned-record count.
- A Judge PASS is held when a green Check measured an older commit than the reviewed tip; the check executor supersedes the stale record (`check.superseded`, Canceled, naming its successor) and arms a fresh one at the tip. A failed Check for an older commit is treated the same way, as stale rather than as a rejection, on Open voyages as well as InProgress ones.
- Rescue voyages ARM their Build and UnitTest Checks instead of running them at rescue dispatch against the default branch, and the scheduler counts rescue voyages toward its ceilings.
- A single deterministic assertion failure classifies as `TestFail`, not `Infra`, even when the output also carries a NuGet warning or the word "dependency".
- Judge rules: accepted work must be an ancestor of the reviewed tip or present in the diff; delivery is proven by the diff under review, not by presence at the tip; a deliverable that lives in a stage's final response is never NOT DELIVERED for being absent from the tree; a citation inaccuracy inside a remark is a Suggested Follow-up, not a NEEDS_REVISION.

### Dispatch, assignment and rescue
- A dispatch whose mission title already carries a stage-persona prefix (`[Worker] `, `[Judge] `, `[Test Engineer] `, `[Architect] `, `[Product Manager] `, ...) is rejected with `mission_title_carries_stage_persona_prefix`. The pipeline prepends the persona itself, so a title that opens with one is a prior run's materialized STAGE mission resubmitted as a task; expanding the pipeline over such descriptions multiplied the work by the stage count (three stage missions became nine, then twenty-seven on the next retry). A real task title never opens with a persona tag; a non-persona bracket tag (for example `[URGENT] `) is unaffected.
- Branch continuation is decided by equality with the dependency's branch name, never by a name merely being present on the row, so a leftover name from an aborted assignment no longer fails a fan-out worker with `stage_base_missing`.
- A planner stage that committed code fails before any fan-out worker is created, instead of every worker failing two stages later.
- A selected captain is reserved in-process before provisioning, so two assignment passes cannot both provision a dock for one idle captain.
- A Worker that failed inside a voyage is rescued through a rescue voyage, never as a standalone mission.
- Rescue briefs: a Judge's narration preamble is dropped before any size budget applies; a Judge report's Suggested Follow-ups and Verdict stay whole in an over-cap brief; a documentation-only rescue under a Research objective is the work, not an `ineffective_rescue`.
- Architect handoff: a front-matter block is titled from its title line, and every spawned brief carries the plan-block-label rule.
- A stale sibling extraction-artifact copy in a shared sibling worktree is refreshed atomically instead of skipped.
- Each dock now owns its directory: the checkout sits at `docks/<Vessel>/<mission>/<Vessel>` and every declared sibling (`../example-sibling`) resolves beside THAT checkout, so a sibling is pinned at provisioning and is never shared with another dock. A dock created after a sibling landing therefore reads the new tip while a running dock keeps the tree its gate started on. The stale-dock sweep, reclaim and the disk-lifecycle orphan scan understand both the nested and the earlier flat shape; a flat leftover at a dock's root is removed before nesting; an empty root is removed on reclaim.
- A dock that reuses a shared sibling worktree behind its branch tip (another dock holds a lease, so the worktree cannot move) logs both commits and emits `dock.sibling_stale`, instead of measuring the older tree silently. The per-dock sibling layout that would remove the condition is an open decision.
- A declared `buildParticipant` sibling (a consumer that builds the sibling through a project reference) that cannot be resolved, throws during provisioning, or comes up as an empty checkout now fails the dock with a `dock.sibling_provision_failed` event, instead of being swallowed into a hollow dock that builds green while missing a required tree. A non-build-participant sibling (a read-only artifact tree whose absence is a tolerated skip boundary) keeps the warn-and-continue path. This closes the "sibling provisioned empty / whole verification stage lost" failure that surfaced as the captain's defect.
- The `mission.git_anchors` brief no longer reports a present nested file as absent. The suffix resolver that finds a file cited by its bare name (`Foo.cs` for `src/<project>/<project>.Core/.../Foo.cs`) used a `:(glob)` pathspec with `git ls-tree`, which does not support glob pathspec magic, so the command threw on every call and the resolver returned "does not exist on this checkout" for every real nested file. It now lists the tracked paths at the revision and matches the suffix in-process, still resolving only when exactly one tracked path matches (an ambiguous bare name stays unresolved).
- A captain actively working through a long tool call is no longer nudged as a `provider_silent_stall`. A parsed progress or `[ARMADA:ACTIVITY]` tool signal now refreshes the provider-progress tracker, which previously only advanced on runtime token-usage updates -- and those go silent while a tool runs, so a Judge running a foreground test suite for ninety seconds looked provider-silent and was mailed a stall nudge mid-work.

### Captain lifecycle
- The handled-exit marker is kept until completion handling (DoD gate, handoff, dock provisioning) finishes. The captain health check re-reads the mission and ignores a vanished PID for a post-work or terminal mission (`captain.process_exit_ignored`) instead of synthesising exit code -1, failing the finished stage and cancelling the Judge with no incident.
- The Cursor runtime passes `--approve-mcps` so the dock's Armada MCP server loads in `--print` runs.
- The one-line AI-Memory pointer is seeded in the runtime's auto-memory folder before launch.

### Storage
- PostgreSQL and SQL Server: token-usage records bind `Estimated` as an integer and `CreatedUtc` as an ISO-8601 string, matching the columns; every insert and every summary read had failed, so the Activity page's Token Usage tab showed nothing. A duplicate token-usage route registration is removed.
- PostgreSQL: the request-history truncation flags are BOOLEAN and the request-history tables are created when absent (migration 81). Every request-history capture had failed with 42804 because the live columns were INTEGER while the model binds a bool, so no request was recorded.

### MCP surface
- Tool-call arguments are normalised against the tool schema once at the transport seam; an empty string for any optional argument is treated as omitted, and string-spelled booleans and numbers are converted.
- `armada_enumerate entityType=checks` withholds each check's command log by default and reports `OutputLength` beside the row; `includeTestOutput=true` returns it whole, and `get_check_run` is unchanged. One 14-row page had measured 26.8 MB on the surface the operator instructions tell operators to prefer.
- The six oversized list tools preview long free-text fields and long primitive arrays, and the default list page size fits a tool result.
- Directed wakes are delivered on any MCP tool result for a session that sends `X-Armada-Participant`; the effective AgentWake participant may acknowledge an unaddressed Wake; long broadcast notes are previewed on a board read while directed mail always arrives whole.
- The inbox lists open incidents and hides failed missions whose voyage has halted.

### Dashboard
- Login: the form uses email autofill semantics, sends bearer and session auth headers, and its layout is repaired; the chat view preserves its reading position.

### Autonomous lead
- The operator's blocking poll is replaced by a WebSocket subscription watcher and bounded autonomous lead cycles: prompts are passed on stdin, the cycle runs under a scoped permission policy, the whole cycle is logged with a 30-minute cap, it runs from the Armada checkout whoever started it, survives a redeploy, and is routed through the configured Fable captain.
- A delegate helper class with permission-enforced limits; the lead posts its handoff from the completion gate exactly once, defaults the room key to `fleet`, refuses a cycle while an operator holds the work, and treats rows another participant owns as read-only.
- A Grok lead integration foundation with its evaluation notes.
- The Grok lead integration now has a restricted MCP listener with a fixed participant identity, an explicit read-only catalog, server-side tool-call audit events, and the shared lead-cycle lease used by the legacy runner.
- Its OAuth proof flow supports protected-resource discovery, dynamic client registration, PKCE authorization, native-app callback handoff, rotating refresh tokens, and owner approval for the read-only scope.
- A deployment overlay provides a loopback-only Armada listener and a Caddy HTTPS gateway that forwards only MCP and OAuth paths; the normal Armada MCP endpoint is not exposed through this gateway.
- The `grok.skcc.network` read-only staging connection is documented, with the legacy unattended lead retained as fallback while durable OAuth storage and write-mode review remain outstanding.
- Added a controlled-dispatch mode that keeps the normal `armada_dispatch` name but exposes a reduced schema: an active Grok cycle and objective are required, mission count is capped, code context is off, and staging files, playbooks, captain overrides, arbitrary pipeline IDs, and cross-dispatch dependencies are rejected.
- Controlled dispatch uses the `armada:dispatch` OAuth scope and remains disabled by default. Cancel, purge, release, deployment, dispatch-hold, check-resolution, and service-control tools remain outside the Grok catalog.

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
- A suitable autonomous rescue now starts from the failed mission's captured HEAD instead of the vessel default branch. It falls back only to an immediate same-vessel dependency commit. A reviewer rescue is blocked when neither commit exists. Armada resolves the ref, then proves the provisioned rescue checkout contains that commit before launch; a missing or unverified base fails as a provisioning fault.
- An autonomous rescue is now judged by what it CHANGED, not by whether it ran. A rescue whose change set is empty, or consists only of documentation, fails with `ineffective_rescue` and the change set named, instead of being accepted because the process stayed alive. The case this addresses ran for twenty-four hours, drew escalating stall nudges, died on a runtime crash, and left one changed documentation file behind - and every liveness measure the platform kept called that a working rescue
- Only rescues are assessed, and only in Implementation mode. A first-attempt mission may legitimately have been dispatched to write documentation, and an Audit or Research mission delivers a report and is never expected to change code - judging those by a diff is the same mistake in the other direction
- The assessment reads changed paths from the diff's `diff --git` headers only, so a hunk body containing a line that looks like a header cannot make a change set describe itself
- It deliberately does NOT compare the rescue's paths against the original mission's: a rescue continues from the preserved failed tip and normally changes the same files, so an overlapping path set would flag the normal case
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
- The `LocalMerge` post-landing working-directory sync guards on tracked changes only, not on a fully clean tree: an untracked captain scratch directory in the configured checkout no longer forces `working_directory_sync_failed` on otherwise-landed work, because a fast-forward preserves untracked files and the merge the sync calls already tolerated them. Only uncommitted tracked changes (or a wrong branch) still hold the sync (`IGitService.HasUncommittedTrackedChangesAsync`)
- The merge-queue integration worktree is guaranteed a clean slate before it is rebuilt: a locked or partially-removed scratch worktree that survived the best-effort cleanup is now deleted from disk and its stale registration pruned, so `PrepareIntegrationWorktreeAsync` never recreates over a dirty leftover. A left-behind scratch checkout with uncommitted tracked deletions no longer fails the whole landing with `contains tracked modifications` on a disposable `_merge-queue/` worktree

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
