# Complete MCP Tool Catalog

This catalog groups the tool names registered in
`src/Armada.Server/Mcp/Tools`. Some names are compatibility aliases.
Availability depends on enabled services and the authenticated caller. Discover
the running catalog with `tools/list` and follow every `nextCursor`.
Tool lists use private cache scope and a five-minute lifetime. Only the
requesting caller's client can cache its filtered catalog.

Risk labels:

- **Read**: no intended state change.
- **Write**: creates or updates Armada state.
- **Execute**: starts work, a command, landing, or rollout.
- **Interrupt**: stops or cancels active work.
- **Destructive**: deletes, purges, restores, or stops the server.

### 8.1 Status, Enumeration, Jobs, And Diagnostics

| Risk | Tools |
| --- | --- |
| Read | `armada_status`, `armada_enumerate`, `armada_job_status`, `armada_captain_diagnostics`, `armada_unlanded_branches`, `inbox` |

#### Needs-you inbox

`inbox` returns everything across the fleet that is waiting on a decision or
intervention from the operator, ordered most-urgent first. It answers "is there
anything waiting on me?" in one call instead of polling each entity. Three kinds
of item qualify:

- **Human-in-the-loop**: a mission in Review to approve or reject, a Judge PASS
  held for operator review (`judge_pass_held`; resolve it with
  `armada_review_hold`), a stage handoff held by the `handoff_outcome` hold
  (`handoff_outcome_hold`; resolve it with `armada_handoff_hold`), or a
  deployment pending approval.
- **Human-out-of-the-loop**: a failed mission, a mission whose work could not
  land, a failed merge, a failed or verification-failed deployment, or a
  stalled captain.
- **Ask approvals**: the caller's own pending saved Ask action proposals
  (`ask_proposal`). The item is a read-only pointer; the exact approval fence in
  the conversation is the only place to approve or reject. A captain's mission
  credential, a saved-chat credential and every other user receive none.

Purely informational state changes are excluded. Each item carries a `kind`,
a `severity` (Critical, Warning, or Info), a title, detail, the referenced
entity, and a dashboard href for one-click navigation. An empty list means
nothing currently needs attention. The same surface is available as the
dashboard `Needs You` page and the `armada inbox` CLI command (`--critical`
shows only critical items).

### 8.2 Fleets And Vessels

| Risk | Tools |
| --- | --- |
| Read | `armada_get_fleet`, `armada_get_vessel`, `armada_suggest_vessel_toolchain`, `armada_discover_vessel_checkouts` |
| Write | `armada_create_fleet`, `armada_update_fleet`, `armada_add_vessel`, `armada_update_vessel`, `armada_update_vessel_context`, `armada_import_vessel_checkout` |
| Destructive | `armada_delete_fleet`, `armada_delete_fleets`, `armada_delete_vessel`, `armada_delete_vessels` |

Local checkout discovery and import require an unscoped global administrator.
Only configured direct child checkouts can be imported. Import leaves the
checkout and Git references unchanged. See [Local checkout import](VESSEL_CHECKOUT_IMPORT.md).

### 8.3 Captains

| Risk | Tools |
| --- | --- |
| Read | `armada_get_captain`, `armada_get_captain_log` |
| Write | `armada_create_captain`, `armada_update_captain`, `armada_bench_captain`, `armada_unbench_captain`, `armada_probe_captain_model` (global administrator; sends one small prompt to the captain's model and records endpoint health, without a mission) |
| Interrupt | `armada_stop_captain`, `armada_stop_all` |
| Destructive | `armada_delete_captain`, `armada_delete_captains` |

Single stop, emergency stop, deletion and restart share one captain
administration service with REST and WebSocket. `armada_stop_captain` stops a
Planning or Refining captain through its active planning or objective refinement
session, and stops the process of any other captain and recalls it to Idle; the
mission it was running ends Cancelled with an `operator_stopped:` reason (see
[REST API](REST_API.md)). `armada_stop_all` stops every working captain, active
planning session and active objective refinement session and returns
`all_stopped`, or `stopped_with_failures` with stopped and failed counts and each
failure named. `armada_delete_captain` and `armada_delete_captains` refuse a
captain that is Working, Planning or Refining or owns an Assigned or InProgress
mission, and remove the deleted captain's events, planning sessions and
refinement sessions. REST `POST /api/v1/captains/{id}/restart` (the dashboard
**Restart** action) resets a captain's runtime state in place and keeps its
identifier, configuration, credentials, endpoint, playbooks and any hold.

Manual holds and releases share one quarantine service. REST
`POST /api/v1/captains/{id}/quarantine` and MCP `armada_bench_captain` hold a
captain with a required reason and an optional expiry (none is an indefinite
hold). REST `POST /api/v1/captains/{id}/unquarantine`, MCP
`armada_unbench_captain` and Captain Detail **Lift Quarantine** release it. The
hold is a conditional database write: it applies only while the captain is Idle
(or already held) and owns no mission, dock or process, so a bench never strips
work from a running agent. A refused hold returns `Busy` (HTTP 409); stop the
captain first. A release applies only to a quarantined captain and never forces
a Working captain to Idle. The expiry sweep and quota probe release only a
hold that is still timed (the sweep also requires the expiry to have passed),
so an indefinite hold placed while a probe runs survives. A provider spend cap
holds the failing captain and every idle captain on the capped model; a busy
sibling keeps its mission, dock and process and is held when its own run
returns the cap. The captains list and Captain Detail show the hold reason and
expiry and offer **Quarantine** and **Lift Quarantine**.

### 8.4 Voyages And Missions

| Risk | Tools |
| --- | --- |
| Read | `armada_voyage_status`, `armada_mission_status`, `armada_mission_output`, `armada_get_mission_diff`, `armada_mission_change_summary` (caller-scoped; the stage's recorded commit pair, no dock), `armada_mission_attempts` (caller-scoped; captain, runtime, model, runtime version and outcome of each attempt), `armada_get_mission_log`, `armada_battery_packs`, `armada_battery_runs`, `armada_battery_run` (global administrators; fixture battery packs and runs) |
| Write | `armada_create_mission`, `armada_update_mission`, `armada_transition_mission_status`, `armada_reconcile_terminal_voyage_missions` (`dryRun=false`; see 8.26), `armada_review_hold` (`action=fail`), `armada_handoff_hold` (`action=rescue`) |
| Execute | `armada_dispatch`, `armada_restart_mission`, `armada_retry_landing`, `armada_review_hold` (`action=clear` lands or hands off the held PASS), `armada_handoff_hold` (`action=release` or `mail_release` hands off the held stage) |
| Interrupt | `armada_cancel_mission`, `armada_cancel_voyage` |
| Destructive | `armada_purge_mission`, `armada_delete_missions`, `armada_purge_voyage`, `armada_delete_voyages` |

`armada_transition_mission_status`, the WebSocket `transition_mission_status`
command, and `PUT /api/v1/missions/{id}/status` share one operator transition
path. A manual `Complete` meets the same gates on all three: review approval for
a mission in `Review`, Judge authority for a terminal stage, captain process
release, participating Check state, and target ancestry of the mission commit
when no active dock will land it. A refusal names its reason, for example
`Manual completion blocked: manual_completion_ancestry_unavailable`, and leaves
the mission unchanged. Treat the reason as the finding. Do not retry the same
request on another surface.

`armada_review_hold` resolves a Judge PASS held for operator review by the
`review_substance` decision. It takes `action` (`clear` or `fail`),
`missionId`, `reason`, and `operator`, all required, and refuses any caller
other than a global administrator. `clear` releases the hold and runs the
handoff or landing the hold stopped; `fail` fails the mission and cancels its
dependent stages. Each records `mission.hold_cleared` or `mission.hold_failed`
naming the operator and reason. It also rules on a Judge NEEDS_REVISION or
FAIL held for a criterion conflict (inbox kind `criterion_review_hold`): `clear`
lands the work as it stands, `fail` ends the mission `Failed` with the
`[judge:criterion_conflict]` rescue block, and either ruling closes the
criterion-review incident. A mission that is not held is refused with
`not_held`. A held mission keeps its voyage open. Under a voyage that has
already ended, the decision settles the mission and leaves the voyage status
and completion time unchanged.

`armada_handoff_hold` resolves a stage handoff held by the `handoff_outcome`
hold. It takes `action` (`release`, `mail_release` or `rescue`), `missionId`,
`reason`, and `operator`, all required, and refuses any caller other than a
global administrator. `release` hands the stage off unchanged; `mail_release`
prepends the unmet-criteria note to the next stage's brief and hands off;
`rescue` fails the held stage and cancels its dependent stages, and autonomous
recovery decides the rescue. Each choice records a `typed_decision.labelled`
event joined to the decision on `state_sha256`. A hold nobody answers is
released with the note after the configured timeout and labelled
`hold_timeout`. `armada_review_hold` refuses this hold with
`handoff_outcome_hold`.

One rule decides when a voyage ends. Three paths apply it: mission state
changes, the health-loop completion check, and the landing drain. They all
reach the same answer for the same voyage. The rule also raises the voyage
completion hook (the dashboard broadcast and the objective scheduler refill)
exactly once for each voyage it ends, on every path. The rule runs these steps
in order:

1. `Complete`, `Failed` and `Cancelled` are the terminal set of the voyage.
   Missions, merge entries, landing jobs and Checks each have their own
   terminal set. A `Complete` or `Cancelled` voyage stays as it is. A `Failed`
   voyage can move only to `Complete`. That happens when steps 2 to 4 would
   complete it and at least one mission is `Complete`, for example after a
   retried landing lands its failed work. A failed stage that is later
   `Cancelled`, or work that is produced but never landed, keeps it `Failed`. A
   `Failed` voyage is never written `Failed` again, so its completion time does
   not change. It never returns to `Open` or `InProgress`.
2. A voyage with no missions stays open. A voyage stays open while any mission
   is `Pending`, `Assigned`, `InProgress`, `Testing`, `Review` or
   `PullRequestOpen`. It also stays open while a `WorkProduced` mission is held
   for operator review. All other mission statuses are finished:
   `WorkProduced`, `Complete`, `Failed`, `LandingFailed` and `Cancelled`.
3. If a finished mission is `Failed` or `LandingFailed`, the voyage becomes
   `Failed`. If every mission is `Cancelled`, the voyage becomes `Cancelled`.
   If a mission is `Cancelled` and no mission is `Complete`, the pipeline was
   cut short before anything landed, and the voyage becomes `Failed`
   (`nothing_landed`). A voyage whose missions are all `WorkProduced` (it asked
   for no landing) goes on to step 4.
4. If a voyage has code missions, its Checks decide the result. This includes
   Checks attached to its missions. A failed Check makes the voyage `Failed`,
   and a Pending or Running Check keeps it open. A voyage with green Checks or
   no Checks becomes `Complete`. A voyage whose missions are all Audit or
   Research skips this step.

The mission path applies the rule to its own voyage at any age, and so does a
retried landing (`armada_retry_landing`, REST `retry-landing`): a `Failed`
voyage whose failed work a retry later lands becomes `Complete` however long
ago it failed. The health loop and the landing drain visit `Open` and
`InProgress` voyages, and `Failed` voyages that ended in the last 24 hours. The landing drain only applies the
completion rule to a `Failed` voyage. It does not enqueue or rescue that
voyage's work.

`armada_restart_mission`, REST `POST /api/v1/missions/{id}/restart` and the
WebSocket `restart_mission` command share one restart. A mission whose voyage
is `Complete`, `Failed` or `Cancelled` is refused with `code: "voyage_ended"`
and a reason that names the voyage and its status, and nothing changes.
Assignment cancels a `Pending` mission of an ended voyage, so a restart there
cannot run. Dispatch a new voyage, or a rescue, for that work instead.

A restarted Judge whose earlier run ended `FAIL` or `NEEDS_REVISION` carries a
bounded, historical excerpt of that run in its description (at most 6000
characters: the opening of the review, then its findings sections). The excerpt
tells the Judge to re-check every finding against the current commit and is not
a verdict. The complete earlier output moves to the mission's preserved runs;
read it with `armada_mission_output`, which lists earlier runs before the new
one. A repeated restart replaces the excerpt, and a rescue brief does not
repeat it.

One ended voyage is reopened instead: a `Failed` voyage whose mission is the
stage that failed on its own (`Failed`, with no terminal-voyage reconciliation
marker), or a downstream stage `Cancelled` with `Blocked by failed dependency`.
A stage an operator stopped (`Cancelled` with `operator_stopped:`) reopens a
`Failed` or `Cancelled` voyage the same way as a failed stage. A stage with a dependency must reach, through `Failed` or `Cancelled` stages, a
stage of the same voyage that handed off work with a commit: one that is
`WorkProduced`, `Complete` or `PullRequestOpen`, or one the terminal-voyage
rule moved to `Failed` or `Cancelled` with `terminal_voyage_work_unlanded`. No
skipped stage may have moved the branch past that commit (it carries the same
commit, or it never started). A failed first stage, with no dependency, runs
again from its own start. Otherwise the restarted stage depends on that stage,
so it runs on that commit, and a stage the terminal-voyage rule had moved
returns to `WorkProduced`. Each skipped stage is recorded `Cancelled` with
`Superseded by an operator restart` and its earlier status and reason; every
later stage `Cancelled` with `Blocked by failed dependency` behind the
restarted stage returns to `Pending`, so the rest of the chain runs again; the
voyage returns to `InProgress`; its Checks are armed again; and a
`voyage.reopened` event is written. A `Complete` voyage, any other stage of a
`Cancelled` voyage, a stage an operator cancelled, a stage the terminal-voyage rule failed, and a
skipped stage that moved the branch stay refused with `voyage_ended`.

`armada_replay_research_voyage` is a separate global-operator operation for an
attempt-preserving full six-role Research replay. Preview with exact
`sourceCommit` and `dryRun=true`; apply the returned fingerprint with a unique
request ID. It archives the prior attempts, resets the same six IDs atomically,
and preserves criteria, source and autonomous recovery budgets. It grants no
stage waiver. `armada_research_replay_output` reads explicitly addressed prior
reports as the owner operator or a mission in that same replayed voyage.
See [Full Research attempt replay](PIPELINES.md#full-research-attempt-replay)
for refusals, history limits, REST routes and durable storage.

`armada_mission_output` pages the authoritative safe report artifact. Follow
`NextOffset` until `HasMore` is false. Then verify `Sha256`, `Finalized`, and
`Complete`. MCP results keep the model's PascalCase property names. A missing page, incomplete artifact, or digest mismatch is an
evidence gap.

### 8.5 Planning, Objectives, And Backlog

| Risk | Tools |
| --- | --- |
| Read | `list_objectives`, `list_backlog`, `get_objective`, `get_backlog_item`, `preview_objective_dispatch`, `list_backlog_refinement_sessions`, `get_backlog_refinement_session`, `get_backlog_planning_session` |
| Write | `create_objective`, `create_backlog_item`, `update_objective`, `update_backlog_item`, `reorder_objectives`, `reorder_backlog_items`, `create_backlog_refinement_session`, `send_backlog_refinement_message`, `summarize_backlog_refinement_session`, `apply_backlog_refinement_summary`, `stop_backlog_refinement_session`, `create_backlog_planning_session` |
| Destructive | `delete_objective`, `delete_backlog_item`, `delete_backlog_refinement_session` |
| Execute | `dispatch_backlog_planning_session`, `armada_decompose_plan`, `armada_parse_architect_output` |

`armada_parse_architect_output` accepts the existing mission markers and opt-in
version 2 or 3 `armada-plan` JSON contracts. The last structured revision must
pass validation; an invalid revision does not fall back to an earlier plan.
The result preserves the final mission's `WaitForOtherMissions` flag for the
explicit parallel integration barrier. Parsing does not dispatch or land work.
See [Structured Architect plans](PIPELINES.md#structured-architect-plans).

An objective and a backlog item are the same record. Each objective tool has a
backlog-named twin that calls the same service operation with the same
arguments and returns the same result. Only the not-found text and the error
`Code` values use the twin's own term (`objective_create_failed` or
`backlog_create_failed`, for example). A refused write also carries `Outcome`
(`Invalid` or `NotFound`), so a caller can tell a missing objective from an
invalid field. Create and update run through the same service methods as the
REST routes; the update tools pass every argument the service reads, including
`autoDispatchEnabled`, and the clearable text fields (`description`,
`category`, `owner`, `targetVersion`, `parentObjectiveId`, `refinementSummary`,
`suggestedPipelineId`, `startFromRef`) declare `emptyStringClears`, so `""`
clears the stored value as it does on REST.

| Objective name | Backlog name |
| --- | --- |
| `list_objectives` | `list_backlog` |
| `get_objective` | `get_backlog_item` |
| `create_objective` | `create_backlog_item` |
| `update_objective` | `update_backlog_item` |
| `reorder_objectives` | `reorder_backlog_items` |
| `delete_objective` | `delete_backlog_item` |

### 8.6 Objective Scheduler

| Risk | Tools |
| --- | --- |
| Read | `armada_objective_scheduler_status` |
| Write | `armada_objective_scheduler_set`, `armada_mark_objective_auto_dispatchable`, `armada_objective_scheduler_clear_stale_pause` |

Scheduler state changed through MCP is persisted to the loaded settings file
and survives an Admiral restart. A pause set through `paused=true` should carry
`pausedBy` (your participant key) and `pauseReason`; both are persisted and
shown by `armada_objective_scheduler_status`. A pause outlives the session that
set it, so without an owner nobody can tell a live deploy window from a
departed peer's leftover.

`armada_objective_scheduler_clear_stale_pause` is the autonomy layer's one
permitted write to the pause: clear only, never engage. It succeeds only when
the pause names its owner, the owner is absent from the coordination presence
window, and the absence exceeds
`autonomousObjectiveScheduler.stalePauseAbsenceMinutes` (floor 30, twice the
presence default; a deploy with verification finishes well inside that). It
wakes every active session and posts a board note naming the stale owner, the
recorded reason, the set time and the measured absence, then clears once and
persists. An unattributed pause is refused and stays an operator's to clear.
The dispatch hold is never touched: it clears itself on a successful redeploy,
so a hold that survives is a deploy that stopped halfway and needs a human.
Pass `dryRun=true` to read the decision and evidence without acting. Scheduler dispatches use the same Build and
UnitTest Check arming path as operator dispatches. A dispatch hold blocks both.
The Admiral also requests a debounced immediate sweep when an eligible objective
becomes ready, a dependency completes, or a terminal mission or voyage can free
a shared lane. These event requests bypass the interval but coalesce burst
traffic. The periodic health-loop sweep remains the recovery path for missed
events. `armada_objective_scheduler_status` reports the process-local
`eventTriggeredSweepCount`.
See `docs/SCHEDULING.md` for eligibility and ordering. Operators maintain
campaign quality and handle work outside the scheduler.

### 8.7 Checks

| Risk | Tools |
| --- | --- |
| Read | `get_check_run` |
| Write | `resolve_check`, `armada_resolve_check` |
| Execute | `run_check`, `retry_check_run`, `armada_suite_lease` |

`armada_resolve_check` is a compatibility alias for `resolve_check`.
`armada_suite_lease` is a mission-credential tool: acquire the same per-vessel
and host-wide full-suite slots the gate and armed checks use before a captain
full-suite run, then release them. See [MCP API](MCP_API.md).

`run_check`, `retry_check_run`, `get_check_run`, and `resolve_check` (with its
`armada_resolve_check` alias) return a BOUNDED view of a check run, not the
complete command log. A build or test log is routinely one to several
megabytes; returning it overran the tool output limit, so the caller received a
truncation error instead of the verdict and had to parse the record out of band
on every call - including the common case where the only question was whether
the check passed.

The bounded view carries what answers that question:

| Field | Purpose |
| --- | --- |
| `status`, `exitCode` | The verdict |
| `testSummary`, `coverageSummary` | Parsed totals, replacing a count grepped from the log |
| `artifacts` | Paths to the per-project `.trx` files, which name individual failures |
| `outputTail` | The last 40 lines, where a failure's cause almost always is |
| `testOutcomes` | Every failing test with its first exception line, and the skipped tests, read from the whole log (standard output and standard error), with the built-in matchers plus the recorded workflow profile's `failedTestLinePattern`; a skip list longer than 100 gives the count and the call for the full log |
| `outputLength`, `outputTruncated` | How much was withheld |
| `outputRetrieval` | The exact call that returns the rest |

Nothing is silently withheld: a truncated view states the full log's size and
names the call that fetches it. Use `get_check_run` with `includeOutput=true`
for the complete record, or `outputTailLines` to widen the tail. `resolve_check`
takes the same `includeOutput` and `outputTailLines` arguments, so resolving a
check with a large log returns the verdict by default and the complete log only
on request.

### 8.8 Merge Queue And Audit

| Risk | Tools |
| --- | --- |
| Read | `armada_get_merge_entry`, `armada_drain_audit_queue`, `armada_list_judge_followups` |
| Write | `armada_enqueue_merge`, `armada_record_audit_verdict`, `armada_backfill_judge_followups`, `armada_record_judge_followup_handled` |
| Execute | `armada_process_merge_entry`, `armada_process_merge_queue` |
| Interrupt | `armada_cancel_merge` |
| Destructive | `armada_delete_merge`, `armada_purge_merge_queue`, `armada_purge_merge_entry`, `armada_purge_merge_entries` |

`armada_backfill_judge_followups` repairs Judge follow-ups lost to a transient
write fault. Pass an explicit `fromUtc` (and optional `toUtc`), run with
`dryRun: true` first, check `incomplete` and `errors`, then run the same range
with `dryRun: false`. A second write pass must report zero `created` rows.

`armada_list_judge_followups` lists the stored Judge reviews that suggested at
least one open follow-up, newest first, with each review's text split into
numbered items (one bullet is one item, numbered from 1; an explicit "none" has
no items). Filters: `vesselId`, `sinceHours` (default 168, maximum 2160),
`limit` (default 25, maximum 200), `offset` and `includeHandled`. It is
read-only. Sweep it like papercuts, so every item gets a home: a fix objective,
an owner decision, or a recorded note.

`armada_record_judge_followup_handled` records that home for one item, so the
next sweep does not re-read it. Address the item by `followUpId` and the
`itemNumber` the listing shows, and pass `kind` (`Objective`, `Decision` or
`Note`) with its `disposition`: the fix objective id, the owner decision, or the
note. `operator` names who handled it and defaults to the caller. The marker
stores the handler and the time and persists on every database provider. An item
holds one marker; pass `replace: true` to correct it. The tool refuses, with a
named `Reason`, a mission caller (`operator_required`), an unknown follow-up
(`follow_up_not_found`), an item number outside the follow-up's items
(`item_out_of_range`), an unknown kind (`kind_invalid`), a blank disposition
(`disposition_required`), an `Objective` disposition that names no objective
(`objective_not_found`), and a second marker without `replace`
(`item_already_handled`).

The listing leaves handled items out by default and reports `OpenItems` and
`HandledItemsOmitted`; a review whose items are all handled is left out too.
Pass `includeHandled: true` to list every item, each with its `Handled` marker
(null for an open item).

### 8.9 Docks, Signals, And Events

| Risk | Tools |
| --- | --- |
| Read | `armada_get_dock`, `armada_list_papercuts`, `armada_list_memory_proposals` |
| Write | `armada_send_signal`, `armada_nudge_voyage`, `armada_mark_signal_read`, `armada_repair_dock`, `armada_dismiss_memory_proposal`, `armada_promote_memory_proposal` |
| Destructive | `armada_delete_dock`, `armada_purge_dock`, `armada_unstick_dock`, `armada_delete_docks`, `armada_delete_signals`, `armada_delete_event`, `armada_delete_events` |

#### Coordination Board (Chatroom)

Every coordination tool resolves a blank room key, `fleet`, and the literal
word `default` to the one shared room (`CoordinationRoom.NormalizeKey`). These
aliases must not create separate boards or hide notes from other sessions.

The coordination board is a shared room where concurrent operator sessions and
the dashboard post short notes about what they are doing, so no session is
surprised by a voyage another session dispatched. Notes are visible through board reads. General notes stay advisory;
voyage-tagged notes can enter the next stage brief on supported providers.
SQLite and PostgreSQL store the board. On MySQL and SQL Server each coordination
tool returns an `Error` naming the provider (`The MySQL database provider does not
store the coordination board; use SQLite or PostgreSQL.`).

**Campaign rooms.** A campaign is an objective tree whose root carries a
`campaign:<name>` tag; the nearest tagged ancestor is the campaign root. Each
campaign has its own room, keyed `campaign-<name>` (lowercased, with characters
outside letters, digits, `.` and `_` reduced to `-`), created on first use like
the fleet room. The fleet room stays the all-hands channel and the default for
every tool.

- A claim on an objective in a campaign, and a note posted with an `objectiveId`
  in a campaign, land in the campaign room when no `roomKey` is given. An
  explicit `roomKey` always wins. Other claims and notes, and captain notes,
  land in the fleet room.
- A mirrored fleet event (voyage dispatched or cancelled, mission completed,
  failed or cancelled) always lands in the fleet room. When the event's voyage
  serves an objective in a campaign, a copy also lands in the campaign room. The
  copy carries no voyage id, so a voyage-tagged brief injection never repeats it.
- Voyage-tagged notes reach the next stage brief whatever room holds them.
- A heartbeat in any room keeps every claim its participant holds alive, in
  every room.
- `GET /api/v1/coordination/presence` returns presence across every room, one
  entry per participant naming the rooms it was seen in. The dashboard presence
  strip shows that aggregate.

- `armada_coordination_post` — post a note. Claim work before you start it;
  report outcomes when you finish. Pass `objectiveId` to route a campaign note
  to its campaign room. Address a note with `toParticipantKey` set to the
  recipient's participant key; a display or author name a key used in the last
  24 hours is delivered to that key (to each key, when several use it), and
  `AddressNotice` on the result names every key the note reaches.
  `authorName` is optional: without it the note carries the caller's
  identity, which is the participant's last board name (the
  `X-Armada-Participant` key, else `authorId`), then the name of the
  credential the call authenticated with, then the participant key. A name you
  pass wins. A call with none of these is refused with an error that names
  `authorName`, `authorId` and the `X-Armada-Participant` header.
- `armada_coordination_read` — read recent notes plus who is active. Read this
  before dispatching voyages or touching incidents so you do not duplicate a
  peer session's work. `SharedParticipantKeys` lists each participant key that
  posted or heartbeated under several names within the presence window and
  switched back and forth; several sessions share such a key. A session that
  sends the participant header reads as that key, and a different
  `participantKey` argument is refused with an error naming both keys, unless
  it is the bridge's configured base key or a name the header key posted or
  heartbeated under in the last 24 hours. The read returns notes addressed to
  your key and to such a name.
  Long broadcast notes come back previewed at 600 characters with
  `Truncated=true`, and the result carries `TruncatedMessageCount` and a
  `TruncationHint`; notes addressed to you (your key or such a name) are
  always whole. Pass
  `messageId` to read one note whole (the result's `Message`), or
  `includeFullContent=true` to read every note whole.
- `armada_coordination_heartbeat` — refresh your presence while working. The
  heartbeat and read responses carry `UnreadWakes` when notes are addressed to
  your participant key: PAUSE and address those before continuing, then
  acknowledge each with `armada_mark_signal_read`. This is how a session inside
  a blocking loop learns it was handed work at the next tool boundary. The
  heartbeat result carries `SharedParticipantKey` when another session uses
  your key under a different name; move one session to its own key.
  `displayName` is optional and defaults the same way as a post's
  `authorName`.
- `armada_coordination_claim` — claim a vessel or objective before non-trivial
  work so peers see who owns it; heartbeats keep the claim alive, and a claim
  expires unless refreshed. `action` is required: a missing action returns a
  `missing_action` error and an unknown action returns an error naming the valid
  values, so a claim call that omits the action never reads back an empty claim
  list as a successful reservation. The header key stands in for `participantKey`, and
  `displayName` defaults the same way as a post's `authorName`. A voyage
  dispatched on a claimed vessel or objective posts a `[claims]` overlap note
  that names each other holder. `armada_dispatch` sends the request's
  participant key with the dispatch, so a claim that key holds is the
  dispatcher's own and is not announced. A dispatch with no participant key
  (the scheduler, REST) announces every active claim.
- `armada_campaign_status` — return a campaign's whole matrix (lanes, slices,
  status, evidence) in one call, with recent fleet notes and, under
  `CampaignRooms`, recent notes from each root's campaign room. Reading it does
  not create a room.

#### Identify your session so wakes reach you on any tool

MCP has no channel that can interrupt a running agent. Armada's MCP transport
is stateless, so the server cannot push, and no client turns an inbound
notification into a model turn. A tool result is the only content a session is
certain to read, so a pending wake rides back on one.

Send the caller's participant key on every MCP request:

```
X-Armada-Participant: <participantKey>
```

Armada then appends an `[ARMADA WAKE]` block to the result of whichever tool
the session calls next, so `armada_status` or `armada_voyage_status` delivers
mail just as `armada_coordination_read` does. Without the header a session is
anonymous and receives no wake — that is deliberate, because the server would
otherwise have to guess whose mail to hand out.

Configure it per client:

- SSH stdio bridge: set `ARMADA_PARTICIPANT_KEY` in the MCP server's `env`.
  `scripts/mcp-ssh-http-bridge.mjs` turns it into the header. The same `env`
  must also set `ARMADA_MCP_AUTH_HEADER_FILE`, or every request is refused.
- Several sessions on one bridge configuration: also set
  `ARMADA_PARTICIPANT_KEY_UNIQUE=1`. Each bridge process (one per client
  session) then sends the configured key plus a random six-character suffix,
  fixed for the life of that process, as `X-Armada-Participant`, and the
  configured key as `X-Armada-Participant-Base`. Each session gets its own
  mailbox: it receives and acknowledges only its own wakes. The session never
  needs the suffix. `armada_coordination_read` and
  `armada_coordination_heartbeat` accept the configured key, or no key, as the
  session's own; their results name the full key as `YourParticipantKey`; and a
  note addressed to the session's display name reaches its key. Without the flag
  every session on the configuration shares one key, and the board reports it
  under `SharedParticipantKeys`.
- Direct HTTP clients: add the header to the server entry's `headers` object.
- Bounded helpers: `scripts/autonomy/spawn-helper.sh` writes the header into the
  generated per-helper config. An `AUTONOMY_CLAUDE_MCP_CONFIG` you supply
  yourself must carry the header, or the helper gets no wakes.

Delivery is not acknowledgement. The banner repeats on every tool result until
the session calls `armada_mark_signal_read`; a wake that stopped appearing
before it was read would be a lost wake. The two coordination tools above are
excluded, because they already return the same wakes in their own payload.

Who may acknowledge a Wake follows who it woke. A session that sends the
participant header may mark read a Wake addressed to it (`[to=<its key>]`), and
the effective AgentWake participant (`armada_agentwake_status`) may also mark
read an UNADDRESSED Wake — a mission-outcome or critical wake prefixed
`[vsl=...]` or `[CRITICAL]` — because that is the session such a wake starts.
Any other authenticated key is refused with the owner named; an anonymous
caller (no header) remains unrestricted. Mail and Nudge signals are consumed by
the stage handoff and are never acknowledged through this tool.

#### Do not wait by polling. Subscribe.

The banner is reliable but not immediate: a session that calls no tool sees
nothing until it does. The instinct is to close that gap with a blocking poll --
a shell `while` loop inside one `ssh_exec` call. Do not. That shape was measured
on one operator session (2026-08-23 23:19Z to 01:50Z) and it costs more than it
looks:

- 68 assistant messages, of which only FIVE carried visible text. The operator
  saw nothing for seven minutes at a stretch, and fourteen minutes before the
  final handoff.
- Three of five turns ended `state=interrupted, stopReason=tool_use`, each within
  four seconds of a failed `ssh_exec`. Two of those restarted the underlying
  session, which is why the run finished one item and wrote a handoff.
- While the loop runs the session makes no MCP calls, so a directed board note
  cannot reach it either. A helper waiting on an answer times out against a lead
  that is technically alive.

The Admiral's WebSocket hub already broadcasts every voyage, mission, incident
and board change. Subscribe to it instead, and let each change arrive as an
event:

Each client has an independent bounded output queue. A slow client does not
delay the event producer or another monitor. If a client cannot keep up, Armada
disconnects it instead of silently dropping events. Reconnect and reconcile
authoritative state after that disconnect.

Each broadcast has a process `streamId` and a monotonic `cursor`. The watcher
keeps the last pair in memory and sends it on reconnect. Armada replays the
available suffix from a bounded process buffer (8,192 events and 32 MiB by
default, set by the `webSocket` settings section). It sends an explicit
`event.gap` if the process changed, the cursor is invalid, history was evicted,
or the reconnect output is too large. The gap names the lost cursor range and a
`GET /api/v1/events` time window to re-read, and the watcher prints both on its
`EVENT GAP` line. The events after the gap continue in cursor order, so the
watcher does not reconnect for it.

Each subscription also gets an authoritative reconciliation snapshot before
Armada enables live delivery. A global watch gets all Open and InProgress
voyages, their missions and Checks, and all captains. `--voyage` gets the exact
voyage, including a terminal voyage, with its linked missions, Checks, and
captains. The global view also includes active standalone missions and their
Checks. The watcher applies this state before it trusts later live events. It
prints `RECONCILED` after each connect or reconnect. It prints a failed or
stalled state from a snapshot once: a reconnect that lists the same state again
prints nothing for it. A scoped snapshot for a
purged voyage is empty and does not stop future live delivery.

```sh
ssh <server> 'ARMADA_API_KEY="$(<read the admiral API key>)" \
    node <armada-checkout>/scripts/autonomy/watch-armada.mjs \
    --voyage <voyage-id> --participant <your-key> --exit-on-terminal'
```

The hub refuses a session that does not authenticate. Set `ARMADA_API_KEY` to
the admiral API key, or `ARMADA_TOKEN` to a bearer credential token. Without
either, the watcher prints the refusal and exits instead of reconnecting.
Do not put the key on a command line that other users can read; export it from
a protected environment file.

Drive it with the harness's Monitor tool, so every line becomes a notification.
Each mission line is a stage boundary, which is the only window where a
correction still reaches the next brief. `--exit-on-terminal` ends the watch when
the voyage finishes. `--all-notes` widens it to the whole board; `--quiet-captains`
drops the stall lines.

The watcher notifies and nothing more. It never reads, acknowledges, or consumes
a wake, so the banner and `armada_mark_signal_read` remain the delivery and
acknowledgement path.

### 8.10 Incidents

| Risk | Tools |
| --- | --- |
| Read | `armada_list_incidents`, `armada_get_incident` |
| Write | `armada_create_incident`, `armada_update_incident`, `armada_close_incident` |
| Destructive | `armada_delete_incident` |

An incident is active until it is `Closed` or `RolledBack`. When recovery,
assignment or handoff checks for an existing incident of a mission or voyage, it
filters out terminal incidents first and reads every matching active incident.
Any number of newer closed incidents cannot hide an older open one, so recovery
updates that incident instead of opening a duplicate. When a voyage is
cancelled, recovery closes every active incident of its failed mission.

Autonomous recovery skips failures in voyages that ended `Complete` or
`Cancelled`. A failure in a `Failed` voyage stays eligible, because a mission
failure is what ends a voyage `Failed`. Cancelling a voyage cancels only its
`Pending`, `Assigned`, `InProgress`, `Testing` and `Review` missions, including
a mission waiting for a review decision and when captain recovery finds the
voyage cancelled. Finished missions keep their status, and
produced work takes its status from landing evidence (section 8.26).

### 8.11 Releases

| Risk | Tools |
| --- | --- |
| Read | `get_release` |
| Write | `create_release`, `update_release`, `armada_update_release`, `test_release_webhook` |

`armada_update_release` is a compatibility alias for `update_release`.

`test_release_webhook` is registered only when the `cdWebhook` setting is
configured. It POSTs a synthetic `release.shipped` payload to the configured
endpoint and returns the delivery outcome, so you can verify reachability and
authentication before approving a real release.

### 8.12 Deployments

| Risk | Tools |
| --- | --- |
| Read | `get_deployment` |
| Write | `create_deployment`, `update_deployment`, `armada_update_deployment` |
| Execute | `approve_deployment`, `verify_deployment`, `rollback_deployment` |

`armada_update_deployment` is a compatibility alias for
`update_deployment`.

`create_deployment` resolves `sourceRef` once to the full commit it ships and
accepts an optional `artifactIdentity`. `approve_deployment` accepts
`expectedSourceCommit` and `expectedArtifactIdentity` and refuses when the
deployment names a different candidate, or when the environment's
`requiredCheckTypes` have not passed at the pinned commit. The rules are in
[DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) section 4.

### 8.13 Runbooks

| Risk | Tools |
| --- | --- |
| Read | `list_runbooks`, `get_runbook`, `list_runbook_executions`, `get_runbook_execution` |
| Write | `create_runbook`, `update_runbook`, `update_runbook_execution` |
| Execute | `start_runbook_execution` |
| Destructive | `delete_runbook`, `delete_runbook_execution` |

### 8.14 Workflow Profiles And Environments

| Risk | Tools |
| --- | --- |
| Read | `list_workflow_profiles`, `get_workflow_profile`, `validate_workflow_profile`, `preview_workflow_profile`, `list_environments`, `get_environment`, `armada_audit_operational_assets` |
| Write | `create_workflow_profile`, `update_workflow_profile`, `create_environment`, `update_environment` |
| Destructive | `delete_workflow_profile`, `delete_environment` |

### 8.15 Personas, Pipelines, Playbooks, And Templates

| Risk | Tools |
| --- | --- |
| Read | `get_persona`, `get_pipeline`, `get_playbook`, `list_prompt_templates`, `get_prompt_template` |
| Write | `create_persona`, `update_persona`, `create_pipeline`, `update_pipeline`, `create_playbook`, `update_playbook`, `create_prompt_template`, `update_prompt_template`, `reset_prompt_template` |
| Destructive | `delete_persona`, `delete_pipeline`, `delete_playbook` |

### 8.16 Code Index, Context Packs, And Graphs

| Risk | Tools |
| --- | --- |
| Read | `armada_index_status`, `armada_code_search`, `armada_context_pack`, `armada_fleet_code_search`, `armada_fleet_context_pack`, `armada_graph_search_symbols`, `armada_graph_get_callers`, `armada_graph_get_callees`, `armada_graph_get_impact`, `armada_graph_suggest_affected_tests`, `armada_graph_get_node`, `armada_graph_get_files`, `armada_graph_explore` |
| Execute | `armada_index_update`, `armada_code_duplicates` (background job; operator-only) |

### 8.17 AgentWake

| Risk | Tools |
| --- | --- |
| Read | `armada_agentwake_status` |
| Write | `armada_register_agentwake_session` |

### 8.18 Dispatch Hold

| Risk | Tools |
| --- | --- |
| Write | `armada_dispatch_hold` |

The hold is fleet-wide: while it is engaged every new dispatch is refused,
whichever session or scheduler asks, and in-flight voyages continue. Engage it
with a reason before an Admiral rebuild; a successful restart clears it by
design. `setBy` names the session that engaged it; without it the hold takes
the caller's participant board name, then the name of the credential the call
authenticated with, and an engage that carries neither is refused. Hold changes require an authorized operator.

The refusal happens at submission, before any job is accepted: every operator
entry point answers `dispatch_hold_active` with `SetBy`, `SetByUtc` and
`Reason` (`docs/MCP_API.md`, "Long Operations", lists them). `status` and
`engage` return `UnfinishedJobs`: background jobs accepted before the hold and
not yet finished; `RunningGates`: definition-of-done gates running or waiting
for the host command slot; `GateReruns`: interrupted gates being re-run; and
`HostSlot`: who holds and who waits for the host command slot, with
`MemoryWaitReason` when free memory holds the next request. A restart records
each unfinished job `Lost` and kills every running gate and check, so wait for
`UnfinishedJobs`, `RunningGates` and `GateReruns` to be empty and `HostSlot` to
have no holder or waiter before restarting.

Autonomous recovery obeys the same hold. A recoverable failure that arrives
while the hold is engaged gets no rescue voyage or mission and spends no
recovery attempt. Its incident's `RecoveryNotes` gets one
`Autonomous rescue deferred: dispatch_hold ...` line per engagement, and an
`autonomous_recovery.rescue_deferred_dispatch_hold` event is recorded. The
first recovery sweep after the hold clears re-evaluates each deferred rescue,
even when the failure is older than the sweep lookback window. Deferrals are
runtime state like the hold itself: after a restart, the sweep picks up only
failures inside its lookback window. A deferred failure whose incident is
`Closed` at or after the failure (by an operator, or by the lifecycle as
superseded or mitigated), with no other incident for it still active, is not rescued when the hold clears or after a
restart: recovery opens no new incident, spends no attempt, and records one
`autonomous_recovery.skipped_closed_incident` event. A failure newer than the
closure is recovered as usual.

A failure whose incident is `Mitigated` with a root cause a person wrote,
mitigated at or after the failure, with no incident for it still `Open` or
`Monitoring`, is treated as an operator's disposition the same way: recovery
dispatches no rescue, spends no attempt, and records one
`autonomous_recovery.skipped_mitigated_incident` event naming the incident. An
incident the lifecycle mitigated over its automatic cause does not stop
recovery. Restarting the mission runs it again, and a failure newer than the
mitigation is recovered as usual.

A failure that an active non-rescue mission on the same vessel names by mission
id in its description is treated as already under recovery by an operator:
recovery dispatches no rescue and records one
`autonomous_recovery.skipped_active_recovery_mission` event naming that mission.
The rescue is evaluated again once that mission is no longer Pending, Assigned,
InProgress, Testing or in Review.

Automatic Checks obey the same hold. The heartbeat runs no Pending Check while
the hold is engaged, records one `check.auto_deferred_dispatch_hold` event per
engagement, and runs the waiting Checks on the first sweep after the hold
clears. `run_check` and `retry_check_run` are not held.

Stall recovery continues under the hold. A relaunch continues an in-flight
mission in its own dock and is not a new dispatch, so when the heartbeat
confirms a stalled captain while the hold is engaged, it stops the stalled
process and relaunches the agent within `MaxRecoveryAttempts`, as it does with
no hold. Later stages of in-flight voyages are assigned the same way.

Empty voyages created through REST, WebSocket or the remote-control tunnel
dispatch no work. Missions added to them later go through the admiral
dispatch, which the hold refuses.

AgentWake is a process-delivery transport, not the work queue or the source of
truth. Put the authorized operator key in `remoteTrigger.agentWake.participantKey` when
addressed process wakes must work after an Admiral restart. A registration with
its own `participantKey` temporarily overrides the configured key and remains
useful for a controlled probe or a resumable Claude or Codex session. An
addressed board note always creates a Wake signal. With delivery mode
`SpawnProcess` or `Both`, a note for the effective participant key also starts
the runtime process. With `StoredWake`, or when no key matches, the signal
remains for the next heartbeat or read. `armada_agentwake_status` reports the
configured key, effective key, delivery mode, runtime, and transient session.

Nothing is pushed under any delivery mode. The row waits until the session next
calls a tool, and the participant header above is what lets that call carry it.
Settings accept only `SpawnProcess`, `StoredWake`, or `Both`; any other value is
rejected with an error that lists them.

OpenCode does not resume an earlier conversation for AgentWake. It starts a
fresh session by design. Put the complete task in the addressed note and make
the bootstrap prompt reconstruct state from the coordination board and durable
memory. Never give the same participant key to a resident process and an
AgentWake registration.

### 8.19 Backup, Restore, And Server Control

| Risk | Tools |
| --- | --- |
| Write | `armada_backup` |
| Destructive | `armada_restore`, `armada_stop_server` |

Backup is provider-native and verified on every provider. Each run restores
its own artifact into an isolated target and checks it before writing the
archive. The manifest reports the configured provider's schema version and
record counts. Install the provider's client utilities on the admiral host;
SQL Server also needs `selfDeploy.sqlServerBackupDirectory`. A failure returns
a named reason and leaves no archive, so a successful `armada_backup` is
evidence of a restorable backup.

Archives do not carry secrets. `settings.json` is written with database and
connection-string passwords, API keys, tokens, provider keys and every other
value the shared redaction rule marks secret replaced by `[REDACTED]`, and the
manifest records `settingsRedacted`. A restore keeps the target host's own
secrets for those values and never writes the placeholder. Secrets that exist
only in the source host's settings must be set on the target by hand after a
restore.

The server image ships the PostgreSQL 16 and MySQL 8.0 clients. SQL Server's
`sqlcmd` must come from a derived image. An image built before those packages
were added has no server clients, so backup and the self-deploy preflight fail
with `native_client_missing_<tool>` (for example
`native_client_missing_pg_dump`). The deploy window that adds them rebuilds
the image with rollback retention. Inside the new container, `pg_dump
--version` must report a major version at least the PostgreSQL server's. One
`armada_backup` must then produce a manifest with the configured
`databaseType`, non-zero `recordCounts` and both verification flags true.
`docs/DOCKER.md` ("Database Client Tools") has the steps.

Restore replaces the database only on SQLite. On PostgreSQL, MySQL and SQL
Server it is refused with `restore_unsupported_for_provider_<type>`. For those
providers, stop the admiral and restore the archive's native artifact with
`pg_restore`, `mysql` or `RESTORE DATABASE` after taking a fresh backup, then
start the admiral and check health and the schema version.

#### In-place server restart

An in-place restart is available at `POST /api/v1/server/restart`. It performs
the same graceful stop as `POST /api/v1/server/stop`, honours the same
`RequireAuthForShutdown` guard, and returns `{ "Status": "restarting" }`. Under a
container restart policy the graceful stop IS the restart: the supervisor
relaunches the admiral once the process exits, with a brief period of downtime
and no orphaned child process. The dashboard Server page offers a Restart Server
button beside Stop Server, disabled in proxy mode and behind the same confirm
dialog. There is no MCP tool for the restart; it is a REST and dashboard action.

### 8.20 Disk Lifecycle

`armada_disk_lifecycle` reports and, when explicitly enabled in settings,
reclaims Armada-owned disk storage. Run `action=scan` for the dry-run report
before anything destructive: it returns bytes per owned category (docks, bare
repos, mission logs, diffs, instruction snapshots, dock metadata, integration
and merge-queue worktrees, temp artifacts, backups, operator evidence) plus
reclaimable counts. Folder sizes count every nested file; symlinks are neither
counted nor followed. A symlinked dock, integration or merge-queue worktree entry
is recorded `skipped` with the reason `symlink or outside allowed roots`, and the
bytes of its target are never counted reclaimable.

Temp artifacts are entries in the admiral's temp directory whose names start
with an Armada prefix (`armada-chk-`, `armada_test_`, `armada-code-index-`,
`armada-model-validation-`, `armada_init_`, `armada-iso-`, `armada-backup-`,
`armada-restore-`) or with a prefix that test runs and builds leave
(`armada-test-home-`, `armada_coverage_`, `armada-tool-profile-`,
`armada-preview-`, `armada-staleness-`, `selfdeploy-provider-`, `MSBuildTemp`).
An entry is reclaimable only past `diskLifecycle.tempArtifactRetentionHours`,
and the checkout of a check run that has not finished is kept.

Operator evidence is the `operator-evidence` folder under the log directory,
where operator proof runs keep receipts, logs and full clones. A folder's age is
the newest write of any file inside it. After
`diskLifecycle.operatorEvidencePayloadRetentionHours` (default 24) a folder
loses its nested `.git`, `bin`, `obj` and `node_modules` directories and keeps
every other file. When `diskLifecycle.operatorEvidenceRetentionDays` is set
above `0` (default `0`, which keeps every folder because objectives and incidents
cite files inside them), a folder idle that many days is removed whole. A folder holding an `.armada-keep` file is
never touched. When free space on the log volume is below
`diskLifecycle.lowSpaceThresholdGb` (default 20; `0` turns it off), the pass
runs in low-space mode and uses `diskLifecycle.lowSpacePayloadRetentionHours`
(default 6) for that payload, logs a warning naming the free bytes and the
threshold, and records a `disk_lifecycle.low_space` event. The report carries
`FreeBytes` and `LowSpace`.

History (mission logs, diffs, instruction snapshots and brief-file folders, dock metadata and stage
records, final messages, whole operator-evidence folders and the configured
`archive.sessionRoots`) is freed only with a verified copy in the history
archive. While no archive is configured it is kept, unless
`archive.keepHistoryWithoutArchive` is false. The report carries
`HistoryMode` (`Archive`, `Keep` or `Delete`), `ArchivedItems`,
`ArchiveReusedItems`, `ArchivedBytes`, `ArchiveFailures`,
`ArchiveFreeFailures`, `ArchiveCapped`, `ArchiveDeferredItems` and
`ArchiveManifestKey`; a pass archives at most
`diskLifecycle.archiveMaxFilesPerPass` files (default 1000) and defers the
rest of the expired history to the next pass, and archives an expired folder of
at least `diskLifecycle.archiveBundleMinFiles` files (default 50) as one
`.tar.gz` object that counts as one file; a pass whose uploads fail
verification keeps each failed item, records a `disk_lifecycle.archive_failed`
event and posts a board note, and an archived item that cannot be removed is
named in `Errors`, recorded as a `disk_lifecycle.archive_free_failed` event and
retried by the next pass without a second upload. Deny-listed files
such as credentials and settings files are never uploaded and never freed.
`docs/ARCHIVE.md` describes the archive.

`diskLifecycle.extraRetentionRoots` keeps operator directories outside
Armada's own storage bounded, such as a folder of deploy database dumps or
rehearsal scratch folders. Each entry names an absolute `path`, a
`retentionHours` (default 72) and a `keepNewest` count (default 3); the list is
empty by default. Each root is its own `extraRoot:<folder name>` category, and
each direct child of the root (file or folder) is one item, aged by the newest
write of any file inside it. A child is reclaimable when it is idle past
`retentionHours` and is not among the `keepNewest` most recently written
children. A child holding an `.armada-keep` file and a symlinked child are
never touched, and the root itself is never removed. A path that is empty, not
absolute, a filesystem root or a symlink is refused and named in `Errors`.

```json
"diskLifecycle": {
  "extraRetentionRoots": [
    { "path": "<backup-dir>", "retentionHours": 72, "keepNewest": 3 },
    { "path": "<operator-scratch-dir>", "retentionHours": 72, "keepNewest": 0 }
  ]
}
```
`action=reconcile` additionally purges stale sibling-worktree leases and, only
when `diskLifecycle.enabled` is true and `diskLifecycle.dryRun` is false,
deletes eligible items. Lease reconciliation takes each lease's lock, drops a
holder whose dock is inactive at once, and drops a holder whose dock record is
missing only after `diskLifecycle.staleLeaseGraceHours` (default 24). Only one
pass runs at a time: a scan or reconcile started while the health loop's pass is
running waits for it, then scans what that pass left. A folder that cannot be
listed is not read as empty: the report's `ErrorCount` counts it and `Errors`
names the path and the error (the recorded `disk_lifecycle.*` event carries both),
so a category with an error has incomplete totals. Reclamation fails closed: only paths under the allowed
roots, not symlinks, past their grace period, and not referenced by active
docks, missions, or merge-queue entries are ever touched. Docker image and
build-cache pruning stays an explicit host-side operator action
(`docker builder prune` with the current and rollback images protected), never
a container-triggered deletion. Before rebuilding a mutable local image tag, use
`scripts/common/rebuild-local-image.sh`; it retains the running image and the
current tag first and prints the rollback references for the deployment record.
Host-side cleanup of build outputs, agent caches, Docker tags and test
databases is described in `DOCKER.md`, "Host storage cleanup".

| Risk | Tools |
| --- | --- |
| Read | `armada_disk_lifecycle` (action `scan`) |
| Destructive | `armada_disk_lifecycle` (action `reconcile`; gated by `diskLifecycle.enabled` and `diskLifecycle.dryRun`) |

### 8.21 Token Usage

`token_usage_summary` summarizes model token usage over a time window: time
buckets with a per-model breakdown, a whole-window per-model aggregate ordered
most-used first, and grand totals for input, output, cached, and total tokens.
Narrow it with `model`, `runtime`, `source` (mission, chat, or planning),
`vesselId`, or `captainId`, and set the window with `sinceHours` or an explicit
`fromUtc`/`toUtc` pair. `bucketMinutes` accepts fractional values.

Input is reported as three buckets for every runtime: `uncachedInputTokens`,
`cacheReadInputTokens` and `cacheWriteInputTokens`. Records stored before the
buckets existed carry no buckets and are counted in `legacyInputTokens` and
`legacyRecordCount`; `inputTokens` and `totalTokens` include them, so compare
runtimes with the bucket fields (see "Token usage input buckets" in
`REST_API.md`).

Read the `estimatedCount` before comparing models. Counts are real only where
the runtime reports usage, and estimated otherwise, so a window mixing runtimes
mixes measured and inferred numbers in one total. Several runtimes report no
usage at all; for those, the admiral-side prompt-byte accounting is the figure
that is comparable across runtimes.

| Risk | Tools |
| --- | --- |
| Read | `token_usage_summary` |

### 8.22 Verified Production

`armada_production_summary` reports verified landed slices for a bounded UTC
window. It also reports raw completed leaf objectives, evidence exclusions,
Check timing, rescue share, and the measures that current records cannot
calculate. First-pass acceptance and rescue share come from durable mission
attempt facts; runs that happened before those facts existed are reported as
historical in each metric's `unknown`, `coverage`, and historical fields.
Post-land consumer and ledger regression rates come only from incidents and
Checks that carry a typed regression purpose; set the purpose, cause,
objective and landed commit on the incident or Check, and read
`regressionCoverage` for unlinked and unattributed records. Repeated research
counts only preparation claims re-established with unchanged evidence and
anchors; revalidation and reuse are separate counts, and slices without claim
observations are reported as uncovered. Check timing separates preparation
delay, pure host-slot wait from the recorded slot request, and execution.
`laneTime` reports eligible idle lane-minutes apart from fleet-capacity and
dispatch-hold time; read its `coverage` and `incompleteIntervals` before
using it. Use
`sourceFamily` and `workType` to select one cohort. Read
`scan.truncated`, `isComplete`, and each metric's availability before you use a
rate. The equivalent REST route is `GET /api/v1/production/summary`.

| Risk | Tools |
| --- | --- |
| Read | `armada_production_summary` |

### 8.23 Native Captain Memory

| Risk | Tools |
| --- | --- |
| Read | `search_memory`, `get_memory` |
| Write | `create_memory`, `update_memory` |
| Destructive | `delete_memory` |

Native memory holds captain working memory: a vessel fact, a prior finding, or a
procedure worth repeating. A record carries a type (Episodic, Semantic,
Procedural), an optional stable key, a salience that orders recall, a version,
provenance, and tags. `create_memory` writes the record with that key in place,
so recording the same finding twice corrects one record instead of scattering
copies. Send `expectedVersion` on a write to be refused instead of overwriting a
newer record.

Two boundaries decide what belongs here:

- The shared external memory repository stays the authority for accepted durable
  rules. When a brief carries a Shared Memory section, an external rule wins over
  a native record on conflict, and a captain reports the conflict instead of
  rewriting either side.
- The memory tools never write to that repository, to repository files, or to the
  vessel model context.

The MCP surface authenticates each request. Memory tools pass that caller to
the memory service, which applies tenant, user, and record-scope permissions.
The feature needs no enablement setting.

### 8.23a Captain typed decisions

| Risk | Tools |
| --- | --- |
| Read | `armada_typed_decision`, `armada_score_items`, `armada_check_premise`, `armada_check_prior_art`, `armada_memory_triage`, `armada_change_quality`, `armada_corpus_prelabel`, `armada_run_custom_decision` |

These are caller-scoped. They redact before egress and edit no Armada record.
`armada_score_items` asks one Noul per listed item and returns the expected
count in code. See `docs/MCP_API.md` and `docs/TYPED_DECISIONS.md`.

### Context, evaluation, and remote jobs

| Risk | Tools | Contract |
| --- | --- | --- |
| Read | `armada_search_context` | Mission-scoped recall over guidance, native facts and indexed code; see [Context Index](CONTEXT_INDEX.md). |
| Execute | `armada_typed_decision_eval` | Run synthetic cases through production adapters and the live provider; records evaluation evidence. Global administrator only. |
| Write | `armada_change_quality_gate` | Read a focused diff and route actionable weaknesses to a Triaged objective. Operator tool; it does not land work. |
| Read | `armada_harbor_jobs`, `armada_harbor_job` | Read jobs visible under runner-owner authority, while Harbor is enabled. |
| Interrupt | `armada_harbor_job_stop` | Stop an authorized Harbor job; requires tenant or global administrator authority. |

Use [MCP API](MCP_API.md#authentication-and-scope) for caller permissions and
[Harbor Protocol](HARBOR_PROTOCOL.md) for runner and job behavior.

### 8.24 The Recorder And Linter Stages

The built-in `Linter` persona runs at the mid tier, before the Judge, in the
pipelines that produce vessel code: `Tested` (Worker, TestEngineer, Linter,
Judge), `ReferencePortingTested` (Worker, PortingReferenceAnalyst, TestEngineer,
Linter, Judge), and `ProductDevelopment`. It flags overengineering and style
before review, so slop is caught across the fleet, not only in
`ProductDevelopment`. `FullPipeline` carries no Linter as the minimal review
shape. `docs/PIPELINES.md` lists the built-in pipelines and their stages, and
the typed `lint_finding` decision (D24) that can route a Linter finding to the
Judge is in the Configuration And Administration chapter.

The built-in `Recorder` persona reviews the finished work of a voyage and records
what is worth remembering through the memory tools above. It is seeded with its
own editable prompt, and the built-in `Recorded` pipeline runs it after a Worker.

Three operator facts:

- **Startup adds the Recorder only where an owner decision has placed it.** The
  built-in `Recorded` pipeline runs it after a Worker, and the built-in
  `ProductDevelopment` pipeline ends with it at the mid tier so it never competes
  for the scarce high-tier captains. Startup adds it to no other pipeline: whether
  the Recorder belongs at the end of `FullPipeline`, `Tested` or any other
  pipeline is an owner decision, not a side effect of a deploy. A pipeline that
  already carries the name `Recorded` is left exactly as it is.
- **A Recorder stage produces no commit, and the completion gate knows it.** The
  Recorder writes memory, not code, so its empty diff is its success shape. The
  no-op completion and ineffective-rescue gates exempt the Recorder persona the
  same way they exempt the Architect, so a Recorder stage is safe as the terminal
  stage of an implementing pipeline such as `ProductDevelopment`, not only in a
  fully report-only voyage.
- **A persona that must commit fails the completion gate when nothing
  changed.** For an Implementation mission whose persona is required to produce
  a commit, no change since the dock was provisioned is a no-op completion, even
  when the captain ran for a long time, wrote a long narration and printed its
  completion marker. Runtime and output length measure narration, not work. The
  gate reads the same persona set as the landing gate, so a reviewer stage that
  approves without committing is not affected, and an Audit or Research mission
  is exempt because its deliverable is a report. When the dock start commit
  cannot be read and no branch diff was captured, the result is unknown rather
  than empty, and an unknown never fails the mission.
- **A revising Worker with nothing to fix completes.** The no-op rule judges a
  stage's own work. A Worker whose dependency chain holds an earlier Worker
  stage (a lower stage order, the same branch) that produced a commit and handed
  it off already sits on verified work, so when it prints its completion marker
  and commits nothing it completes with no change needed, its activity log says
  so, and the next stage (the Judge) runs. The first producing Worker of a
  pipeline or of a rescue, and a plan block chained behind another block at the
  same stage order, keep the rule above. The operator Complete transition runs
  the same completion path, so the same rule applies there.
- **A Worker whose start ref already holds the work completes.** When the
  commit a Worker's dock started from holds commits that the vessel's landing
  target branch (its default branch) does not have, the branch already carries
  the deliverable. A Worker that prints its completion marker and commits
  nothing then completes with no change needed, its activity log names the
  start commit, the target branch and the commit count, and the next stage
  reviews the branch against the target. The landing merges the whole branch,
  so the start-ref commits land with it. A start ref at or behind the target,
  or a count git cannot read, keeps the rule above.
- **The Recorder never writes shared memory.** It writes native memory only, and
  hands anything that belongs in the shared external memory repository to the
  operator as a proposal in its summary.

Every other built-in persona template carries a Recall Existing Memory section
telling the agent to read the brief's Model Context section, when the brief
carries one, and search memory before it acts. Startup adds that section once to a built-in persona template that lacks
it and changes nothing else, so an operator edit is kept.

### 8.25 Branch Cleanup Sweep

The health loop runs the branch cleanup sweep every
`branchCleanupSweepIntervalCycles` cycles (default 200, about 100 minutes at a
30-second heartbeat). Every run writes one line to the admiral log
(`admiral.log` under `logDirectory`, dated by day), starting
`[BranchCleanupSweepService] sweep complete:`. The line gives vessels swept,
skipped and in error; branch candidates in the vessel bare and on origin;
landed, kept unlanded, kept for active missions, removed local and removed
origin; the same counts for preserved refs; for each anchor family (`dock
anchors`, `mission anchors`) candidates, kept for active missions, kept for
recover pointers, kept unlanded, kept in retention, removed local and removed
origin; origin refs already absent; failed operations; and the reason for each
skip.

Deleting a remote branch or ref that origin does not hold is not a failure.
Armada never pushes mission branches, so on most vessels the remote half of
cleanup finds nothing to delete. Every cleanup path (landing cleanup, the
merge-queue purge, terminal reaping, dock reclaim and this sweep) reads git's
"remote ref does not exist" outcome through one rule and treats the ref as
already deleted. The sweep deletes with a lease on the listed commit, and git
reports a ref removed after the listing the same way as a ref moved to another
commit ("stale info"), so when that delete fails the sweep lists origin again: a
ref origin no longer holds counts as already absent, and a ref that moved stays
a failed operation and is kept. Landing cleanup records no
`merge_queue.branch_cleanup_failed` event for it, and the sweep counts it as
`origin refs already absent`, not as a removal or a failure. An unreachable
origin, a rejected push or an authentication failure still records
`merge_queue.branch_cleanup_failed` (or a failed operation in the sweep) with
git's reason. A run that removed
nothing still writes the line, so a missing
line means the sweep did not run. A maintenance step failure on the loop logs
as `[ArmadaServer] <step> failed: <reason>`. Each removal also records a
`branch_cleanup.swept` event.

The sweep follows these rules:

- Candidates are `armada/` and `armada-landing/` branches,
  `refs/armada-preserved/` refs, and the reclaim anchors under
  `refs/armada/docks/` and `refs/armada/missions/`. `recover/` refs, human
  branches and other ref families are never touched.
- A ref is landed when its tip is an ancestor of the default branch in the
  vessel bare. A tip that the bare does not hold reads as unlanded and is kept.
- A branch that a non-terminal mission names is kept, even when it reads as
  landed.
- `LocalOnly` removes landed refs from the vessel bare. `LocalAndRemote` also
  lists origin through the vessel working checkout and removes landed refs
  there, with a lease on the tip it measured. `None` skips the vessel.
- A landed preserved ref is removed once its tip commit is older than
  `branchCleanupPreservedRefRetentionDays` (default 14). `0` keeps every
  preserved ref. An unlanded preserved ref is always kept.
- A missing default branch, an unreachable origin or a missing working checkout
  appears as an error or skip in the summary, never as a clean run.

#### Reclaim anchor refs

When a dock is reclaimed, `DockService` writes the dock `HEAD` to the vessel
bare repository under two names, so a produced commit can be found by dock or
mission id without knowing its SHA. It runs `git update-ref` in the dock
worktree, and a worktree shares its refs with the bare it belongs to, so the
refs land in the bare. Nothing in Armada pushes them to origin: no push names a
`refs/armada/` refspec, and the vessel bare is a plain `--bare` clone, so a push
from a dock sends only the branch it names. A dock branch's preserve ref,
`refs/armada-preserved/<branch>`, is written to the bare the same way.

- `refs/armada/docks/<dockId>` names every reclaimed dock, including a
  branchless Architect fan-out worker (`AnchorDockCommitAsync` in
  `src/Armada.Core/Services/DockService.cs`).
- `refs/armada/missions/<missionId>` names the mission that owned the dock, when
  it can be resolved (the same method).

The branch cleanup sweep lists origin as well as the bare. Under
`LocalAndRemote` it deletes an anchor that origin holds, with the same rules,
but origin holds one only when a push that Armada does not make put it there.

Both anchors name the dock's HEAD only. Uncommitted work -- tracked edits and
untracked files that are not ignored -- is saved before the checkout is removed
by every dock removal path: reclaim (and so unstick, stall recovery and mission
completion), delete, purge, a provision that finds a previous attempt's checkout
or a finished dock's leftover directory, and the disk lifecycle's orphan-dock
reclaim. The shared dock work snapshot commits it on HEAD in a private index and
writes `refs/heads/recover/<missionId>-<n>` in the vessel bare;
`mission.work_snapshot_saved` names the branch and the reclaim log line ends with
it. When the snapshot cannot be written, the checkout is kept: reclaim leaves the
dock active, delete returns false, purge and provision fail with the reason,
the disk lifecycle reports the directory as skipped, and
`mission.work_snapshot_failed` names the reason. A directory with no git checkout
in it holds nothing a snapshot can save and is removed as before. Both events
appear in the mission recovery report. Every completed reclaim also records
`dock.reclaimed`, naming the calling code path, the dock's mission and the
mission's status at that moment.

Both families use one retention rule. The sweep keeps an anchor in these cases:

- The dock or mission it names is live. A mission is live while its status is
  not terminal (`Complete`, `Failed`, `Cancelled`). A dock is live while its
  record is active or a non-terminal mission names it.
- A `recover/` branch in the vessel bare, or on origin when the sweep lists
  origin, points at the same commit.
- Its tip is not an ancestor of the default branch in the vessel bare. A tip the
  bare does not hold counts as unlanded.
- Its tip commit is younger than `branchCleanupPreservedRefRetentionDays`. The
  anchors share the preserved-ref window because the same reclaim step writes
  all three families to recover the same commit. `0` keeps every anchor.

Otherwise the sweep removes the anchor from the vessel bare and, under
`LocalAndRemote`, from origin with a lease on the measured tip. A mission that
stays in `WorkProduced` after its voyage ends is not terminal, so its anchor is
counted as kept for active missions until the mission status changes.

To confirm the sweep on a running admiral, read the next summary line. Then
compare `git for-each-ref refs/heads/armada-landing refs/heads/armada
refs/armada-preserved refs/armada/docks refs/armada/missions` in the vessel bare
and `git ls-remote origin` before and after that run. For the anchor families,
count per family on origin through the vessel working checkout:
`git ls-remote origin 'refs/armada/*' | cut -f2 | cut -d/ -f1-3 | sort | uniq -c`.

A WorkProduced mission under an ended voyage is terminal only after 8.26
reconciles it. Until then its branch counts as kept for active missions.

### 8.27 Typed-Decision Training Data

| Risk | Tools |
| --- | --- |
| Read | `armada_typed_decision_labels`, `armada_typed_decision_outcomes` |
| Write | `armada_typed_decision_reversal` |

All are operator tools; a mission captain reaches none.

`armada_typed_decision_reversal` records that a gated decision was wrong. Give
the decision event id, the answer that was correct, and why. It writes one
`typed_decision.reversed` event naming the original, and — when that decision
retains state — a labelled example in the host-local store. It reverses nothing
by itself: the work you already corrected stands, and no gate, threshold, or
record changes.

`armada_typed_decision_labels` reports what the host has retained per decision:
retained calls, recorded reversals, and whether the decision has reached the
minimum sample count. A decision below the minimum is named with its reason,
never left out, so "nothing to train on" is always visible rather than silent.

`armada_typed_decision_outcomes` joins recorded calls to the later outcome of the
record each one judged: a mission's landing result, linked incidents and rescues,
or an objective's final status, per decision and per confidence band. It needs a
global operator and returns counts only.

### 8.26 Terminal Voyage Mission Reconciliation

WorkProduced means that work exists and a later stage or landing will act on
it. That is a valid resting state only while the voyage is `Open` or
`InProgress`. The paths that end a voyage count WorkProduced as finished and
leave the mission in place: the voyage completion rule (8.4), the halt after a
failed stage, and every cancel surface. The completion rule keeps the voyage
open only for a WorkProduced mission that is held for operator review. Without reconciliation those
missions read as live forever to branch cleanup, capacity and recovery.

One rule gives each such mission a terminal status. It uses landing evidence,
never the voyage status alone, because a Failed voyage can hold landed work
and a Complete voyage can hold unlanded work:

| Evidence | Voyage | Mission becomes | Reason |
| --- | --- | --- | --- |
| Commit is an ancestor of the default branch in the vessel bare, or a merge entry is `Landed` | any ended | `Complete` | `terminal_voyage_work_landed` |
| Commit exists and is not on the default branch | `Failed` | `Failed` | `terminal_voyage_work_unlanded` |
| Same | `Cancelled` or `Complete` | `Cancelled` | `terminal_voyage_work_unlanded` |
| Commit absent from the vessel bare | as above | as above | `terminal_voyage_commit_absent` |
| No commit recorded, and the mission mode is `Audit` or `Research` | any ended | `Complete` | `terminal_voyage_report_only` |
| No commit recorded, any other mode | `Failed`; `Cancelled` or `Complete` | `Failed`; `Cancelled` | `terminal_voyage_no_commit` |

An `Audit` or `Research` mission delivers a report, so a recorded commit is not
expected. When it records none, it becomes `Complete` under any ended voyage,
also under landing mode `None`, and it carries no reconciled marker. When it
records a commit, the same landing-evidence rows apply as for any mission.

Unlanded work is never marked `Complete`. Under a `Complete` voyage it becomes
`Cancelled`, because another stage landed and this stage's commit was
superseded. A `Failed` or `Cancelled` mission carries the reason in
`FailureReason`. Every change records a `mission.terminal_voyage_reconciled`
event with the voyage status, landing probe, mission mode and commit. The mission keeps its
commit and branch. The pass never deletes branches, refs or commits, and it
runs no rescue or wake.

A reconciled `Failed` or `Cancelled` mission is a record of an ended voyage, not
a failure to recover. Autonomous recovery checks this before it writes
anything. It opens no incident, dispatches or defers no rescue, and records no
recovery attempt. This holds for the failed-mission sweep, for the rescue
re-check after a dispatch hold clears, and for mission outcome handling. The
incident lifecycle sweep closes an incident already linked to such a mission
as superseded, and the note names reconciliation as the cause. A genuine
failure under a `Failed` voyage still gets its normal incident and rescue.

The decision reads a durable marker on the mission row, not the failure reason
text. Reconciliation writes `reconciled_utc` (the time of the change) and
`reconciled_reason` (the unlanded reason code) together with the reason text,
which it still writes for people. Every reader calls the same rule, and the
sweep reads the marker from the mission summary. A later writer can replace
`FailureReason` without returning the mission to recovery. Restarting the
mission clears the marker, so a later failure of its own is recovered normally.

The migration that adds the marker also backfills it. It marks a `Failed` or
`Cancelled` mission when its failure reason ends, before any
`; previous reason:` suffix, with `(terminal_voyage_work_unlanded)`,
`(terminal_voyage_commit_absent)` or `(terminal_voyage_no_commit)`. The marker
time is the completion time, or the last update time when there is none. The
migration versions are SQLite 100, PostgreSQL 101, MySQL 92 and SQL Server 95.

The rule keeps a mission unchanged in these cases, and counts each by reason:

- `ancestry_unknown`: there is no vessel or repository, the default branch does
  not resolve, or the ancestry query failed. Only `git merge-base --is-ancestor`
  exiting 1 with no error text reads as unlanded; an exit 128, a missing object
  in the history, a read error, a timeout or a query that cannot start is
  unknown.
- `landing_in_flight`: a merge entry for the mission is not yet `Landed`,
  `Failed` or `Cancelled`.
- `voyage_terminal_grace`: the voyage ended less than ten minutes ago.
- `awaiting_manual_landing`: the voyage ended `Complete` with landing mode
  `None`. The work waits for a person to land it.

The health loop runs the rule every 10 cycles for voyages that ended in the
last 24 hours. It logs a
`[TerminalVoyageMissionReconciler] terminal voyage mission reconciliation`
summary line whenever it changes a mission or finds unknown ancestry.

To repair older rows, use `armada_reconcile_terminal_voyage_missions`:

1. Take a database backup in an approved window.
2. Run the tool with its defaults (`dryRun=true`, `includeHistorical=true`). It
   returns a background job; read the result with `armada_job_status`. Check
   `completed`, `failed`, `cancelled`, `kept` and `reasons`. Scope the run with
   `vesselId` or `voyageId` if you need to.
3. Run it again with `dryRun=false`. The counts must match the dry run, apart
   from missions that changed in between (`status_changed`).
4. Run the dry run once more. It must examine zero missions, apart from the
   kept reasons above.
5. Read the next branch cleanup summary. Its `kept for active missions` count
   drops by the branches the repaired missions held.

| Risk | Tools |
| --- | --- |
| Read | `armada_reconcile_terminal_voyage_missions` (`dryRun=true`, the default) |
| Write | `armada_reconcile_terminal_voyage_missions` (`dryRun=false`) |

### 8.28 Trace Records

A trace record is the compact description of one objective, voyage, mission,
check run, incident, merge entry or deployment. It outlives the raw rows, so an
operator can walk from any id to the work around it after data expiry. A record
holds:

- the id, the kind, and links to related records: voyage, parent and child
  (rescue) missions, the Challenger of a Judge, merge entries, check runs,
  deployments, incidents and objectives;
- the status and a timeline of lifecycle events (at most 60, newest kept);
- the vessel, captain, model, persona and pipeline stage;
- the branch, start ref, work commit, landed commit and deployed artifact;
- the latest definition-of-done gate result with up to 20 failing test names,
  and check totals;
- the Judge verdict and blocking-finding count of a Judge mission, and the
  failure reason;
- the last 4 KB of the final message;
- archive pointers (key, sha256, size, expired flag).

Free text is redacted and bounded. No diff, log or prompt is stored.

Records live in the `trace_records` table, their outgoing links in
`trace_links`, and the sweep positions in `trace_cursors`. The health loop
writes them:

- Every 10 cycles, the sweep reads the events after its cursor and records each
  record they name, plus the check runs, merge entries and deployments of each
  named mission or voyage. Its first run starts one day back.
- `TraceExpiryObserver`, a data expiry observer (`docs/ARCHIVE.md`), records
  each expiring voyage, mission, merge entry, check run and deployment row
  again before it is deleted, and records the record an expiring event names
  when it has no trace record yet. A failed write stops the expiry of that
  table.
- Every 100 cycles, records whose described record last changed more than
  `archive.traceRetentionDays` ago (default 365) are deleted with their links.

A write replaces the stored record of the same id and its links, so every path
is idempotent. A record whose source row is gone is never rewritten.

While the archive is enabled and configured and the recorder has a sink, a
record whose described record has ended, or that the expiry observer writes,
is also uploaded to `trace/<yyyy>/<MM>/<id>.json`. The verified upload is kept
on the record as an archive pointer, and unchanged content is not uploaded
again. When a record is in neither the table nor the database,
`armada_trace` reads its archived copy, returns it in `records` and lists its
key in `archivedKeys`; a requested full id found only in the archive returns
`source` `archive`. While the archive is off, nothing is uploaded or read, and
the recorder logs once that the archive is off. The server connects data expiry
and the recorder to the same archive, so data expiry writes the trace record of
each expiring voyage, mission, merge entry, check run and deployment row before
it deletes the row.

`armada_trace` takes a full id (`msn_`, `vyg_`, `obj_`, `chk_`, `inc_`, `mrg_`,
`dpl_`) or a short id: a prefix of a full id of at least four characters after
the kind prefix, with or without that prefix. It returns the requested record
first, then the records it links to and the records that link to it, breadth
first, up to `maxRecords` (default 60, maximum 200). A record missing from the
table is built from its live source row and stored. A short id that matches
more than one record returns the candidates instead.

`armada_trace_backfill` fills the table from stored history, one bounded batch
per call. It walks the objective, voyage, mission, merge entry, deployment and
incident tables in creation order, each from its own cursor, and records each
row with the check runs, merge entries and deployments of each mission and
voyage, so history with no surviving events is recorded too. Call it until
`done` is true.

Both tools require a global administrator.

| Risk | Tools |
| --- | --- |
| Read | `armada_trace` (writes a trace record only for a record missing from the table) |
| Write | `armada_trace_backfill` |

### Compact voyage status fields

`armada_voyage_status` with `summary:false` and `includeMissions:true` returns bounded mission details. Set `includeDescription:true` to include mission description previews. A nonempty `includeFields` list includes report previews only when it contains `output`; omitted or empty fields retain the default bounded report preview. Each preview remains limited to 4096 characters with `Truncated` and `FullLength` metadata. Use `armada_mission_output` for paged full reports.

### 8.29 Saved Ask Conversations

| Risk | Tools |
| --- | --- |
| Read | `armada_list_ask_threads`, `armada_get_ask_thread` |

Both tools read the conversations the authenticated human caller owns. No tool in
this group sends a message, approves or rejects a proposal, changes a conversation
or deletes one. See [Saved Ask Conversations](MCP_API.md#saved-ask-conversations).

`armada_cli_permission_prompt` is not listed here: it is not a general tool. Claude Code
calls it through `--permission-prompt-tool` during a saved Ask turn, and only that turn's
own scoped credential can see or call it. See
[Approving native tool calls](ASK_CONVERSATIONS.md#approving-native-tool-calls).
