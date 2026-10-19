# Changelog

Net changes since the latest full upstream merge on 2026-05-24
([merge baseline](https://github.com/developervariety/Armada/commit/cb9030bd5cbaca71efe738b53807f048afd05cd7)).
This compares the merged tree with the current fork. It includes later selected
upstream integrations and excludes changes already present at that baseline.

## Unreleased

### Added

- `definitionOfDone.hostCommandMemoryFloorMb` holds a free host command slot
  until the host has that many megabytes of available memory (`MemAvailable`
  in `/proc/meminfo`). It defaults to 6144, is clamped to 0-1048576, and 0
  turns it off; a settings reload applies it without a restart. An unknown
  reading never holds a command. A request held for memory keeps its place in
  line, and Armada reads memory again every 5 seconds while it waits. When no
  slot is held and the request has been held for memory for 15 minutes, it
  runs anyway and is recorded as granted under the floor. The slot snapshot,
  the check `SlotWait` and the automatic check runner's log line name the
  memory reason, for example
  `waiting for free memory: 4,100 MB available, floor 6,144 MB`.

- Both mission-rules templates tell a captain to delete a temporary file with
  `rm <path>`, without `-f`, or to write to a fresh path, and to hash a file
  with `sha256sum <path>` instead of hand-written hashing code. The lines reach
  the embedded defaults and existing built-in rows through the append-if-missing
  upgrader, so operator edits are kept.
- The Linter template carries a `## Test Check` section. For each test the
  mission adds or changes, the Linter reports duplicate or low-value proof
  under `## Residual Issues` as `TEST: <path:line> - <problem>`, and it never
  deletes, skips or weakens a test or loosens an assertion. The TestEngineer
  template carries a `## Test Authoring Gate` that asks what each new test
  protects, what regression fails it and why existing tests miss it. Both
  sections reach the embedded defaults and existing built-in rows through the
  append-if-missing upgrader, so operator edits are kept.
- `diskLifecycle.extraRetentionRoots` keeps operator directories outside
  Armada's own storage bounded. Each entry names a `path`, `retentionHours` and
  `keepNewest`; each direct child of the path is reclaimed once idle past the
  retention and outside the newest kept, unless it holds `.armada-keep` or is a
  symlink. A low-space pass logs a warning and records a
  `disk_lifecycle.low_space` event. `scripts/common/rebuild-local-image.sh`
  keeps the newest `ARMADA_RETAINED_KEEP` distinct images plus the running and
  just-built ones, removes the tags matching `ARMADA_PRUNE_TAG_PATTERNS` from
  older images, and prunes dangling layers.
- `preview_objective_dispatch` warns with `objective_text_conflicting_ids`
  when the stored text calls an objective other than the record or its parent
  current, parent or authorizing, and with
  `objective_text_conflicting_criterion_counts` when stated criterion counts
  disagree with each other or with the criteria list. The brief's Objective
  Scope names the stored parent objective id and, when the text names another
  controlling id, states that such an id does not control.
- `armada_record_judge_followup_handled` records that one Judge follow-up item
  has a home: a fix objective, an owner decision, or a note, with who handled it
  and when. `armada_list_judge_followups` numbers each item, leaves handled
  items out by default, reports `OpenItems` and `HandledItemsOmitted`, and lists
  them again with `includeHandled`. An unknown follow-up, item number, kind or
  objective is refused with a named reason. Migration SQLite 115, PostgreSQL
  119, MySQL 108, SQL Server 111 adds the `judge_follow_up_handled_items` table.
- `armada_list_judge_followups` lists the follow-ups Judge reviews suggested,
  newest first, split into one item per bullet, with vessel, time-window and
  paging filters. An explicit "none" counts as no follow-up.
- Memory proposals gain a `Promoted` state. The new operator tool
  `armada_promote_memory_proposal` records the full AI-Memory commit SHA, the
  operator and the time on an Open proposal after the owner writes the rule by
  hand. It refuses a short SHA (`commit_sha_not_full`) and a dismissed or
  already promoted proposal (`proposal_not_open`). Armada still never writes
  AI-Memory. Migration SQLite 114, PostgreSQL 118, MySQL 107, SQL Server 110
  adds the promotion columns.
- Typed-decision call events carry `estimated_cost_usd`, `cost_is_estimate`
  and `price_table_version`, priced from a new `typedDecisions.pricing` table
  that ships empty. A batched item is priced on its share of the request's
  tokens. With no price the cost is null and `cost_unavailable_reason` says
  why. The observation SQL reports cost per decision, cost per applied action
  and cache hits.
- The `preflight` decision caches its answers by state hash, question
  contract and model, so a repeat preview of an unchanged objective records a
  `typed_decision.cache_hit` event instead of a new provider call. The verdicts
  are the same as a fresh call's.

- Two optional workflow-profile fields let the definition-of-done gate re-run
  the failing tests of a runner that is not `dotnet test`.
  `failedTestLinePattern` is a regular expression with a named group `name`
  that reads failing names from lines the built-in matchers do not know; a
  save refuses an invalid pattern or one without the group, and each match
  has a 250 ms timeout. `isolatedTestRerunCommand` re-runs only the named
  tests, at most three, with the names in the `ARMADA_RERUN_TESTS`
  environment variable and never in the command text. The vessel, flake-score
  and consumer re-runs all use them, each from its own profile. A red whose
  names were read but that has no re-run route now says which field is
  missing. Without the fields, every existing re-run command is unchanged.
  The dashboard profile editor shows both fields.

- A configuration-driven host storage cleanup (`scripts/linux/storage-cleanup/`)
  with a daily systemd timer and installer. It prunes ignored build outputs,
  agent caches, redundant Docker tags and old test databases, and can remove
  idle scratch folders and unused disposable image tags. Every host path and
  pattern comes from `/etc/armada/storage-cleanup.json`.
- The four-week typed-decision observation review
  (`docs/TYPED_DECISION_OBSERVATION_REVIEW.md`) replaces the interim one: per
  decision calls, actions, unavailable calls, latency, tokens and reversals for
  16 September to 14 October, reversal causes, one recommendation per
  decision, and the rescue and papercut baselines. No setting changes.
- `definitionOfDone.hostCommandSlots` sets how many full build or test
  commands (definition-of-done gate commands, check runs and merge-queue test
  runs) the host runs at once. It defaults to 1, which keeps one suite at a
  time, and is clamped to 1-8. Slots are still granted in request order; a
  settings reload applies a new count without a restart. The check slot wait
  and the automatic check runner name every current holder. A count above 1
  risks false Timeout failures on a host without the capacity for it.

### Changed

- The Git Service and Dock Service unit suites take their base repository
  (branch `main`, one "Initial commit" adding `README.md`, test user
  configured) from the shared `TestGitRepoHelper` template, moved into place,
  instead of running six git commands per case.

### Removed

- Eleven scripts that nothing referenced: the server bootstrap and
  self-deploy cutover rehearsal scripts, the Windows proxy build, landing
  audit and Markdown backlog import scripts, and the hand-run tests for the
  image rebuild, framework resolution, fork migration check, SSH bridge,
  cycle-log renderer and watcher scripts.
- 132 legacy `test/Armada.Test.Automated` HTTP cases for voyages, fleets,
  captains, events, the merge queue and vessels, each of which the
  `Test.Shared` E2E suite runs with the same route, inputs and assertions; and
  two weaker `Test.Shared` voyage cases whose legacy twins assert more. Mission
  status cases stay in both suites, because the two set up different missions.
- 23 per-adapter typed-decision tests for the refusal, flake-score,
  change-substance, stage-necessity, review-substance, runtime-failure and
  failure-cause adapters that only checked the Off, Unavailable, Shadow and
  below-threshold modes. Those modes are decided by the shared adapter base,
  which `TypedDecisionGateMatrixTests` drives across every cell; each adapter's
  own Gate tests still prove it reads its own decision settings and builds a
  request.

### Fixed

- A voyage-armed Check waits while a stage that reports Testing or Review is
  still running. A captain reports those statuses as progress and can still
  commit, so a Check that ran then measured a commit the stage replaced, was
  superseded, and ran again. A stage that reached Review through completion
  carries a completion time and no longer holds the Check back.
- The autonomous-recovery and incident-lifecycle sweeps find a failure's rescue
  missions by parent mission. They read only the matching mission summaries on
  each tick instead of every mission of the vessel, which kept the database
  busy with large reads several times a second. The mission recovery report
  uses the same lookup.
- The incident-lifecycle sweep reads matching check runs without their output
  logs, and the failed-check count reads one row and the total instead of up to
  500 runs with their logs.

- The `SlotWait.Guidance` text on an unresolved Check no longer claims the host runs one
  suite at a time. It says the host runs a limited number of full build or test suites at
  once, which holds for any `definitionOfDone.hostCommandSlots` value.

- The brief byte-budget backstop keeps the role text at the head of a mission
  description it shrinks. A description already bounded with its pinned
  acceptance criteria is shortened in its narrative only, so the criteria no
  longer take the head position and push the role text out.
- Autonomous recovery writes `autonomous_recovery.capacity_deferred` and its
  `rescue_capacity_deferred:` incident note once per change of refusal code or
  limit, instead of once per sweep while capacity stays full; each refusal is
  still logged with the number of rescues waiting. A failure whose incident an
  operator set to `Mitigated` with a written root cause, at or after the
  failure, gets no rescue and one `autonomous_recovery.skipped_mitigated_incident`
  event; a restart or a newer failure is recovered as usual.
- Readers that scan many Checks no longer load each Check's whole output log.
  The history timeline, the production summary, the fleet reconciliation
  snapshot, the status counts and the disk-lifecycle live-Check scan list
  Checks without the `output` column, so the admiral no longer holds every
  stored log in memory at once when the History page opens with no time bound.
  `CheckRunQuery.IncludeOutput` (default true) selects this for any listing,
  including `POST /api/v1/check-runs/enumerate` with `"includeOutput": false`.
  A Check read without its output cannot be written back with an update, so a
  listing never erases a stored log.
- `armada_enumerate` with `entityType=checks` does not read the `output`
  column unless `includeTestOutput` is true, so a page of up to 1000 Checks no
  longer loads every log only to drop it. `outputLength` comes from the
  database's length of the column. `GET /api/v1/check-runs` accepts
  `includeOutput=false`, both REST listings then return `outputLength`, and the
  dashboard's Check lists use it.
- The production summary reads only the Checks created from 90 days before its
  window's start, bounded in the database query, the same lookback as its other
  slice evidence. It no longer reads every stored Check.
- A dispatch preview whose blocker exists but whose stored row cannot be read
  (for example a backlog state that is not a defined value) reports
  `objective_dependency_unreadable` with the stored-data error naming the
  blocker and the field to repair, instead of reporting the blocker as missing.
  The blocker still blocks, and the scheduler counts the code as
  dependency-blocked.
- Releasing a held mission that has dependent stages, by an operator clear or by
  a passing definition-of-done gate re-run, releases the mission's dock after the
  handoff, as completion does, so the next stage can check out the branch.
  `armada_review_hold` refuses `clear` with `gate_rerun_running` while the
  mission's gate is re-running on its dock; the re-run releases or fails the
  hold itself. A stage whose inherited branch is still checked out in the dock
  of an earlier stage of its own chain waits with the assignment hold
  `predecessor_dock_held` instead of failing through the held-dock retry bound.
- An autonomous rescue that re-runs its review stages checks fleet and
  sibling-lane capacity before it writes its rescue voyage, so a full lane no
  longer leaves an empty cancelled voyage on every recovery sweep. The check
  asks the admiral (`IAdmiralService.ProbeCapacityAsync`), which applies its
  own admission rule through `FleetCapacityAdmission.ProbeAsync` without
  reserving. A rescue voyage refused for capacity after it was written, before
  any mission, is deleted.
- The isolated-checkout, commit-count and Slop-execution suites report each of
  their git-backed cases as a named skip when git is not on the path, so the
  runner counts the skips instead of reporting suites that ran nothing.
- An operator's `run_check` or `retry_check_run` runs to completion even when
  its voyage has ended; the automatic Check runner neither discards nor runs a
  Check an operator is executing. Every discarded Check writes its reason into
  its summary and output and records a `check.discarded` event.
  `retry_check_run` follows the `run_check` bounded wait: a retry still waiting
  for the host-wide slot returns unfinished with `slotWait` instead of blocking
  until the client's tool timeout.
- The WebSocket idle-session test runs in under a second. It gives its own
  server a 300 ms authentication window and asserts that the refusal arrives no
  earlier than that window and well before the 15-second production window,
  which stays the default for every other session.
- The agent-runtime log tests wait for the runtime to finish handling the
  agent's exit (output drained, final message written, log closed) instead of
  a fixed 2.5-second pause, so a log assertion never reads a half-written file
  and the tests no longer wait when the agent exits at once.
- The runtime test named for stdin encoding asserts it: a launch that does not
  redirect stdin carries no stdin encoding, and the agent still starts.
- The code-index staleness suite reports each of its cases as a named skip when
  git is not on the path, so the runner counts the skips instead of reporting a
  passing suite that ran nothing.
- The Harbor transport tests prove that the Admiral sent a runner nothing with
  an ordering probe instead of a 400 ms quiet wait. The fake runner repeats its
  handshake, and the link answers with one `harbor_handshake_duplicate` error
  after every frame it sent earlier, so the first frame back must be that
  answer.
- `armada_enumerate` scopes every entity type by its per-id read rule: a
  global administrator lists every record, a tenant administrator its tenant's
  records, and any other caller its own records, with playbooks tenant-wide. A
  mission caller lists as its owner without administrator rights, and its
  mission and voyage listings match what the status reads return. An undeclared
  argument is refused by name instead of being ignored, so a misspelled filter
  never returns an unfiltered listing.
- On a voyage that splits into lanes, each Judge's Check gate reads only its own
  lane's records. Another lane's green no longer reads as stale, and another
  lane's red no longer rejects it. The supersession re-arms a stale record at
  its own lane's commit instead of moving records between lanes. A done lane
  runs its Checks while another lane still works, and a lane with no record of
  an armed Check type gets one stamped at its own commit. Voyages that do not
  split behave as before.
- A definition-of-done gate holds the host command slot from its first command
  until it ends. Its unit tests and each consumer build and test follow its
  build directly instead of going to the back of the queue for every command,
  which made one gate wait behind all newer work once per command. A Check or
  merge-queue test that asked before the gate began still runs first; one that
  asks while the gate runs waits for the rest of the gate.
- A voyage-armed Check runs once the voyage's work is final: a stage has
  committed and no stage that can still commit is Pending, Assigned,
  InProgress, or in Testing or Review without a completion time. The Judge, personas whose normal result is an empty diff, and
  read-only missions do not count, so the Check still runs before the Judge
  decides. A Check no longer spends the host command slot on each intermediate
  stage commit only to be superseded by the next stage.
- A brief whose runtime cannot widen its workspace roots names
  `grep -rn <pattern> <sibling path>` for a sibling search; the captain image
  ships `grep`, not `rg`.
- `GetMission_ReturnsAllProperties` accepts a mission the scheduler assigned
  before the read, so it no longer fails when a captain is idle.
- A downstream stage reads the complete report of every earlier stage its brief
  cites. The full redacted output is written to `_briefing/handoff/<mission id>.md`
  in the dock, with the digest the brief states, and the handoff preview names
  the file. Another owner's output is never staged.
- Briefs no longer give two conflicting orders. The Judge acceptance-line form
  uses ASCII (`-- MET.`), matching the ASCII-only rule, and the commit
  instructions name the trailers as the one place Armada ids belong, so a rule
  against ids in committed content covers files, not trailers.
- A rescue of an objective whose non-goals forbid test edits skips its
  TestEngineer stage and records the skip on the rescue voyage, unless the
  failure names the TestEngineer.
- The Linter verifies with the repository's check script (for example
  `scripts/check.sh`) in full when one exists, and a Codex brief names the shell
  commands the Codex runner accepts for temporary-file cleanup and digests.
- A captain whose vessel declares siblings now sees each sibling's provisioned
  HEAD, any uncommitted changes, required paths the checkout lacks, and a
  sibling that provisioning did not produce, in a Sibling Checkouts brief
  section. A sibling input can pin a commit: the brief lists the assigned paths
  changed since an ancestor pin and states that HEAD controls, and dispatch
  preview refuses a pin that does not resolve or is not an ancestor of the
  branch docks provision. Claude Code, Codex and Cursor captains get each
  provisioned sibling as an extra workspace root (`--add-dir`); other runtimes
  get the shell search command in the brief.
- A brief no longer names a host path a captain cannot open: the
  `briefPathRewrites` setting (empty by default, hot-reloaded) rewrites a host
  path prefix in the mission description and the objective scope to the path
  the captain resolves, such as a container mount.
- Git anchors and objective preflight question 1 resolve a cited directory and
  a path spelled in another letter case, so a present test project is no
  longer reported absent as new work. A path written with a trailing slash
  resolves the same way. A name that matches several tracked paths is reported
  as ambiguous, and an absent path cited with line numbers as a stale
  citation. Both read each cited line range and flag one past the end of its
  file or one that no longer holds its named symbol, with the symbol's current
  line; question 1 names a case-only miss with the tracked spelling.
- Two unit tests no longer fail on a loaded host. The stale-job reap test waits
  for its operation to start instead of sleeping a fixed 30 ms, and the relaunch
  test accepts a heartbeat stamp that the new process records after the launch.
- A finished Test Engineer or Product Manager report that lacks a section its
  persona template requires, such as a progress note in place of the report, is
  no longer a complete output artifact. `armada_mission_output` reports
  `report_contract_missing` and names the missing sections, and the next stage's
  handoff lists them as an evidence gap. Every page also carries `PageSha256`
  and `PageUtf8Bytes` for its own content beside the joined-content digest and
  length.
- A failed definition-of-done command, and the rescue brief built from it, names
  every failing test with its first exception line and every skipped test, read
  from the whole standard output and standard error. A bounded tail used to drop
  a failure that printed early or on standard error and named only the last few
  skips. A skip list over 100 gives the count and where the full log is. Check
  run views carry the same list as `testOutcomes`.
- Dispatch no longer stages the vessel's generic baseline context pack for a mission. When the
  pack for the mission's own goal fails or times out, dispatch stages a lexical-only pack for that
  goal instead, read from the local index without a query embedding, graph expansion or summary.
  Every pack header now names the covered roots as well as the goal and indexed commit, and a
  summarized pack keeps the header above its summary.
- A captain brief names the assigned runtime's instruction file even when a
  handoff written on another runtime named `CODEX.md`, `CURSOR.md` or `MUX.md`,
  and lists under `## Named Files Not Staged` each `_briefing/` path the
  description names that the dock does not hold. Objective Scope names the
  objective id.
- A captain brief says that printing an `[ARMADA:NOTE]` line posts a board note,
  so captains no longer look for a board tool, and that an `unavailable`
  typed-decision answer is not a papercut. It names `armada_check_prior_art` as
  unavailable on a vessel where the tool is off.
- Objective Scope states that a non-goal wins over a conflicting role, rescue or
  captain duty, and tells a TestEngineer whose objective forbids test edits that
  it is validate-only.
- A captain reads the records its brief cites. Captain evidence reads follow the
  objectives that authorize the mission or its direct sources, with their
  ancestors, blockers and children, the ids those objectives cite, and the
  missions and voyages they list. A voyage-less review stage can read the
  voyages of its objective and of its direct dependency through
  `armada_voyage_status`, whose counts include mission rows stored without a
  user. `armada_enumerate` lists a mission caller exactly the objectives
  `get_objective` returns. A refused read carries `Reason` (`out_of_scope` or
  `not_found`) without revealing another owner's record.
- The admiral image carries the captain study tools `pdftotext`, `monodis` and
  the Python `dnfile` package, so a study can read PDF sources, disassemble IL
  and walk .NET metadata without hand-written decoders. A running deployment
  gets them on its next image rebuild.
- Staged AI-Memory files are stable for a whole voyage. The first slimmed brief
  in a voyage pins its memory files, and every later stage receives the same
  bytes, with `_briefing/memory/manifest.json` giving each file's bytes and
  SHA-256. A section that holds only its heading is no longer indexed or staged,
  and the slimmed memory section names only the dock-local memory folder, not
  the host memory root that a dock cannot resolve.
- A fresh SQL Server database migrates past v108. The statement that drops the
  pipeline stage optional flag's default constraint builds its `ALTER TABLE` in
  a variable, because `EXEC` does not accept an expression.
- A captain can omit the id on `armada_mission_status`, `armada_mission_output`
  and `get_objective`. They read the captain's own mission, or the objective that
  authorizes it, instead of failing with a null-id error. A caller with no
  mission credential gets an error that names the missing argument.
- A captain's typed-decision tool call is recorded on the mission its credential
  names. A `missionId` argument that names another mission, such as the
  Architect of the same voyage, is ignored and logged, so the call is no longer
  stored on that other mission.

- A PortingReferenceAnalyst stage holds the mission branch in its dock, because
  it commits parity fixes; it no longer starts detached and leaves its captain
  to move the branch by hand. The no-push mission rules tell a captain in a
  detached checkout to commit on HEAD and leave branch refs alone, since Armada
  moves the mission branch to that commit when the stage ends.

- Rescue briefs carry only a review's real defects. The blocking-findings list
  no longer reads "Minor, not blocking:" or "Observed but not blocking:" items
  as blocking, leaves out items that disclaim a defect, close "Reviewed and
  tested." or "Cosmetic.", or report agreement without naming a defect, and
  keeps the paragraph after a required list out of that list. It now carries
  bold defect labels, paragraphs under a "Confirmed defect:" lead-in, and each
  inline "(n)" change in a Verdict paragraph as its own finding. When a long
  review is reduced, NOT MET criteria from the omitted sections stay in the
  embedded feedback and the note names the reduced sections.

- The PostgreSQL and SQL Server binders name `memory_proposals.promoted_utc`,
  so the startup schema guard accepts the promotion column. A unit test now
  compares every timestamp column a PostgreSQL or SQL Server migration adds
  with the binder's form for it, so a missing entry fails the suite instead of
  stopping the admiral at startup.

- Dispatch stages a context pack built for each mission's own goal. Before, every
  dispatch on an indexed vessel staged the vessel's cached baseline pack, which
  answers a generic architecture question, so captains reported packs that did not
  cover their files. The baseline is now a logged fallback for a goal pack that
  fails, times out or returns no file. The captain brief tells captains to record a
  pack miss in the `Pack:` line instead of a papercut.
- The nine typed decisions the owner switched off after the four-week
  observation review (`inbox_triage`, `stage_necessity`, `prior_art`,
  `revision_kind`, `memory_record`, `memory_review`, `memory_candidate`,
  `flake_score`, `owner_digest`) ship `Off`, so a settings reset or a new
  install no longer turns them back on. Each stays wired; live settings that
  name a mode keep it.

- A rescue runs the stage list its failed voyage was dispatched with, not the
  pipeline as it is at rescue time. Every dispatch records its resolved stage
  list as a `voyage.pipeline_recorded` event, and a rescue anchors on that
  list, so editing, deactivating, deleting or re-pointing a pipeline while a
  voyage runs no longer moves the rescue to the wrong stage. A voyage with no
  recorded list is rescued from the live pipeline and records a
  `rescue.live_pipeline_used` event. `update_pipeline` and `delete_pipeline`
  report `RunningVoyageCount`, the running voyages that keep the old stages.

- `GET /api/v1/status` reads the objectives once and the incident snapshots
  once to build its objective, backlog and incident counts. It read every
  objective once per status and backlog value and every incident snapshot
  once per status and severity value, which made the call take several
  seconds on a large store. The counts are unchanged.

- A rescue of a failed Judge, Linter or TestEngineer no longer hands its Worker
  the failed reviewer's role text as the original mission description. The
  rescue brief removes every persona role section from that description and
  says it did.
- The later stages of a rescue chain no longer get only pointers and a fixed
  "resolve the reviewer feedback" sentence. After a reviewer rejection they
  carry the blocking findings to check; after any other failure they carry the
  failure reason.
- Report essentials read blocking findings under a Linter's `Code
  Correctness` and under `Findings`, `Required Changes`, `Blocking Findings`
  and `Blocking Issues`, and under whole-line bold section labels. A
  `NEEDS_REVISION` or `FAIL` verdict with no readable finding is marked
  incomplete instead of reading as no findings.
- The shared E2E test that lists Open voyages no longer fails when the
  fixture's idle Worker captain dispatches one of its voyages before the list
  call. It checks the filter against each voyage's status after the call
  instead of counting on two voyages staying Open.

- An admiral restart no longer leaves a finished mission held for good when it
  kills that mission's running or queued definition-of-done gate. The health
  loop re-runs the gate: a pass releases the hold and hands off or lands, a
  failure fails the mission into recovery, and a mission whose dock is gone
  stays held and says why. The orphan-dock reaper keeps the dock of a produced
  mission whose gate has no result, so the re-run still has the work to test
  after a restart. `armada_dispatch_hold` `status` and `engage` also
  list `RunningGates`, `GateReruns` and `HostSlot`, so a deploy can wait until
  no gate or check is in flight.
- A Research or Audit mission of an operator-defined persona gets a
  report-only role header and its persona template as guidance. Before, such a
  brief carried no role text and no output contract at all.
- The partial-outcome note that lists unmet acceptance criteria tells only an
  Implementation Worker or TestEngineer to complete them. Every other stage,
  including every Judge and every Research or Audit stage, is told to check
  them, report each one still unmet as a finding, and not do the prior stage's
  work itself.
- A captain brief whose acceptance criteria fill the 12,000-character metadata
  cap keeps at least 8,000 characters of mission context beside them: the role
  text at the head and the newest handoff at the tail. Before, such a brief
  carried only the criteria and an elision marker.
- A Research or Audit Worker that ends `[ARMADA:RESULT] COMPLETE` while its own
  report states that its acceptance criteria are not met now fails with a
  `report_incomplete:` reason that quotes the statement, and the reviewer
  stages after it are cancelled. Before, every reviewer ran and the Judge
  failed the voyage for the same reason.
- A pipeline stage's own description now reaches its captain: dispatch appends
  it to the stage's mission description as a `## Pipeline Stage` section.
  Before, only an internal decision read it. Pipelines whose stages carry
  descriptions show them to captains from this change on.
- In a report-only pipeline, a Worker stage after an earlier Worker in its own
  chain is told to revise the root report instead of writing a new one, and a
  Judge after that revision judges the revised report and checks that each
  reviewer finding was addressed.
- A rescue brief's blocking-findings list leaves out an item whose closing
  sentence only labels it lesser ("Follow-up.", "Follow-up, not a blocker."),
  the same as an item with a parenthetical "(not a blocker)" label. A natural
  rescue listed two such follow-ups as findings to fix.
- A read-only (Audit or Research) brief no longer contradicts itself. Captain
  instructions that order commits are followed by a line that gives the
  read-only rules precedence. A Product Manager or Usability Engineer gets a
  report contract instead of a required specification, so a mission that asks
  for findings, or forbids a specification, has one consistent contract.
- An MCP tool argument sent in snake_case or kebab-case (`mission_id`) is
  renamed to its schema property (`missionId`), unless the caller also sent
  that property. Such a call used to answer `Mission not found`.
- The mission-output handoff points captains at the `armada_mission_output` MCP
  tool only, with its PascalCase field names (`NextOffset`, `HasMore`). It
  named a REST route with no host or port, and captains called it on the MCP
  port. The tool description uses the same field names.
- Docs: `MCP_API.md` states the `Accept` header a `POST` must send and the `406`
  answer without it; the `REST_API.md` log examples use the REST port 7890.

- A mission caller can read missions from the other voyages of its own
  objective. A Judge in a later voyage got `Mission not found` for a mission of
  an earlier voyage, because the evidence scope used objective links only for
  objective reads. One objective with the caller's owner must list both
  missions, by voyage or by mission, and the target must have the same owner;
  missions of other objectives stay out of scope.

- A pipeline that names one persona at two stages (for example Worker, analyst,
  TestEngineer, Worker, Judge) is rescued at the right stage. A rescue anchors
  on the last stage of its persona before the failed stage, or at the failed
  stage when the rescue has the failed persona, and takes that stage's order,
  tier and review settings; a Judge rejection is rescued at the later Worker
  and re-runs only the stages after it, instead of rebuilding every stage after
  the first Worker. A dependant of a cancelled
  stage waits on the rescue stage at the same stage order. A `skipStages` name
  that is the persona of more than one stage is refused with
  `stage_skip_ambiguous_persona` instead of dropping every such stage, and a
  rescue does not carry such a skip.

- Terminal-voyage reconciliation completes an Audit or Research mission that
  recorded no commit, with reason `terminal_voyage_report_only`, under any ended
  voyage. It no longer marks the finished report `Failed` or `Cancelled` with
  `terminal_voyage_no_commit`. A read-only mission that recorded a commit keeps
  the landing-evidence rule. Mission summaries, reconciliation items and the
  `mission.terminal_voyage_reconciled` event carry the mission mode.

- A Judge PASS that the acceptance-criteria check refuses on line shape alone
  (a table, evidence on the next line, a missing criterion line) runs the same
  Judge once more in place with the check's reason and the exact accepted line
  form, instead of failing the mission and starting a Worker rescue. A NOT MET
  criterion still fails at once. The build prompt states the exact line form,
  and a criterion listed twice in a brief is covered by one line.

- A report-only (Audit or Research) pipeline stage reviews the root report
  itself. Each stage received only the stage it depends on, so a Judge after a
  Linter never saw the Worker report, parallel reviewers lost each other's
  findings, and a long report reached the next stage as a head-and-tail preview.
  A report-only dependent now gets an `## Earlier Stage Reports` section with the
  root report whole and every earlier reviewer's findings, the prior report is
  inlined whole up to 24,000 characters, and the description budget is 80,000.

- The rescue findings list no longer shortens a Judge's required changes to make
  room for passing notes. Items under a lead-in that reports passing or reviewed
  behaviour, items under a minor or follow-up lead-in, and items marked not a
  blocker are left out; when the list is still over its bound, plain items that
  name no defect are dropped before any named defect is shortened.
- The disk lifecycle no longer counts a symlinked merge-queue or integration
  worktree entry as reclaimable. The scan followed the link, counted the
  target checkout's bytes, and every reconcile then logged a refused removal.
  Such an entry is now recorded `skipped` as a symlink, as docks already were.
- Autonomous recovery no longer reopens a failure whose incident is closed.
  A rescue deferred by a dispatch hold was kept only in memory, so when the
  hold cleared or the admiral restarted, a failure an operator had already
  closed got a new incident and a duplicate rescue. An incident `Closed` at or
  after the failure now ends recovery for it, with one
  `autonomous_recovery.skipped_closed_incident` event.
- The disk lifecycle reclaims operator evidence: clones and build outputs in a
  folder idle for 24 hours (6 hours when free space is low), keeping receipts,
  logs and any folder marked `.armada-keep`. Whole-folder removal is opt-in
  through `operatorEvidenceRetentionDays`. Folder sizes and ages now include nested files, and the
  report carries the free space it measured.

- The rescue findings list carries a Judge defect written as a paragraph under
  Correctness, Failure Modes or Tests, not only list items, so a rescue sees a
  required revision or a missing proof that the review states in prose.

- API-endpoint conversation compaction records a `mission.context_compacted`
  event for each pass that replaces a tool result, with the removed bytes in
  total and per tool, so its cost is visible on the mission.

- Non-objective dispatch and autonomous rescue planning verify configured captain
  coverage for each required persona and tier before creating work. Missing
  configuration refuses or defers the work without treating busy compatible
  captains as absent or spending a rescue attempt.

- Check cancellation, execution and source stamping re-read and update the
  current record under one state lock. Terminal-voyage discard cancels only
  Pending Checks, and a stale stamp cannot retag a Check after it starts.

- Explicit MCP code-search debug requests return independent embedding vector
  copies within normal result limits. Default search and context-pack results
  remain vector-free.

- Rescue stages use the current owning objective contract and share objective
  preview's execution requirement checks. Missing resources leave a named
  incident without creating rescue work or spending a recovery attempt.

- Objective preflight keeps Jev Q4 dock-observability and Q13 owner-question findings as visible warnings in Gate mode. They do not block dispatch or create an owner note; other high text-question findings remain blocking.

- Successful no-op reconciliation records the completion note `no-op integration: work already at target`, while logs retain the detailed decision reason.

- SQLite coordination room creation ignores only a duplicate room key, so concurrent provisioning keeps the existing room and still reports other constraint failures.

- The dashboard exposes Code Index and Notifications history pages, and routes
  `/token-usage` to the token-filtered Activity page. Index searches clear old
  results when the vessel changes and show why an index is unavailable.
- Merge-queue no-op detection accepts already-integrated implementation work
  only when its reachable branch history includes a commit made after the
  mission started. Missing or unreadable history, review stages, and rescues
  with no substantive work remain refused.

- Captain `[ARMADA:NOTE]` output is stored on the coordination board with the
  assigned captain and mission metadata. Notes redact credentials and enforce
  500-character and 20-per-mission limits.
- The Admiral health check uses OpenCode provider-progress timestamps when it
  decides whether to stop and recover a live captain. Startup grace, terminal
  markers, dock writes, branch commits, and the bounded recovery limit remain
  in effect.

- Blocked recovery for Infra and Timeout gate failures retains the stable
  `gate_class_not_rescuable` reason in the event and incident, with the failure
  class and operator guidance.

- Judge acceptance checks read the verdict attached to each required criterion.
  Literal `MET/NOT MET` instructions and evidence text do not cause false
  rejection; a real `NOT MET` verdict still refuses PASS.

- Merge commits never name a mission branch. Docks install a
  `prepare-commit-msg` hook that rewrites the first line of a merge message Git
  writes, so a captain's `git merge main` and a merge-queue merge no longer
  publish the mission id in history. The operator branch merge builds its
  subject by the same rule.

- Rescue delivery evidence rejects chains that cross tenant, user, or vessel
  scope and includes cancelled siblings whose attempt facts remain.

- A repeated model gateway timeout fails with `provider_timeout` and a
  configured endpoint label. Its one-retry budget follows the mission across
  captain assignments, and retry and failure facts retain the typed cause.

- Dashboard captain forms retain the Mux turn cap across edits and offer only
  approval policies supported by `mux print`.

- MCP captain create and update tools store Mux maximum-turn settings, and clear
  the setting when a captain changes to another runtime.

- Mux captain options reach `mux print`: configured adapter type, temperature,
  token limit, system prompt, and supported approval policy are emitted, and
  `--mux-max-turns` sets a clamped per-captain iteration limit. Unsupported
  print approval policies now stop launch with an explicit error.

- Verified production summaries can count a failed voyage after recovery only
  when typed attempt facts prove every independent rescue chain through
  completed, committed delivery tips with landing and passing Check evidence.
  Ambiguous, incomplete, or cross-scope lineage remains unknown.

- Mission attempt facts keep stable reason codes only when a typed emitter
  provides an allow-listed cause. Watchdog, runtime, definition-of-done, and
  landing failures retain their known causes; unknown causes and free-text
  messages remain unclassified.

- Tenant creation rejects whitespace-only identifiers with a client error
  before storing a tenant or its initial user credentials.

- REST mission and voyage reads enforce the shared mission evidence scope
  before returning metadata, counts, or embedded details, including tokens
  whose owners are administrators.

- Rescue finding lists retain unbulleted `R<number>` and severity findings with
  their explanations and anchors. They omit passing test summaries and test
  gaps do not become positive results. Completed criteria stay out of the list
  when their descriptions contain phrases such as "NOT DELIVERED".

- Dock boundary hook tests detect POSIX shells with a portable command and
  report unavailable tools as named skips instead of passing without execution.

- Check runs honor an explicit commit hash as the exact revision to test. An
  unavailable commit or failed checkout now fails before the command runs,
  instead of testing a branch or the clone's default revision.

- Failed definition-of-done commands retain their redacted bounded-runner output
  in mission-scoped capture files. The evaluation report stores a capture ID,
  byte count, digest, and runner completeness flags. The mission log endpoint
  returns bounded UTF-8 pages after mission authorization. Older records without
  captures remain readable.

- A usage account whose login the provider rejects reads as needing sign-in,
  not Exhausted on a timer. A Codex usage read refused with a credential
  rejection (HTTP 401, an expired or revoked token, "sign in again") names
  `account_login_expired` instead of a generic provider error. A captain's
  authentication failure that names no quota or credit limit refuses its
  account as `account_login_rejected`, records a `usage_account.login_rejected`
  event and an error signal, re-routes the mission, and no longer starts a
  timed quota hold or quarantines sibling captains. A refused account drops its
  stale windows from the status, is not routed or launched, and recovers on the
  next successful usage read or a new login. Account holds name their kind
  (`quota`, `credit`, `auth`) in `holdKind`, and a usage read in which the
  provider serves ordinary usage ends a quota hold early.

- Objective preflight Q1 resolves parent-relative citations only through a
  sibling that is both declared by the target vessel and required by the
  objective. It checks the cited file against that sibling repository's
  current verified tip and keeps ordinary target-repository citations on the
  target revision.

- Objective dispatch keeps long, numerous, and multiline acceptance criteria
  complete for the Judge evidence gate. Mission description, metadata, and
  total-budget trimming preserve complete-output references and digests even
  when large contracts, diffs, and voyage board notes consume the narrative
  budget. Quoted control markers cannot replace the objective contract, and
  operator-supplied handoff markers render as text. Same-objective retries add
  the brief unless it is the last complete frame before the first handoff marker;
  the exact frame comparison normalizes line endings across hosts. Retries keep
  generated handoff markers. Criteria that differ by letter case remain separate
  requirements.

- Git source anchors exclude the reserved generated briefing tree while keeping
  ordinary tracked and absent repository paths in the anchor report.
- Rescue evidence reads support long configured stage chains through a visited
  dependency walk that preserves owner, voyage and vessel boundaries.
- Stored objective stage skips require an explicit authenticated operator
  confirmation with server-recorded identity, time and proof. Legacy skips block
  dispatch until reconfirmed. Changed stage lists or reasons clear prior proof;
  unchanged preparation round trips retain it.
- REST refuses mission-scoped writes to objectives and backlog, including
  reorder and GitHub import. It also refuses linked planning, voyage,
  refinement, release, deployment and incident writes before route side
  effects. Reads, enumerate requests, owner roles in internal workflows, and
  MCP authorization keep their existing rules.
- Judge rescue briefs protect report-only Evidence and Residual Risks sections,
  retain their actionable findings, and state when protected feedback exceeds
  the embedding cap.
- Mission status replies redact operator review and failure text before returning
  it through an authorized read.
- Mission status and full-description reads apply the same caller evidence scope
  as mission output before returning metadata, scope text, or context events.
- Global operators can inspect retained preflight input by event id through
  bounded pages with digest validation; incomplete scans and unrecorded Q4
  rationale are explicit, and no objective or mission ownership fields leave
  the host-local sample store.
- Judge rescue briefs use the full redacted output artifact, keep required review
  findings ahead of completed criteria, and mark bounded excerpts incomplete.
  Every rescue stage receives the failed-output reference; description, metadata,
  and total-budget trimming keep its exact artifact ID, length, digest, and
  generated read notice together. Later stages can read that parent through the
  same-owner rescue chain.
- Objective MCP tools expose stored preflight answers and stage-skip confirmation
  in the preparation schema. Update descriptions state that a supplied
  preparation replaces the full nested object, so clients can send each value
  that must remain when they change repository anchors.
- Cursor usage routing reads Cursor's own models correctly: models matching the
  account's `cursorModelPrefixes` (default `composer-`, `grok-`, `cursor-grok-`)
  draw on the Cursor-models pool, read Low instead of Exhausted while they spill
  into the third-party pool, and never take the third-party API-pool lead.
- Eligible automated Checks for active voyages can finish during a dispatch hold,
  so existing voyages can pass their Check gate and drain. Checks for idle vessels,
  completed voyages, and other unrelated work remain deferred; new mission and
  rescue dispatches remain blocked.
- Smart Routing persona model lists are authoritative: a captain whose model is
  on none of a persona's lists no longer takes that persona's work when the
  listed captains are busy; the mission waits with
  `persona_model_list_excludes`, and the dispatch preview counts the same
  captains. A Cursor captain with API-pool usage leads only its own list group.
- Captains no longer inherit admiral secrets: a local launch removes provider and
  typed-decision key variables, configured `captainEnvironmentScrubNames`, and
  credential-named `ARMADA_` variables before applying its own, and Claude Code
  captains are refused the bare environment-listing commands.
- Stored-prompt upgrade coverage uses a fixed historical fixture instead of
  deriving its prior input from the current prompt.
- Premise checks identify a selected supplied fact for a positive absent-symbol
  reading, or return an explicit unknown identity, using centrally redacted values.
- Default TestEngineer instructions honor explicit objective limits on test-file
  changes while preserving required, permitted validation.
- Mission tokens can read scoped report-chain output and full authorizing
  objectives without inheriting administrator operator tools.
- Voyage status now applies caller scope to the voyage record, summary counts,
  assignment totals and mission details. Mission tokens remain limited to their
  own voyage and its same-owner missions, even when the owner is an administrator.
- Papercut listings expose continuation pages, source and result totals, and
  scan truncation. New arrivals cannot displace the page set; changed retained
  events or merged results require a restart.
- Added-test coverage extraction accepts recognized test attributes and excludes
  unannotated helper methods from its bounded candidate list.

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

- **Each captain launch gets its own temporary directory, removed on exit:**
  agent CLIs and the builds and tests they start wrote to the shared temporary
  directory and left files behind. The OpenCode CLI unpacked a native library
  of about 5 MB there on every run, several hundred a day, and nothing removed
  them. A launch whose caller sets TMPDIR keeps that directory.
- **Report-only reading rules no longer cut a full study short:** the
  context-conservation section of a Research or Audit brief defers to the
  mission's scope, lets a large file be read in chunks to its end and re-read
  after compaction, and reports unfinished required work as unmet acceptance
  criteria. It forbade reading the same path twice and called a partial report
  a success, which captains cited to leave mandatory files unread.
- **A captain's MCP tool list is the captain set:** a mission token reaches the
  caller-scoped tools and the read-only voyage tools, even when the mission's
  owner is an administrator. An administrator-owned mission was listed every
  operator tool, and each model request carried about 51,000 tokens of tool
  definitions; a self-hosted model behind a 60-second gateway could not read
  the first request in time.
- **A voyage ends on its last verdict:** the voyage completion sweeps, and a
  stage's own completion, keep a voyage open while any of its missions is still
  in completion handling. A Judge reads WorkProduced from the moment its captain
  exits until its verdict is applied, and a sweep in that window could write the
  voyage Complete; the later NEEDS_REVISION then left a Complete voyage beside a
  Failed Judge, because a Complete voyage is never rewritten. The landing drain
  also leaves such a voyage alone, so it no longer queues a stage for landing on
  a Judge PASS that the Judge's own handler has not accepted yet.
- **Unfinished in-scope work no longer reads as an owner question:** the
  working personas' blocked-path guidance keeps `[ARMADA:RESULT] BLOCKED` for
  what a captain cannot resolve itself (missing context, a false premise, a
  question only the owner can answer) and asks it to commit finished work and
  end COMPLETE with a `Remaining work:` list when in-scope work remains, so the
  next stage continues it instead of the voyage stopping for the owner. Rows
  that still carry the first wording get the current one; operator edits stay.
- **A voyage is judged against the objective it was dispatched for:** the
  handoff-outcome decision, its partial-handoff note and the linked-objective
  reads resolve a voyage's objective by the objective named in its brief, and
  otherwise prefer the most specific objective over an umbrella that lists the
  same voyage. Taking the first listing objective could pick the umbrella, so a
  child step was checked, and its later stages briefed, against the umbrella's
  acceptance criteria.
- **The landing sweep no longer repeats a step beside a processing pass:** the
  periodic merge-queue landing sweep takes the vessel's repository slot that
  entry processing holds, skips a vessel another pass holds, and re-reads the
  entry once it holds the slot. Before, a sweep could start a second test run of
  an entry already in Testing; that run outlived the landing, held the host
  build slot, and then marked the landed entry failed.
- **Two vessels that declare each other as siblings no longer deadlock their docks:**
  dock provisioning releases the vessel's own repository lock once its worktree
  exists, before it provisions sibling repositories. Holding it across sibling
  provisioning made two such docks, provisioned at the same moment, wait on each
  other's repository forever, so both missions stayed in provisioning.
- **A Judge revision asked for before its Checks finished waits for them:** a
  NEEDS_REVISION given while the independent Checks at the reviewed commit are
  unresolved is held, and once they resolve (or the wait budget ends) the Judge
  runs again with their result, once per mission and without spending recovery
  budget. A NEEDS_REVISION after that re-run is recorded as before.
- **A vessel test that only fails under the loaded suite no longer fails its
  gate:** when the `flake_score` decision does not re-run a red unit-test suite,
  the definition-of-done gate re-runs the failing classes once in isolation (at
  most three), as it already did for consumer suites, and that result stands
  (`RerunFailingClassesOnce`, default on). A red that is not re-run states why in
  the failure text, and one that fails again alone says so.
- **Checks no longer starve behind other vessels' gates:** the host-wide build
  and test slot is granted in request order and names its holder, and a
  definition-of-done gate takes it per command instead of for its whole
  sequence of builds, tests and consumer suites. `run_check` returns an
  unfinished Check after two minutes instead of timing out the caller; the
  Check keeps running. Check views carry `SlotWait` (state, place in line,
  holder, guidance), and the automatic Check runner logs running and waiting
  Checks separately.
- **Bounded image retention on rebuild:** `rebuild-local-image.sh` keeps the
  newest three retention sets after a successful build and removes older sets
  by tag (`ARMADA_RETAINED_KEEP` sets the count). Each rebuild retained two
  full images with no bound before.
- **One re-run after a model gateway timeout:** a captain whose model request
  failed with an HTTP 502 or 504 that says the gateway gave up waiting (for
  example a cold self-hosted model load) re-runs its mission once and records
  `mission.model_gateway_retry`. A second such failure fails the mission as
  before.
- **Quieter git log, working prior-art samples:** `merge-base --is-ancestor`
  answering "no" (exit 1, no error text) is logged at Debug, not as a failed git
  command; a missing ref still warns. The prior-art sample pass prints only the
  matched text, so a common term found on a minified file's megabyte-long line
  returns its path and line instead of failing the output limit.
- **Code search and prior art say when they could not look:** a mission code
  search on an unindexed or failed index returns a `Readiness` remedy naming
  `armada_index_update` and records `code_index.search_unavailable` once an
  hour per vessel. `armada_check_prior_art` returns each candidate's path and
  ref with its readings, plus `searchComplete` and the surfaces whose search
  failed, which the decision state also carries.
- **Docks and landings that cleanup or a failure left behind:** the orphan-dock
  reaper keeps the dock of a mission awaiting review; stall recovery keeps the
  dock of a mission whose work is produced, its pull request open, or under
  review; the disk sweep skips its orphan-dock scan when the protected docks
  cannot be read. A direct landing pushes to origin before it advances the bare
  target, and an exhausted drift retry records `landing.origin_push_failed`
  and opens an incident. A merge-queue error while preparing the integration
  worktree fails the entry as `integration_setup_failed` instead of leaving it
  in Rebasing.
- **Handoffs keep a stage's report essentials; rescues release their
  dependants and close their objective:** each handoff block ends with the
  stage's complete-output reference and digest, verdict, blocking findings,
  follow-ups and added tests, which survive the preview, the description cap,
  compaction and the rescue brief. A cross-voyage dependency on a cancelled or
  failed stage moves to the completed stage of a rescue of its voyage
  (`mission.dependency_rewired`). A rescue of a failed stage recovers the failed
  stages above it in the same chain, so the objective reconciles to Completed.
- **Objectives can forbid native memory writes:** a captain's launch
  credential names its mission, and `create_memory` and `update_memory` refuse
  with `native_memory_forbidden` when that mission's objective carries the tag
  `no-native-memory` or a rollout constraint or non-goal saying the work writes
  no memory. The rule covers rescue stages descended from the objective, so a
  Recorder under such an objective reports only. Other objectives and operator
  credentials are unchanged.
- **Architect plans keep their blocks:** stored output redaction treats a
  quoted span that is not a JSON string as prose, so markdown whose quotes pair
  across lines keeps its text and its `[ARMADA:MISSION]` lines; key-shaped
  values and sensitive JSON properties are still removed. The pipeline handoff
  and `armada_parse_architect_output` read mission blocks through one grammar
  that accepts `[ARMADA:MISSION-END]` and `[/ARMADA:MISSION]`. The
  numbered-line fallback runs only for output with no mission marker and
  records `mission.architect_plan_fallback`.
- **Rescue briefs carry every blocking finding:** a rescue for a reviewer
  rejection lists every item the review marks `NOT DELIVERED`, `NOT MET`,
  `NOT RESOLVED` or `Blocking`, each whole, within a 4,000-character bound,
  before the bounded review excerpt. Findings in the middle of a long review
  are no longer cut away.
- **Landings that do not reach origin are loud and keep their work:** a merge
  queue landing that does not reach origin records a
  `merge_queue.origin_push_failed` event and opens one High incident per vessel
  and target, naming the branch, the git error, and the bare, origin and
  integration heads. The branch sweep counts a branch as landed only when origin
  also contains it (`KeptUnpushed`). A landing retry whose dock was reclaimed
  lands from a temporary worktree at the mission branch tip instead of being
  refused as `landing_evidence_unavailable`.
- **Planner guard and fan-out base for recovery tips:** the planner
  committed-code guard measures what an Architect or Product Manager changed
  from its provisioned start commit to its final commit, so code inherited from
  a recovery tip is no longer reported as `planner_committed_code`. When a
  non-target start commit cannot be resolved, the guard fails closed and says
  so. Workers fanned out from an Architect that started off the target branch
  are cut from the Architect's start commit and verified to contain it.
- **A usage provider's error reply no longer aborts captain exit handling or dispatch:** a Codex
  account whose login the provider rejects answers the usage read with an error, surfaced as
  InvalidDataException, which the collector did not catch. The fault escaped the shared in-flight read
  into every awaiting caller, so a captain's exit handling stopped before its stage handoff and a
  pending-mission dispatch sweep failed. The error is now recorded on the account like any other
  collection failure (`usage_collector_provider_error`).
- **An automatic incident write no longer undoes an operator close:** every automatic writer
  (the incident lifecycle sweep, autonomous recovery) acts on an incident it read as active. When the
  incident is Closed or RolledBack by the time the write lands, the write leaves it unchanged, so a
  stale sweep can neither reopen it nor replace the operator's cause and notes. The lifecycle sweep
  logs the skipped move instead of emitting a move event. An operator can still reopen by hand.
- **A provider's own verdict outranks its usage meter:** the Codex collector reads the
  provider's `ordinaryUsageAllowed` and `rateLimitReachedType`. A spent window whose provider still
  serves ordinary requests makes the account Reserve (demoted, still routable) instead of Exhausted,
  and a provider that refuses ordinary usage makes it Exhausted. With no verdict reported, the
  measured windows decide as before.
- **An operator can retire a seeded pipeline:** the startup seed leaves an
  inactive built-in pipeline inactive instead of reactivating and rewriting it, so a
  deactivated built-in stays retired across restarts. A built-in pipeline that
  neither the code nor `additionalPipelines` declares any more stops being built
  in at the next start and can then be deleted. Deleting a pipeline the seed
  still declares is refused with a reason that names deactivation as the
  durable retirement.
- **A retired pipeline is refused by name, never replaced:** a dispatch whose
  requested pipeline, vessel default or fleet default is inactive is refused
  with `pipeline_inactive` and the pipeline's name and source, instead of
  running it. Setting an objective's suggested pipeline or a vessel or fleet
  default to an inactive pipeline is refused on every write surface. A rescue
  that would run on an inactive pipeline is not created, spends no attempt, and
  names the pipeline on its incident.
- **A deleted pipeline no longer breaks status and subscriptions:** deleting a
  pipeline clears it from every objective that suggests it through the
  objective update, so each objective gets a new snapshot. The objective
  snapshot backfill runs once per admiral process instead of on every status
  read, drops a suggested pipeline that no longer exists
  (`objective.snapshot_pipeline_dropped`), and names and skips one objective it
  cannot restore (`objective.snapshot_restore_skipped`) instead of failing every
  read. A subscribe whose snapshot fails gets `subscribe.error` and can be sent
  again on the same connection. `scripts/autonomy/watch-armada.mjs` prints
  every hub error frame, reconnects after a refused subscribe, and exits 1
  after five refusals in a row.
- **Pipeline stages have no optional flag:** `IsOptional` is gone from the
  stage model, `create_pipeline` and `update_pipeline`, the REST and MCP
  references and the dashboard, and its column is dropped (SQLite 112,
  PostgreSQL 116, SQL Server 108, MySQL 105). Nothing read it, so a stage marked
  optional still ran. Leave a stage out by removing it or with a recorded stage
  skip.
- **A held Pending stage names its hold:** the assignment pass records why it
  holds a Pending mission in `AssignmentHoldReason`, with the holding record in
  `AssignmentHoldEntityId`, persisted on every provider (SQLite 111, PostgreSQL
  115, MySQL 104, SQL Server 107) and shown by mission status, voyage status and
  the dashboard mission page. A stage waiting while its predecessor's
  definition-of-done gate re-verifies declared consumers reads
  `dependency_completion_in_flight`; before, only a repeated log line said so.
  Each named hold records `mission.assignment_held` once when it starts or
  changes and `mission.assignment_hold_released` once when it ends, never one
  event per assignment tick.
- **The latest record of a Check decides every Check gate:** the Judge gate, the
  voyage completion gate and the manual completion proof read, for each Check
  (same voyage, mission, type, workflow profile, executed command and commit),
  only its most recently created record that is not canceled. The label does not
  identify a Check, so an operator re-run of the same command under another label
  supersedes the failed record, and an abbreviated commit hash matches the full
  one. A run of a narrower command stays a separate Check. A retry that is still
  running holds a Judge PASS instead of rejecting it, and a retry that passed
  clears the failure it re-ran. A canceled retry decides nothing, so the failure
  it was meant to replace still stands.
- **Production summary landing evidence and scan scope:** a delivered tip counts
  as landed on a mission-linked `Landed` merge entry or a `Landed` attempt fact,
  so direct landings (`LocalMerge`, merged pull requests) verify as well as
  merge-queue landings. The readiness scan reads only `objective.snapshot`
  events and reports the source as `objective_snapshots`, so other high-volume
  events no longer exhaust the record limit and mark the summary incomplete.
- **The merge queue keeps LocalMerge landings on the local target:** a vessel
  whose effective landing mode is `LocalMerge` merges into the local target and
  pushes nothing, so its local target is legitimately ahead of origin. Integration
  setup, the post-push sync and the failed-landing rollback reset the local
  target to origin only when the move fast-forwards or the vessel lands by
  pushing; otherwise they keep the ref and log a warning that names the kept
  commits, and the post-push sync records a `target_ref_sync_skipped` event. A
  queue entry whose mission commit is already on the target (and on origin for a
  pushing vessel) is `Cancelled` as already landed instead of failing as a no-op.
- **Restarting a mission of an ended voyage is refused:** REST, WebSocket and
  MCP restart share one rule that refuses a mission whose voyage is `Complete`,
  `Failed` or `Cancelled` with `code: "voyage_ended"`, names the voyage and its
  status, and changes nothing. Assignment cancels a `Pending` mission of an
  ended voyage, so the restart would report success and then be undone;
  dispatch a new voyage or a rescue instead.
- **A voyage ends Complete only when its work landed or was never meant to
  land:** a `Failed` voyage becomes `Complete` (`failed_voyage_landed`) only
  when at least one mission is `Complete`; a failed stage that is later
  cancelled keeps it `Failed`. A live voyage with a cancelled stage and no
  `Complete` mission ends `Failed` (`nothing_landed`), and one whose every
  mission was cancelled ends `Cancelled`. Voyages that land work, report-only
  voyages whose missions complete, and voyages that ask for no landing still
  complete.

- **A captain brief carries only what its mode and persona can use:** an Audit
  or Research brief leaves out `engineering` and `testing` skills and states
  Git Anchors as facts, not new work or prior art; the memory read-first rule
  no longer assumes the mission edits; Runtime Signals verdict lines reach only
  a Judge and the Architect rule only an Architect. The output contract is
  stated once in Mission Instructions, not also in the captain instructions and
  the launch prompt, so a Judge without captain instructions now receives it.
  `captainInstructionByteBudget` defaults to 65,536 (0 selects the default),
  and an over-budget brief records a `mission.prompt_over_budget` warning.

- **A retried mission's sibling checkouts are removed when its last dock is
  reclaimed:** a superseded dock whose nested path now belongs to the live
  retry releases its sibling leases instead of keeping them, and sibling
  removal ignores a lease held by a dock that is already reclaimed. Before,
  the leftover lease kept every sibling clone of the mission on disk after
  both docks were gone.
- **The dispatch preview test harness deletes its temporary repository
  directories** after each test, so repeated unit runs no longer accumulate
  empty `armada-preview-*` folders in the temp directory.

- **A stage is handed off only on a recorded definition-of-done result:** the
  lazy handoff dispatch runs for a dependent whose upstream was never handed
  off, and the dangling-handoff recovery, read the upstream's latest gate
  evaluation. They hand off only when it belongs to the upstream's current
  launch and is `Passed`, `Skipped` or `NotVerifiable`. An upstream whose gate
  never recorded a result, such as one whose completion an admiral restart
  interrupted while the gate waited for the host command slot, is held for
  operator review with a `definition_of_done_not_run:` reason, and its
  dependents wait.
- **Cancelling a mission stops its definition-of-done gate:** a mission cancel
  or a status transition to `Cancelled` stops a gate still running or queued
  for that mission. The gate's command process group is killed, the host-wide
  command slot is released, completion records a `Cancelled` evaluation and a
  "validation cancelled" activity line, and the mission keeps its `Cancelled`
  status.

- **A newly armed Check starts on the next sweep:** the automatic Check sweep
  starts each eligible Check on its own task, up to four in flight, and returns.
  A re-armed or newly armed Check joins the host-wide command slot at once
  instead of waiting until every Check of an earlier sweep finishes. The slot
  still runs one build or test command at a time.

- **A held Judge PASS is released without a new Judge run:** a PASS whose
  independent Checks have not reached a verdict stays `WorkProduced` in a
  `judge_check_wait` hold that names the reviewed commit. The health cycle
  releases it to handoff or landing once every Check passes at exactly that
  commit, rejects it when one fails there, and rejects it as unresolved after
  two hours. A green at another commit never releases it, an operator clear
  is refused, and the Judge runs again only when the reviewed commit changes.
  A held PASS keeps its dock through the orphaned-dock sweep.

- **Dock anchor and preserve refs stay in the vessel bare repository:** the
  `refs/armada/docks/*`, `refs/armada/missions/*` and `refs/armada-preserved/*`
  refs written when a dock is reclaimed are now local ref updates. A dock is a
  worktree of the vessel bare, whose `origin` is the hosted remote, so the old
  push sent every anchor, with mission and dock IDs in its name, to that
  remote. The stage handoff also advances the mission branch in the bare only
  and no longer pushes it. Mission branches reach the remote only through the
  pull-request landing path.

- **Model validation runs on the captain's account login:** creating or
  editing a captain on a usage-routing account probes the model with that
  account's login (`CURSOR_API_KEY`, `CODEX_HOME`, `CLAUDE_CONFIG_DIR` or
  `XDG_DATA_HOME`), resolved by the same helper a launch uses. Before, the
  probe ran on the shared login, so a Cursor edit read as a credit or
  authentication failure for a model the account serves.
- **A rescue keeps the stage skips of the voyage it rescues:** the autonomous
  rescue reads the failed voyage's `voyage.stage_skipped` events, drops those
  stages from the rescue chain through the dispatch skip rule, and records a
  skip event on the rescue voyage with the original reason plus "carried from"
  the voyage. A stage whose persona the failure names still runs, and the
  Judge always runs. Before, a rescue rebuilt the whole objective pipeline and
  re-ran the skipped stages.

- **A merge entry for work that already landed is cancelled, not failed:**
  when an entry's branch no longer exists and its mission commit is already
  in the target branch, the entry is cancelled with that reason. Before, a
  landing-drain entry that raced a landing failed after branch cleanup and
  raised a false "merge failed" alert.
- **A consumer suite red is re-run once in isolation:** when a declared
  consumer's unit-test suite fails inside the definition-of-done gate and the
  flake decision did not re-run it, up to three failing classes run again
  alone and that result stands. `DefinitionOfDone.RerunFailingConsumerClassesOnce`
  turns it off.
- **Every rescue inside a voyage is re-judged:** an automatic rescue of any
  non-planner stage that failed inside a voyage (an analyst, TestEngineer or
  Linter as well as a Worker) now runs in a rescue voyage with the pipeline's
  later stages and the Judge chained after it, starting after the rescued
  stage. Before, only a Worker rescue was chained; any other rescue ran
  standalone and could land through LocalMerge without a Judge.
- **Readiness ignores separators inside double quotes:** the command
  dependency probe splits a command at `;`, `&&`, `||` and line breaks only
  outside double quotes. Before, a quoted message such as
  `echo "...; left unchanged"` made every Check on the profile fail with
  `The command dependency 'left' could not be found`.
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
  that stage leaves it. A stage whose commits change only test files is not
  re-verified; untracked files in the dock, such as the briefing folder, do not
  count as a change.
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
  stores a CORE_RULE_5 violation with the secret replaced by `<redacted>`. The
  exemption recognises SHA-256 hex and SRI digests, and the base64 SHA-512
  `contentHash` values NuGet writes in `packages.lock.json`, so adding a NuGet
  package no longer fails the landing.
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
