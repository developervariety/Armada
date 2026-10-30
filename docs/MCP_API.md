---
topic: "MCP API"
summary: "Transport, discovery, authentication scope, and error behavior for the MCP endpoint."
read_when: "Connecting an MCP client, or debugging discovery, credentials, or a tool error."
applies_to: orchestrator
tier: leaf
---
# Armada MCP API

**Version:** 1.0.0
**Primary endpoint:** `http://localhost:7891/mcp`
**Compatibility endpoint:** `http://localhost:7891/rpc`
**Transport:** Stateless Streamable HTTP
**Server library:** Official MCP C# SDK
**Server name:** `Armada`

This document describes connection, discovery, request, and error behavior.
The server's live `tools/list` response is the source of truth for tool
descriptions and input schemas. The complete tool-family catalog and operator
workflow are in [armada-ops.md](armada-ops.md).

## Transport

Use `/mcp` for current clients. `/rpc` remains available for older JSON-RPC
clients. The server accepts normal JSON responses and request-scoped SSE
responses.

A `POST` must send `Accept: application/json, text/event-stream`, as the MCP
Streamable HTTP transport requires. A request that does not list both types
gets HTTP `406 Not Acceptable` and never reaches a tool. MCP client libraries
send this header; a hand-written `curl` call must add it. The credential check
runs first, so a request with no valid credential gets `401` whatever its
`Accept` header says.

The HTTP server is stateless. A client does not need to preserve an MCP session
ID between requests. A remote client can use an SSH stdio bridge that forwards
each request to the running Admiral's loopback endpoint.

Every request must carry a credential, the same as a REST request:
`Authorization: Bearer <credential token>`, `X-Token: <session token>` or
`X-Api-Key: <admiral API key>`. A request with no credential or an invalid
credential gets HTTP `401` and never reaches a tool. See
[Authentication And Scope](#authentication-and-scope).

For parameterless discovery, clients can send `tools/list` with an empty
`params` object or without a `params` member. Armada accepts both protocol
forms.

Because the transport is stateless, Armada cannot send a server-initiated
notification and never does.

Do not start `armada mcp stdio` inside a host that already runs the Admiral.
That command creates a second service graph. It does not control the running
Admiral.

To register Armada with Claude Code manually, add it as an HTTP MCP server:

```bash
claude mcp add --transport http --scope user armada http://localhost:7891/mcp
```

Drop `--scope user` to add it for the current project only. The endpoint
refuses a request without a credential, so add a header that reads your
credential from the environment, for example
`--header "X-Api-Key: ${ARMADA_API_KEY}"`. If you changed `McpPort`,
substitute your port. `armada mcp install` (or `scripts/*/install-mcp`)
registers the endpoint for Claude Code and the other supported runtimes. Each
HTTP entry carries an `X-Api-Key` header that references `ARMADA_API_KEY` in the
client's own syntax (`${ARMADA_API_KEY}` for Claude Code and Gemini CLI,
`${env:ARMADA_API_KEY}` for Cursor, `{env:ARMADA_API_KEY}` for OpenCode), so set
that variable in the client's environment. Codex uses the stdio bridge. The Mux
entry authenticates through Mux's `auth` object:
`"auth": { "type": "apikey", "apiKeyHeader": "X-Api-Key", "apiKeyValue": "${ARMADA_API_KEY}" }`.
Mux ignores an `auth` field it does not know and then connects with no
credential, which the endpoint refuses; Mux reports no error for it.

The Gemini CLI entry is registered with
`gemini mcp add --scope user --transport http --header 'X-Api-Key: ${ARMADA_API_KEY}' armada http://localhost:7891/mcp`.
That header form is unverified against an installed Gemini CLI. On a host with
Gemini CLI, run that command, start `gemini` with `ARMADA_API_KEY` set, run
`/mcp`, and confirm the `armada` server lists its tools. A `401` means the
header value was not expanded.

**Enterprise-managed Claude Code.** If the add is rejected with
`Cannot add MCP server 'armada': not allowed by enterprise policy`, your
organization's Claude Code managed settings restrict which MCP servers may be
added (`allowedMcpServers`). This cannot be overridden by a user, a project
`.mcp.json`, or `--mcp-config`. Ask a Claude Code administrator to allow the
Armada endpoint:

```json
{ "allowedMcpServers": [ { "serverUrl": "http://localhost:7891/mcp" } ] }
```

Run `/status` in Claude Code to see the active setting sources. If Claude Code
stays locked down, the same HTTP endpoint works from any other MCP client that
is not under that policy.

## Initialization

Example request:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-03-26",
    "capabilities": {},
    "clientInfo": {
      "name": "example-client",
      "version": "1.0.0"
    }
  }
}
```

The response includes the negotiated protocol version, server capabilities,
and server information.

## Tool Discovery

Call `tools/list` and continue while the response contains `nextCursor`.

First request:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/list",
  "params": {}
}
```

Continuation request:

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/list",
  "params": {
    "cursor": "500"
  }
}
```

Catalog size depends on the running build and caller role. Follow every
`nextCursor`; do not infer completeness from a fixed tool count.

Standalone stdio lists server-dependent tools with the same names and schemas
as HTTP. These include the scheduler, planning and
refinement sessions, context retrieval, typed-decision evaluation, Harbor jobs
and server shutdown. If the required service is absent, the handler returns
`ErrorCode: "Unavailable"` and an `Error` message that starts with `Unavailable:`.
Stdio reports this as JSON-RPC error `-32603` with the named message. HTTP
retains the handler error fields in its tool result. Authorization checks
still apply before a tool can use a service.

Stdio runs as an explicit local operator. Its record-backed tools act on the
configured database and local storage. Listing a process-dependent tool does
not create a server service or grant control of a running admiral.

Each returned tool contains:

- `name`;
- `description`;
- `inputSchema`.

Clients must not use a stored schema as the primary source when a live
connection is available. Tool fields and enum choices can change before the
stable release.

The result can include public cache metadata. A client can cache it for the
advertised TTL, but it must refresh after an Admiral upgrade.

## Tool Calls

Example:

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "tools/call",
  "params": {
    "name": "armada_status",
    "arguments": {}
  }
}
```

The result uses MCP content blocks. Armada normally returns one JSON text
block. Parse the JSON text before you inspect the result fields.

Do not infer success from the absence of a JSON-RPC error. Many Armada tools
return a structured application result with `success`, `status`, `code`,
`error`, or `action` fields. Read those fields.

The objective, incident, runbook and prompt-template read tools and
`armada_campaign_status` preview long free text. A string longer than 250 characters keeps its first
250 characters and ends with ` ... [preview; call again with
includeFullContent=true for the full text]`; it gains a companion
`<name>Length` with the original length, and the result carries
`TruncatedFieldCount`. A long list of short strings keeps 5 items and gains
`<name>Count`. Pass `includeFullContent=true` to get every field whole. The
captain brief's objective section names the flag beside `get_objective`.

## AgentWake

A session learns of work by calling a tool. Operator sessions coordinate with
each other outside Armada, in the operators' own session threads.

A mission outcome that needs an operator writes an unaddressed Wake signal
(prefixed `[vsl=...]` or `[CRITICAL]`). Read it with `armada_enumerate` and
`entityType=signals`. Process the work first, then acknowledge each signal with
`armada_mark_signal_read`; any authenticated caller may acknowledge a Wake.

The `inbox` tool (and the REST inbox built from the same service) lists the
calling operator's own pending, unexpired saved Ask action proposals as kind
`ask_proposal`, with `href` `/ask?thread=<threadId>`. The item is a read-only
pointer to the conversation: it carries no approval fingerprint, and a decision
is possible only through the conversation's exact approval fence. A caller that
is not an interactive global administrator, a captain's mission credential, and
a saved-chat credential receive no `ask_proposal` items, and no caller sees
another user's or another tenant's proposals.

The `inbox` tool (and the REST inbox built from the same service) lists a Judge
PASS held for operator review as kind `judge_pass_held`. Resolve it with
`armada_review_hold`:

| Argument | Required | Meaning |
| --- | --- | --- |
| `action` | yes | `clear` releases the hold and the PASS proceeds through the normal handoff or landing path; `fail` fails the mission and cancels its dependent stages |
| `missionId` | yes | Held mission (`msn_` prefix) |
| `reason` | yes | Recorded on the event |
| `operator` | yes | Recorded on the event |

The tool refuses any caller other than a global administrator
(`global_administrator_required`), a missing argument (`missing_reason`,
`missing_operator`, `missing_mission_id`, `invalid_action`), and a mission that
is not held (`not_held`). Every argument problem is reported in one refusal:
`Error` names each one, `Problems` lists each code, and `Reason` is that code
for one problem or `invalid_arguments` for several. `clear` is also refused with `gate_rerun_running`
while the mission's definition-of-done gate is re-running on its dock after an
interrupted run: the re-run records a result and then releases the hold (pass)
or fails the mission (failure) by itself, and `fail` stays available. `clear`
writes `mission.hold_cleared` and `fail` writes `mission.hold_failed`, each
naming the operator and the reason. A cleared hold that has dependent stages
hands off to them and releases the mission's dock, so the next stage can check
out the branch. The model never resolves a hold; the only automatic release is
the gate re-run. A `handoff_outcome` hold is refused with `handoff_outcome_hold`;
resolve it with `armada_handoff_hold`.

The inbox lists a stage handoff held by the `handoff_outcome` hold as kind
`handoff_outcome_hold`; its detail lists the unmet acceptance criteria. Resolve
it with `armada_handoff_hold`:

| Argument | Required | Meaning |
| --- | --- | --- |
| `action` | yes | `release` hands the stage off unchanged; `mail_release` prepends the unmet-criteria note to the next stage's brief and hands off; `rescue` fails the held stage and cancels its dependent stages, so autonomous recovery decides a rescue |
| `missionId` | yes | Held mission (`msn_` prefix) |
| `reason` | yes | Recorded on the label |
| `operator` | yes | Recorded on the label |

The tool refuses any caller other than a global administrator
(`global_administrator_required`), a missing or unknown argument
(`missing_reason`, `missing_operator`, `missing_mission_id`, `invalid_action`;
several at once are one `invalid_arguments` refusal that names each in `Error`
and lists each code in `Problems`), a mission without this hold (`not_held`), and a hold another choice or the
timeout release is already ending (`handoff_hold_busy`). Each choice writes one
`typed_decision.labelled` event with `label` `release`, `mail_release` or
`rescue`, the operator and reason, the decision event id, and its
`state_sha256`. A hold nobody answers is released with the note after
`typedDecisions.handoffOutcomeHold.timeoutMinutes` and labelled `hold_timeout`.
The model never resolves this hold.

When the `inbox_triage` typed decision is enabled (`Gate`), the `inbox`
tool's items each carry an extra
`attention` field (`informational`, `today`, `this_hour`, or
`blocking_live_voyage`) and are ordered by it.
Triage only annotates and re-orders — it never hides or drops an item. With the
decision `Off` (it ships `Gate`) `attention` is absent and the
deterministic severity order stands.

`armada_register_agentwake_session` registers one in-memory process target with a
concrete `runtime` (`Claude`, `Codex`, or `OpenCode`) and an optional session ID,
command, working directory, and client name. The registration lasts until
restart. The settings file controls delivery:
`SpawnProcess`, `StoredWake`, or `Both`. `StoredWake` stores the wake row and
sends no MCP notification, because this transport cannot carry one. Any other
delivery-mode value is rejected when settings load.

A mission-outcome Wake signal is always retained. When delivery is
`SpawnProcess` or `Both`, Armada also starts the effective runtime.
`armada_agentwake_status` shows both the configured and transient state.
OpenCode always starts a fresh session; it does not use resume state. Make the
bootstrap prompt reconstruct context from the Armada status tools and durable
memory.

## Long Operations

Dispatch, code-index refresh, merge processing and landing retries can return an
accepted job instead of blocking the request. Save the job ID and call
`armada_job_status` until the job reaches a terminal state: `Succeeded`, `Failed`
or `Lost`.

`armada_retry_landing` checks that the mission exists and is `WorkProduced` or
`LandingFailed`, then returns the job at once; the landing runs in the job. One
retry per mission runs at a time. A retry requested while a retry job for the
same mission is unfinished returns that job with `Joined: true` and starts
nothing, so a caller that lost the reply never lands the work twice. A retry
requested while a REST retry for the mission runs is refused with
`retry_landing_in_flight`. The job's `SubjectId` names the mission. A job whose
landing does not end `Complete` reads `Failed`, and its `FailureMessage` gives
the mission status and failure reason.

`armada_dispatch` validates the request, checks the dispatch hold, runs the
objective preflight and checks fleet and sibling-lane capacity before it accepts
anything. A refusal at that point returns its named code and creates nothing; a
full fleet or lane returns `fleet_capacity_reached` or
`sibling_lane_capacity_reached` with `ActiveCount`, `Limit`, `CandidateVesselId`
and `LaneMembers`. Only then is the job accepted, and it is
written to the job journal in the data directory before the `Accepted` response
is returned. The voyage and mission rows are created when the job runs, not
before the response: an `Accepted` job is not yet a voyage. Read the voyage ID
from the job's result. A dispatch refused after acceptance (the hold engaged in
between, another dispatch won the objective, or another dispatch took the last
capacity slot) ends `Failed` with the refusal
body as its `FailureMessage`. `armada_job_status` reports a `Failed` or `Lost`
job's reason in `FailureMessage`, and reading it is a successful call. A
successful job is not evidence that a captain has started work.

While the dispatch hold is engaged, every dispatch entry point refuses at
submission with `dispatch_hold_active`, the holder (`SetBy`), `SetByUtc` and
the reason, and accepts no job: `armada_dispatch` (plain and alias-ordered),
REST `POST /api/v1/voyages`, WebSocket `create_voyage`, planning-session
dispatch, and mission, architect and remote-control dispatch. The objective
scheduler records `dispatch_hold` and autonomous rescue defers. The hold is
stored in the database and survives an Admiral restart. Automatic
Checks for eligible work on active voyages may run so those voyages can drain;
Checks for idle vessels, completed voyages and unrelated work wait until the
hold clears.

A job survives the process that accepted it. When the admiral restarts before a
job finishes, the next start records it as `Lost` with the reason
`job_lost_on_restart`, and `armada_job_status` returns that record, never
`job_not_found`. A lost job is never resumed: check whether its effect exists,
then submit it again. Every job that ends `Failed` or `Lost` writes a
`job.failed` or `job.lost` event on its objective (on the job when it names no
objective). The event names the job, its reason and its objective. Finished records are kept for 14 days. `armada_dispatch_hold`
`status` and `engage` list `UnfinishedJobs`, the jobs a restart would lose,
`RunningGates`, the definition-of-done gates running or waiting for the host
command slot, each with a `SlotWait` that reads `Running` while the gate holds a
slot, `WaitingForSlot` with its `Position` in line while it waits, or `Queued`
before its first command, so a queued gate is told from a lost one without
reading its dock, `GateReruns`, the interrupted gates being re-run, and `HostSlot`,
who holds and who waits for the host command slot, with `MemoryWaitReason`
when free memory holds the next request. A restart kills every gate
and check listed there; wait for `UnfinishedJobs`, `RunningGates` and
`GateReruns` to be empty and `HostSlot` to have no holder or waiter before
restarting the admiral.

A global administrator can also list every job, newest first, through REST
`GET /api/v1/jobs` and the dashboard Jobs page, and read one through
`GET /api/v1/jobs/{id}`. Jobs have no cancel operation.

A job that stays `Accepted` or `Running` for 30 minutes with no sign its work is
still advancing is reaped as `Failed` with the reason `reaped as stale`, and its
operation is cancelled. Past the limit is not the same as hung: a landing-retry
job whose integration candidate is still running a Build or UnitTest check is
kept, not reaped, because those checks serialize behind one host lock and a
healthy landing can outlast the flat limit; once no such check is advancing, the
job is reaped like any other. A disk-lifecycle job is kept while its pass is
running and has handled an item, written a bundle entry or finished an upload in
the last ten minutes, so a long archive pass is not cancelled mid-upload. A terminal status is final: an operation that
finishes after its job was reaped does not change the status in memory or in the
journal.

For objective work, call `preview_objective_dispatch` before `armada_dispatch`.
It performs the same read-only preflight that operator and autonomous objective
dispatch enforce. It reports all structural blockers, a complete typed
dependency graph, and bounded diagnostic chains. A busy compatible captain remains valid configured coverage; its idle
count is capacity information only. With `compact: true` it returns only
`objectiveId`, `isReady`, `errors` (the Error issues), `failedFacts` (the
preflight facts that fail), `warningCount` (the number of Warning issues) and
`warningCodes` (each Warning code once, in preview order), for an audit that
previews many objectives. A ready preview can still warn, for example
`gate_inputs_unplanned` or `stage_skip_unconfirmed`, so read `warningCodes`
before reading `isReady: true` as clean; the
REST route takes the same `compact=true` query parameter. The default returns
the full preview.

An objective must also pass the dispatch preflight. The preflight is an answer
for each numbered question in the operator dispatch-preflight battery, recorded
in the objective's `preparation.preflight.questions` array through
`update_objective`. The battery has fourteen questions. Questions 1-13 keep
their numbers; question 14 is last (the target tip is green, or the brief names
the inherited failures) and is a recorded operator answer, not a live suite run
at preview time. This is deliberate: a preview must remain read-only and fast,
and the operator can admit a target with explicitly named inherited failures.
A new suite run would neither replace that judgment nor prove which failures
the mission owns. The answer should cite the checked target commit and the
baseline result or named failures. Each entry carries a question `number`, an `answer` of
`Yes`, `No`, or `Unanswered`, an operator `note`, an `answeredUtc`, and an
`answeredBy`. Dispatch is refused while any question is unanswered, a question
that must be yes is answered no (including 14), or the open-owner-question
question (13) is answered yes; the preview reports this as a blocking
`objective_preflight_incomplete` finding listing the offending question
numbers, and reports the deterministic questions as facts to check the recorded
answers against the repository. Question 2 matches deliverable phrasing
(`deliver a report`, `report only, no code`), not a bare `report-only` token
that names the code under change. A high Q1 model reading is not raised when
the Q1 citation fact passes and the model names no disputed claim. An `armada_dispatch` call may set
`forcePreflight: true` to override an incomplete preflight or a
`objective_preflight_model_flag` finding, including question 14. It overrides
only those preflight-class findings; any other blocking issue still refuses the
dispatch, and the override is recorded as an `objective.preflight_overridden`
event naming the operator, the blocking question numbers, and the
model-flagged question numbers. A refusal without the force flag lists both
`IncompleteQuestions` and `ModelFlaggedQuestions`.

`armada_dispatch` accepts `skipStages`, an array of pipeline stages the operator
confirms the voyage does not need (for example `["TestEngineer"]`), and an
optional `skipStagesReason`. Each entry names one stage: a persona name when only
one stage carries that persona, a stage order (`"5"` or `"#5"`), or a persona and
its occurrence in stage order (`"Worker#2"` is the second Worker, so a pipeline
with a first Worker and a revising Worker can drop either one). The `PipelineStageSkip`
parser reads every entry, so every dispatch surface below accepts the same
forms. The named stages are dropped when the voyage is
materialised and the remaining stages chain across the gap: each kept stage
depends on the last kept stage before it. One `voyage.stage_skipped` event is
recorded per dropped stage; its payload carries `Persona`, `StageOrder`,
`Reason` and `Confirmer` (the calling principal). The same rule applies to REST
`POST /api/v1/voyages` (`SkipStages`), WebSocket `create_voyage` (`skipStages`), alias dispatch,
and the autonomous scheduler. The dispatch is refused, and nothing is created,
when an entry names the Judge by persona or by order (`stage_skip_judge_refused`),
is blank or malformed or names no stage of the effective pipeline
(`stage_skip_unknown_persona`), is a bare persona name that more than one stage
carries or an order that parallel stages share (`stage_skip_ambiguous_persona`;
the message lists the order and occurrence name of each such stage), or when
only the Judge would remain (`stage_skip_leaves_no_work`). A `stage_optional` preview Warning
is advice only: nothing is skipped unless the operator names it. An objective's
operator-confirmed `preparation.stageSkip` (`stages`, `reason`, `confirmedBy`,
`confirmedUtc`, and the server-issued `operatorConfirmationId`) is that
objective's default skip. The autonomous scheduler applies it, and so does an
operator dispatch linked to the objective that sends no `skipStages`. A dispatch
that sends `skipStages` uses only its own list, and an empty list (`[]`) skips
nothing; the stored default then neither applies nor gates the dispatch. A
default entry that names no stage of the chosen pipeline (for example a stored
`Recorder` on a Worker and Judge pipeline) is dropped instead of refusing the
dispatch: the preview reports it as a `stage_skip_default_dropped` Warning and
lists it in `droppedStageSkips`, and the voyage records one
`voyage.stage_skip_default_dropped` event per dropped entry. Every other refusal
holds for a default entry, with a message that names the stored skip: a bare
persona name that two stages carry, the Judge, or a skip that leaves only the
Judge refuses the dispatch until the stored skip is corrected or the dispatch
names its own `skipStages`. A stored skip is honored only when
all server confirmation fields are present; older records without the proof are
kept but treated as unconfirmed until an operator confirms them. On objective
create or update, send top-level `confirmStageSkip: true` with the skip in
`preparation.stageSkip`. On update, omit `preparation` to confirm the existing
skip again without changing its stage list. Armada ignores inbound confirmer,
timestamp, and proof values and records the authenticated operator and server
time. Every write with the flag issues a new proof and a new time, even when
the skip is unchanged, so a batch write that sends the flag re-stamps each
skip it touches. Without the flag, an unchanged full-preparation replacement
preserves the stored proof, while a changed stage list or reason leaves the
skip unconfirmed. The write that drops a confirmation says so: the returned
`stageSkip.confirmationNotice` names what changed and how to confirm again,
and `stageSkip.droppedConfirmation` keeps the server's copy of the last
confirmed stages, reason and proof. A later write that returns the skip to
exactly those stages and that reason restores that confirmation, with its
original confirmer, time and proof. Both fields are server-owned; inbound
values are ignored. A refinement summary never confirms or writes this field.
An autonomous rescue carries the skips recorded on the voyage it rescues: it
reads that voyage's `voyage.stage_skipped` events, drops the same stages from
the rescue chain through the same rule, and records one `voyage.stage_skipped`
event per carried stage on the rescue voyage, with the original `Reason` plus
`carried from <voyage id>` and the original `Confirmer`. A skip is not carried
when the failure reason or review comment names that stage's persona, or when
its persona names more than one stage of the rescue chain (a skip that named one
of two same-persona stages by order or occurrence is carried by its persona, so
such a rescue runs both stages), and the Judge is
never skipped. A voyage with no recorded skip gets the full chain.
`preview_objective_dispatch` lists `effectivePipelineStages` after that same
`PipelineStageSkip` rule, names `skippedPipelineStages`, reports whether the
stored skip is confirmed, lists the stored entries the pipeline does not carry
in `droppedStageSkips` (each with a `stage_skip_default_dropped` Warning), and
surfaces the named refusal a stored skip would hit
(`stage_skip_unconfirmed`, `stage_skip_judge_refused`,
`stage_skip_unknown_persona` for a malformed entry, `stage_skip_ambiguous_persona`,
`stage_skip_leaves_no_work`) without creating a
voyage. Captain coverage in the preview follows the effective stages, so preview
and dispatch agree.

`preview_objective_dispatch` warns, without blocking dispatch, about stored text
that contradicts the record. `objective_text_conflicting_ids` quotes each
sentence that calls an objective other than the record's own id or its
`ParentObjectiveId` current, parent, authorizing, owning or controlling; a short
`obj_` plus eight characters counts as the same objective.
`objective_text_conflicting_criterion_counts` quotes the sentences that state
criterion counts when they disagree with each other or with the number of
acceptance criteria. The description, criteria, non-goals, rollout constraints
and refinement summary are read; each warning quotes at most five sentences.
Rewording a superseded sentence stays an operator edit.

`preview_objective_dispatch` also warns, without blocking dispatch, when a stage
that will run has a duty that conflicts with the objective, and each
`stage_duty_conflict` warning names the rule that wins. A read-only dispatch
(Audit or Research) whose acceptance criteria require a build or test run
quotes those criteria, and the criterion wins: the brief lets each stage run the
existing build and test commands the criterion needs, while read-only still
forbids edits, commits and pushes. A TestEngineer stage on an objective whose
non-goals forbid test edits is decided by the non-goal, and the brief tells
that stage it is validate-only. A criterion requires a run when it asks to run
or execute tests, a suite, a gate or a build, names class or suite runs, pins a
pass count (`42/42 pass`, `0 failures`), says a build or suite passes, or names
a build or test command; a criterion that says not to run them does not count.

When the target vessel's workflow profile declares `gateInputPaths`, the preview
also returns a `gate_inputs_unplanned` warning, which does not block dispatch.
With an Architect stage the plan does not exist yet, so the warning lists every
declared gate input the Architect brief tells it to assign. With operator
`missionDescriptions` it lists each gate input that no mission title or
description names, either as the exact path or through the declared glob. A
gate input the objective's rollout constraints or non-goals keep out of the
change is not listed: a sentence that forbids changes ("No src/ or docs/ file
changes") excludes each input it names or whose directory it names, and a
sentence that allows only the files it names excludes each input it does not
name. The warning names the excluded inputs, and with nothing left to list it is
not raised. A pipeline with neither raises no gate-input warning.

For an explicit dependency, set `preparation.gateInputSources` on the objective:

```json
[{"inputPath":"docs/data-dictionary.md","sourcePaths":["src/Export/**/*.cs"]}]
```

`inputPath` must exactly name a workflow gate-input entry. `sourcePaths` are
operator-declared repository-relative paths or globs, not inferred dependencies.
The preview names matching supplied path mentions and their declared affected
input, or the declared source paths when no mention matches. Path mentions may
be references, exclusions, directories, or incomplete plans; absence of a match
never suppresses a warning. Only the existing objective scope exclusions can
exclude an input. A missing or broad plan keeps the planning reminder. Unmapped inputs keep their existing
warning, with the missing relation stated. Existing exclusions still win.
Mappings persist in preparation; a complete preparation replacement must include
them. Paths cannot be rooted, contain `..`, a colon, or control characters.
There may be at most 50 inputs and 50 source paths in total. Invalid mappings
are refused; they cannot suppress a warning. These mappings affect preview
explanations only; they do not change required checks or their pass conditions.

`armada_dispatch` accepts `captainAssignments`, an array of `persona`,
`captainId` and optional `fallbackTier` entries (`persona` `*` applies to every
stage without its own entry). The voyage row stores them when it is created,
before any mission exists, so every mission of the named persona resolves the
captain as its requested captain before its first assignment: the root stage,
later stages, and fan-out missions alike. A single-stage dispatch (the
`WorkerOnly` pipeline, or no pipeline) stamps its stage persona on each mission,
so an assignment for `Worker` applies to it too. The same rule applies to REST
`POST /api/v1/voyages`, WebSocket `create_voyage` and alias dispatch. Dispatch
refuses an assignment whose captain can never take the stage it names, before
any voyage or mission exists, with the captain, the persona and one code:
`captain_persona_not_allowed` (its `AllowedPersonas` excludes the persona),
`captain_runtime_cannot_serve_persona` (its runtime cannot run the persona),
`captain_below_persona_minimum_tier`, `captain_not_found` (absent or outside the
caller's scope), `captain_in_another_tenant`, or `captain_unavailable` (benched,
quarantined, stalled or stopping). A named persona is checked against itself; a
`*` entry against every stage of the effective pipeline without its own entry.
`armada_dispatch` refuses before it accepts a background job.
`preview_objective_dispatch` reports the same assignment as the blocking issue
`assigned_captain_ineligible` with the same reason and code. A captain that
changes after dispatch is handled at assignment
([USAGE_ROUTING.md](USAGE_ROUTING.md#requested-captain-and-fallback-tier)).

After the deterministic preflight the preview also consults the `preflight`
typed decision when it is enabled (`typedDecisions`, ships `Gate`). It reads the
title, description, acceptance criteria, non-goals, refinement summary, Kind,
vessel name, pipeline stages and the deterministic facts, and asks the text-half
battery questions the code cannot settle (Q1 premise-versus-facts, Q4–Q9, Q12,
and a Q13 owner-question choice). A question the model answers at or above the
threshold adds a blocking `objective_preflight_model_flag` finding to the
preview, which the autonomous scheduler skips dispatch on exactly as it does for
any other Error finding (an operator dispatch may pass it with `forcePreflight`); The model only adds findings — it never dispatches, lands, or removes a
deterministic finding — and when the decision is `Off`, unavailable, or below the
threshold the preview is exactly the deterministic result.

### Dispatch starting commits


`armada_dispatch` accepts `missions[].startFromRef` (branch, tag, or commit).
An omitted value inherits the linked objective's `StartFromRef`; an explicit
mission value takes precedence. This also applies to mission-alias dispatch.
The root mission pins the resolved commit; dependent stages continue their
predecessor's branch, except that workers fanned out from an Architect plan
start from the Architect's start commit when it was not the target branch. The response preserves the voyage fields and adds
`MissionStartRefs`, with `MissionId` and `StartFromRef` for each mission.
`mission.start_ref_resolved` records each root's verified commit. A missing
reference fails with `start_from_ref_missing`; it never falls back to main.

## Errors

Armada uses two error levels:

1. A JSON-RPC or MCP protocol error for an invalid request, unknown tool,
   invalid cursor, or unhandled call failure.
2. A structured Armada error result for a valid tool call that cannot perform
   the requested operation.

A tool result is an error when it is an explicit protocol result with
`isError: true`, or when its top-level `Error` field is a non-empty string. An
explicit `isError` flag decides in both directions. A null or empty `Error`
field, or an `Error` nested inside the payload, is a successful result. Over
HTTP, an error result reaches the client with `isError: true` and its full
payload, and the tool-call audit records it as `Failed` with the error message.
Local stdio (`armada mcp stdio`) applies the same rule and answers an error
result with a JSON-RPC internal error that carries the message. A
collection-limit refusal is the exception: both transports return it as the
tool result, with `errorCode` `CollectionLimitExceeded`, and stdio does not
collapse that result into a protocol error. See [Request limits](REQUEST_LIMITS.md).

When a structured result includes an action hint, follow it before you retry.
Do not repeat a dispatch call until you have checked whether it created a
voyage.

`armada_transition_mission_status` runs as the authenticated caller. A mission
the caller may not read returns `Mission not found` and is not changed. The
change it applies is broadcast with the mission owner's delivery scope.

`armada_transition_mission_status` uses the same operator transition path as
the REST status route and the WebSocket `transition_mission_status` command. A
manual `Complete` that fails a completion gate returns a structured error result
and leaves the mission unchanged:

```json
{
  "Error": "Manual completion blocked: manual_completion_ancestry_unavailable",
  "Reason": "manual_completion_ancestry_unavailable"
}
```

A transition into `WorkProduced`, or out of `Review` to `InProgress` or `Failed`, returns the
same structured error with the reason `manual_transition_work_produced_pipeline_owned` or
`manual_transition_review_decision_required`. Completion handling and retry-landing produce
`WorkProduced`, and a review approval or denial moves a `Review` mission.

The gates are review approval, Judge authority, captain process release, Check
state, and target ancestry of the mission commit when no active dock will land
it. The same request on another surface gets the same decision.

A `LandingFailed` mission moves to `Complete` only by confirming a landing
proposal; see [Landing proposals](MERGING.md#landing-proposals).

Common protocol errors:

| Condition | Result |
| --- | --- |
| Unknown tool name | Invalid parameters |
| Cursor outside the catalog | Invalid parameters |
| Missing required JSON-RPC fields | Invalid request |
| Malformed tool arguments | Invalid parameters or structured validation error |

## Authentication And Scope

Every MCP request authenticates through the same service as the REST API. There
is no anonymous or default identity. A missing credential, an invalid
credential or a server without an authenticator gets HTTP `401`, and no tool
handler runs. Each tool reads the authenticated caller of its own request.
Records it creates carry that caller's tenant and user, as the matching REST
create does. That covers `armada_create_fleet`, `armada_add_vessel`,
`armada_create_captain`, `armada_create_mission`, `armada_send_signal`,
`armada_nudge_voyage`, `armada_enqueue_merge`, `create_persona`,
`create_pipeline`, `create_playbook`, `create_prompt_template`,
`create_workflow_profile` and the
service-backed creates (objectives, backlog items, incidents, releases,
deployments, environments, runbooks and memories). `create_playbook` checks
file-name uniqueness inside the caller's tenant. `create_workflow_profile`
keeps a tenant named in the record only for a global administrator. A progress
signal a tool writes about an existing mission, such as the one from
`armada_restart_mission`, belongs to that mission's owner.

An id a tool argument names is read with the caller's scope, like an id in a
REST body. `armada_update_mission` reads the mission and a changed
`dependsOnMissionId` or `parentMissionId` in the caller's scope and answers
`Mission not found` or `... not found` for a record outside it, and
`armada_add_vessel` refuses a `fleetId` outside it with `Fleet not found`.

Dispatch creates follow the record they act on, the same way on REST and MCP.
`armada_dispatch`, `armada_decompose_plan` and `dispatch_backlog_planning_session`
create voyages and missions owned by the target vessel's owner. `run_check` and
`retry_check_run` create a check run in the vessel's tenant for the calling
user. A linked mission, voyage or deployment must be in the caller's tenant.
`commandOverride` runs a raw shell command as the server process, so only a
global administrator may send it, and only a global administrator may retry an
imported check; other callers retry with the workflow-profile command.
`Deploy` and `Rollback` checks run only through the deployment workflow. `start_runbook_execution` creates an execution in the runbook's tenant for
the calling user.

`run_check` and `retry_check_run` wait at most two minutes for a result. A
check still waiting for the host-wide slot by then is returned unfinished, with
`slotWait` naming its queue position and what holds the slot, and it finishes
its own record; read the result with `get_check_run`. Neither call blocks until
the client's tool timeout. Both are operator runs: the check runs to completion
even when its voyage has ended. Only the automatic Check runner discards a
Pending check of a cancelled or failed voyage, and the discard writes its
reason (`voyage_cancelled` or `voyage_failed`) into the check's `summary` and
`output` and records a `check.discarded` event. A check that passed because a
definition-of-done gate already ran the same command on the same commit carries
`satisfiedBy` (gate mission, command label, commit, pass time) and a null
`durationMs`; it ran no command of its own.

What a caller may use:

| Caller | Tools listed and callable |
| --- | --- |
| Global administrator (admiral API key, or a global-admin user credential) | The whole catalog |
| Tenant administrator | The caller-scoped tools below, plus `create_persona`, `update_persona`, `delete_persona`, `create_pipeline`, `update_pipeline` and `delete_pipeline`, as on REST. Each change finds the record through the caller scope and applies `OwnershipPolicy.CanEdit`, so it changes only the caller's own tenant's records; another tenant's record reads as not found |
| Mission captain (a mission session token), whatever its owner's role | The caller-scoped tools below, plus the read-only voyage tools `armada_enumerate`, `armada_mission_status`, `armada_get_mission_log`, `armada_voyage_status`, `armada_mission_output` and `get_objective`. An administrator owner's mission still reaches no operator tool, and its model is sent only these definitions with each request |
| Any other authenticated user | Only caller-scoped tools: `get_persona`, `get_pipeline`, `get_prompt_template`, `list_prompt_templates`, `create_memory`, `get_memory`, `search_memory`, `update_memory`, `delete_memory`, `armada_typed_decision`, `armada_score_items`, `armada_check_premise`, `armada_check_prior_art`, `armada_memory_triage`, `armada_change_quality`, `armada_corpus_prelabel`, `armada_run_custom_decision`, `armada_search_context`, `armada_mission_change_summary`, `armada_mission_attempts`, `armada_suite_lease`, and while Harbor is enabled `armada_harbor_jobs`, `armada_harbor_job`, `armada_harbor_job_stop` |

The Harbor job tools apply the runner authority rule that Harbor enrollment
uses: a caller sees a job when it is the runner owner or has authority over the
owner, and a job it may not see reads as unknown (`harbor_job_unknown`).
`armada_harbor_job_stop` also needs a tenant or global administrator, the level
the REST stop route requires, and otherwise returns reason
`tenant_administrator_required`.

The rest of the catalog is operator control with no tenant or user scope, so a
narrower role neither discovers nor calls it. An operator repair that reads or
rewrites records in every tenant also checks its caller itself:
`armada_reconcile_terminal_voyage_missions` refuses any caller other than a
global administrator with reason `global_administrator_required`, for a dry run
as well as an apply, before it starts a job or reads a mission. A refused call returns a JSON-RPC
error before its arguments are read or its audit is written.

**Captains.** A launched captain authenticates to Armada MCP with a
caller-scoped session token, never a global-admin credential. A mission captain
carries the mission owner's own session token, which the endpoint scopes to that
owner's tenant and user; a chat captain carries the authenticated caller's own
session token. The owner of a mission is its tenant and user. A part the mission
does not carry comes from its voyage, which holds the objective owner of an
autonomous mission. Dispatch copies the vessel's owner, and a tenant-owned vessel
has no user. A record without a user belongs to the default user and a record
without a tenant to the default tenant, so a mission on such a vessel runs as the
default user. A mission token keeps the captain tool limit even when its owner
is an administrator. The owner must be an active user
of the mission's tenant. Otherwise the launch presents no credential, the
endpoint refuses it (fail closed), and the admiral log names the reason.

API runtimes select remote tools from that caller-filtered catalog. Built-in
workspace tools keep their names when a remote tool has the same name. Up to
32 selected remote tools are offered with full schemas at the start. Larger
catalogs offer the local `armada_discover_mcp_tools` tool instead. It searches
names and descriptions with a nonblank `query` of at most 256 characters; `*`
browses the catalog. An optional nonnegative `offset` selects a page. Each page
adds up to four full schemas to the next model request and returns descriptions
of at most 400 characters. Discovery executes no remote action.

The same client keeps the remote server's `Mcp-Session-Id` and sends it on later requests. When a request that carried that id is refused with HTTP 404, the client opens one new session with the same caller credential and sends that request once more. A second 404, a timeout, an HTTP 5xx response, or a network failure is returned to the caller. Armada's own MCP endpoint stays stateless and does not require a client to keep a session id.

Each run owns its catalog and loaded schemas. Calls to any selected remote name
still use the same authenticated MCP client, and the server checks authorization
again. Discovery does not grant access to a hidden tool. If a selected remote or
built-in tool already has the discovery name, the runtime offers the remote
catalog eagerly and preserves that tool's behavior.

The token reaches a captain in its environment as `ARMADA_MCP_TOKEN` (chat uses
`ARMADA_MCP_CHAT_TOKEN`), and it changes on every launch. With dock MCP delivery
enabled, every captain launch carries it, including Cursor, Gemini and OpenCode
captains that read only their dock configuration, and a subscription-account
login switch leaves it in place. Codex receives the reference as a command-line
override, so it holds in whichever `CODEX_HOME` an account selects. A Mux captain
receives its scoped servers file as `mux print --mcp-config <file>
--strict-mcp-config`: `mux print` loads MCP servers only from that flag, so the
launch reaches exactly its own Armada entry, while the captain's config directory
(`--config-dir`, or `~/.mux`) still selects its endpoints and settings. Scoped and
dock configuration files only reference the variable, in each client's own
syntax:

| Runtime | Reference |
| --- | --- |
| Claude Code | `"headers": { "Authorization": "Bearer ${ARMADA_MCP_TOKEN}" }` |
| Gemini | `"headers": { "Authorization": "Bearer ${ARMADA_MCP_TOKEN}" }` |
| Cursor | `"headers": { "Authorization": "Bearer ${env:ARMADA_MCP_TOKEN}" }` |
| OpenCode | `"headers": { "Authorization": "Bearer {env:ARMADA_MCP_TOKEN}" }` |
| Codex | `bearer_token_env_var = "ARMADA_MCP_TOKEN"` |
| Mux | `"auth": { "type": "bearer", "bearerToken": "${ARMADA_MCP_TOKEN}" }` |

A captain can list its own environment, and whatever it lists reaches its model
provider, so a captain never inherits an admiral secret. Before a local launch
applies its own variables, it removes every inherited variable whose name is a
model provider's `apiKeyEnv`, the typed-decision `apiKeyEnv`, or a name in the
`captainEnvironmentScrubNames` setting, plus every `ARMADA_` variable whose name
has a credential part (`KEY`, `TOKEN`, `SECRET`, `PASSWORD`, `PASS`,
`CREDENTIAL`, `CREDENTIALS`). The `ARMADA_TEST_` namespace is exempt from that
name rule because it carries test fixtures to fake agents. The variables the launch then sets on purpose
(`ARMADA_MCP_TOKEN`, a per-captain provider key) still reach the captain. Claude
Code captains are also refused the bare environment-listing commands (`env`,
`printenv`, `set`, `export`, `export -p`, `declare -x`, `declare -p`) through
`--disallowedTools`; `env NAME=value command` stays allowed.

A running Codex captain's tool inventory runs `codex mcp list` and `codex mcp get` on the host that executes that captain. A checkout binding on the dock selects that host. With no binding, a configured Harbor route selects it. With no remote host selected, the commands run on the Admiral. A selected host whose owner cannot be authenticated, or that does not accept the probe, reports the inventory unavailable.

The captain tool inventory of a running Mux mission captain lists the MCP
servers that mission's launch delivers: it reads the servers file the launch
plan builds, not the captain's config directory, whose servers `mux print`
never loads. With dock MCP delivery disabled the launch passes no
`--mcp-config`, so the inventory lists no MCP server. It probes each delivered
server with the credential its `auth` object declares, which for the Armada
entry is the mission owner's own scoped token. The probe is Armada's own MCP
client, so it shows what the delivered file grants, not that the installed Mux
client can use it: `mux print` does not report an MCP server it failed to
connect. The admiral image builds Mux against a patched Voltaic, because the
unpatched MCP client cannot read the endpoint's event-stream responses or its
`{}` `ping` result (see "Mux Runtime" in `docs/DOCKER.md`). Mux names an MCP
tool `<server>.<tool>`, so a Mux captain calls `armada.armada_status`. Mux
counts every tool schema against the endpoint's `contextWindow` in
`endpoints.json`, which defaults to 32768 tokens. The full Armada catalog alone
estimates at about 58,000 tokens, so a Mux endpoint with the default window
fails every run with `context_limit_exceeded` before it calls the model. Set
`contextWindow` to what the model actually serves. The
proof for a Mux captain is still the read-only smoke mission that lists its
tools. The only Mux CLI call is
`mux --version`, which makes no provider request. The inventory always lists a
`Mux Built-In Tools` entry. Its tool calling flag, base URL and adapter come from
the endpoint the launch selects in `endpoints.json` in the captain's config
directory (`--config-dir`, else `MUX_CONFIG_DIR`, else `~/.mux`): the named
endpoint, else the default one, else the first. Tool calling is on unless that
endpoint's `quirks.supportsTools` is `false`. The built-in tool count and names
are not reported by `mux --version`, and only `mux probe`, which sends a
completion request to the provider, reports them, so the entry and the summary
say that the count is not reported. When the endpoint cannot be read, the entry
names the reason and does not report tool calling as enabled.

Because the token is caller-scoped, a mission reaches only its owner's records
and the captain tools listed above. The endpoint applies the same limit to tool
discovery and calls, regardless of the owner's administrator role. Every MCP
credential goes through the shared authentication service. The environment
variable name carries a scoped session token; it grants no process-wide
administrator identity. Captain prompts must still keep dispatch, administration,
deployment, restore, purge and server-control actions outside mission scope.

### Captain evidence reads

`armada_voyage_status` requires an authenticated caller. A global administrator
can read any voyage. A tenant administrator can read voyages and mission rows
in that tenant. An ordinary user can read only its own voyages and mission
rows. A mission token keeps user scope even when its owner is an administrator:
it can read the voyages linked to its mission (see the evidence links below)
and the same-tenant, same-owner mission rows in them. A mission row stored
without a user counts as the default owner's. Other owners' rows do not appear
in the summary counts or mission details. A missing or out-of-scope voyage
returns `Voyage not found`.

A mission token's evidence links are:

- its own report chain: its voyage, its direct `DependsOnMissionId` and
  `ParentMissionId` sources, and the failed parent of a marked rescue root;
  a `Challenger` mission's direct source is the Judge mission its brief names,
  when that Judge's `judge_challenge.dispatched` record names the Challenger;
- the objectives that authorize the mission or one of those direct sources, by
  `MissionIds`, by `VoyageIds`, or through the objective named by the voyage
  brief;
- the ancestor chain (`ParentObjectiveId`), the `BlockedByObjectiveIds`, and the
  direct children of those authorizing objectives;
- every objective, mission and voyage id that an authorizing or ancestor
  objective cites in its description, acceptance criteria or evidence links.
  Citations are followed one hop: an id cited by a cited objective is not
  linked;
- the missions and voyages that any linked objective lists, and the voyage of
  any direct source.

Every linked record must have the caller's tenant and owner. A sibling
objective, an unrelated objective and any record of another owner stay out of
scope.

A refused read returns `Error` (`Mission not found`, `Objective not found` or
`Voyage not found`) with `Reason` and `Detail`. `Reason` is `out_of_scope`
when the caller's owner holds the record but no evidence link reaches it, and
`not_found` otherwise. An absent, purged or other-owner record all read as
`not_found`, so a denial never reveals another owner's record.

These reads, `get_objective`, `armada_mission_status`, `armada_voyage_status`
and `armada_mission_change_summary` accept an abbreviated id: the prefix and the
eight-character creation-time part of a full id, such as `msn_abcd1234`. The
abbreviation resolves to the one full id the caller can see (its own owner for a
mission caller, its normal read scope for an operator), and the normal scope
check then applies. Records the caller cannot see are never candidates, so an
abbreviation of another owner's record reads as `not_found`. When two visible
records share the abbreviation, the read returns `Reason` `ambiguous_id`,
`Candidates` with the full ids it matches, and no record; pass the full id. An objective that cites an abbreviated id links the one
record it resolves to, the same as the full id, and an ambiguous citation links
nothing.

Every MCP tool resolves abbreviated ids the same way before it runs. Each
argument named `id` or ending in `Id` or `Ids`, at any depth, whose value is an
abbreviation is replaced by the full id, so a write stores the full id. No
visible match returns `Reason` `not_found`, two return `ambiguous_id` with
`Candidates`, and the tool does not run. The REST GET-by-id routes apply the
same rule (`REST_API.md`, "Record IDs"). Mission, voyage, objective, check-run, incident, merge-entry,
signal, captain, vessel, fleet, dock, release and deployment ids resolve; any
other id is passed as given. Delete and purge tools take full ids only: an
abbreviation there returns `Code` `full_id_required` with text that says to use
the full id, and nothing is deleted. Objective writes made through REST resolve
the objective being updated, `parentObjectiveId` and every linked-id list in the
objective service, against the scoped read that validates each field.

`armada_mission_output` returns digest-backed pages of persisted output. A mission
caller can read its own output, missions in the same voyage, and its direct
`DependsOnMissionId` or `ParentMissionId` source. Each source must have the same
tenant and owner. Use `offset` and `length`, follow `NextOffset` while `HasMore`
is true, and check the final reconstructed UTF-8 content against `Sha256`.
`Sha256`, `TotalLength` and `TotalUtf8Bytes` describe the joined content and are
the same on every page; `PageSha256` and `PageUtf8Bytes` describe the page's own
`Content`, so each page can be checked as it arrives.
`Complete` identifies a final artifact; it does not mean that one page contains
all content. A finished, successful Test Engineer or Product Manager stage whose
report lacks a section its persona template requires is not complete:
`TruncationReason` is `report_contract_missing` and `MissingReportSections` names
each missing section. A heading of any level that starts with the section name,
in any case, counts as the section. A finished Judge whose verdict is PASS,
whatever its final status, is held to the rule its PASS verdict check applies: each review section its
mode requires, then `Acceptance Criteria` when its brief lists criteria, so a
Judge refused for a missing section names the same section here. A later autonomous rescue stage can also read the failed parent
named by its marked rescue root, reached through the same-owner, same-voyage
and same-vessel dependency chain. A legacy rescue title alone does not grant
this additional read. A mission caller can also read any mission its evidence
links reach: a mission listed or cited by a linked objective, or a mission in a
linked voyage. A same-owner mission of an unlinked objective stays out of scope.

`armada_mission_status` uses the same evidence scope before it returns mission
metadata or context events. Its default response omits the description and
report bodies. Pass `includeDescription=true` to read the full stored brief
when the rescue scope excerpt omits original instructions or diagnostics.
Unrelated and cross-owner mission IDs return not found.

REST mission and voyage reads use these same caller scopes. A mission token
does not gain broader reads from its owner's administrator role. Voyage
summaries and embedded mission details exclude rows outside the caller's scope.

`get_objective` returns the full record of any objective the caller's evidence
links reach. `armada_enumerate` with `entityType=objectives` lists exactly that
set for a mission caller, so every listed objective is readable with
`get_objective`. Operator reads retain their normal caller scope.

`armada_enumerate` scopes every entity type by the same rule as that entity's
per-id read. A global administrator lists every record. A tenant administrator
lists its own tenant's records. Any other caller lists only the records it owns
in its tenant, except playbooks, which every caller in a tenant reads and so
lists. A mission caller lists as its owner without the owner's administrator
rights. Its `missions` and `voyages` listings return exactly the records
`armada_mission_status` and `armada_voyage_status` let it read, and
`totalRecords` counts only those records.

`armada_enumerate` with `entityType=checks` does not read each Check's `output`
log unless `includeTestOutput` is true. Each row carries `outputLength`, the
log's length in characters as the database computes it, so a page of Checks
never loads their logs. `get_check_run` returns one Check's log.

`armada_enumerate` refuses an argument it does not declare. The error names
each unknown argument and lists the accepted ones, so a misspelled filter such
as `statuses` never returns an unfiltered listing. Use `status` to filter by
one status.

A captain may omit the id on these reads. `armada_mission_status` and
`armada_mission_output` then read the captain's own mission, and `get_objective`
reads the objective that lists the captain's mission, or else its voyage. A
caller with no mission credential must pass the id; an omitted id returns an
error that names the missing argument (`missionId` or `objectiveId`).

### Papercut listing groups

`armada_list_papercuts` collapses captain reports into groups. `groupBy` picks
the grouping, and the filters (`vesselId`, `category`, `minSeverity`,
`sinceHours`) apply to the reports before they are grouped:

- `vessel` (the default) returns one group per vessel. `Key` is the vessel id
  (`-` for reports with no vessel), and `Category` names the category with the
  most reports.
- `category` returns one group per vessel and category. `Key` is
  `<vesselId>|<category>`.
- `title` returns one group per vessel, category and normalized title, and
  applies the `papercut_merge` decision to fold same-issue title groups.

Every group carries `GroupBy`, `VesselId`, `VesselName` (null when the vessel
record cannot be read), `Count`, `DistinctCaptainCount`, `HighestSeverity`,
`FirstSeenUtc`, `LastSeenUtc`, `CategoryCounts` (reports per category name),
`TitleGroupCount` (distinct titles in the group), the newest report's sample
fields and up to five `SampleMissionIds`, newest first. A vessel or category
group also carries `TitleSubgroups`: its 10 largest title groups by count and
then most recent report, each with `Key` (the matching title-group key),
`Category`, `NormalizedTitle`, `SampleTitle`, `Count` and `LastSeenUtc`. The
response names the mode in `GroupBy`; `ungrouped=true` returns reports instead
and reads `GroupBy` as `ungrouped`.

### Papercut listing pages

`armada_list_papercuts` returns at most 200 groups or reports per page. Pass
`NextContinuationToken` as `continuationToken` until `HasMore` is false. Keep the
filters, `limit`, `scanLimit`, `groupBy` and `ungrouped` unchanged. The token
records its grouping mode; a token used with another mode returns an error that
names both modes. The token fixes the
source cutoff and time filter. New reports do not displace the original page
set. If retained events or merged groups change, the tool asks you to restart
instead of silently skipping rows.

`TotalFiltered` counts matching reports; grouped responses also include
`TotalGroups`. `TotalEvents` counts source events before filters. `ScanTruncated`
means that `scanLimit` bounded the scan. Paging covers only that scanned set;
increase `scanLimit` when needed, up to 5000.

**Local stdio.** `armada mcp stdio` runs the tools in-process with the local
settings file and database credentials. It sets an explicit local operator
identity; it does not fall back to a default context. It builds its own mission
status transition service, so `armada_transition_mission_status` applies the
same validation, completion gates and landing as the admiral's endpoint. It also
supplies the record-backed services the admiral's endpoint supplies: the mission
service (`armada_review_hold`), the remote-trigger service (the AgentWake
tools), releases, deployments, runbooks, captain bench and unbench, unlanded
branches, terminal-voyage reconciliation, disk lifecycle, and a process stop for
`armada_stop_captain`. Services that exist only inside the running admiral (the
dispatch hold, the objective scheduler, planning and refinement session
coordinators, harbor jobs, the context index, saved Ask
conversations, the captain model probe and the fixture battery) are not built, so
their tools are not registered on stdio or report the service as unavailable.

**SSH stdio bridge.** `scripts/mcp-ssh-http-bridge.mjs` requires
`ARMADA_MCP_AUTH_HEADER_FILE`, an absolute path on the server to a file that
holds one credential header line, for example `X-Api-Key: <admiral API key>`.
Protect it with mode `600`. curl reads the header from that file on the server,
so the credential never appears on the workstation, in the SSH command line or
in the remote process list. Without the variable the bridge refuses each
request with a named error.

Bind the service to a trusted interface and use a protected transport even with
authentication. Do not expose the MCP port to an untrusted network.

## Catalog Availability

Some families register only when their backing service is available. Examples
include merge processing, code indexing, Checks, delivery records, objective
scheduling, backups, and unlanded-branch reporting.

Use live discovery to decide what the connected Admiral supports. Do not infer
availability from repository source or from this document.

Vessel branch listing, push and merge are REST and dashboard operations only.
No MCP tool writes vessel branches; see `docs/REST_API.md` for their
authorization and refusal contract.

## Backup And Restore

`armada_backup` and `armada_restore` call the same backup service as REST
`GET /api/v1/backup` and `POST /api/v1/restore` and the WebSocket `backup` and
`restore` commands. `docs/REST_API.md` describes the archive contents.

- `armada_backup` (`outputPath` optional) takes a verified provider-native
  backup of the configured database. The manifest records the provider's
  schema version and record counts. A failed native backup or isolated restore
  check is a tool error carrying a stable reason, such as
  `postgresql_backup_failed`, and no archive is written.
- `armada_restore` (`filePath` required) replaces the database only on SQLite,
  after a verified safety backup. PostgreSQL, MySQL and SQL Server are refused
  with `restore_unsupported_for_provider_<type>`, and an archive from another
  provider with `backup_provider_mismatch`. Nothing is changed in either case.
- Archived `settings.json` has every secret replaced by `[REDACTED]`, and the
  manifest sets `settingsRedacted` and `redactedSettingCount`. On restore, each
  redacted value keeps this host's current secret. A redacted value with no
  local counterpart is omitted, so a placeholder is never written.

## Native Memory

### armada_search_context

One read-only local query over applicable external guidance, native records and
the existing vessel code index. Supply `query`; operators also supply
`vesselId`. Mission credentials use their active, owned mission's vessel and
ordinary owner visibility. Optional arguments are `limit` (default 20, maximum
40), `maxBytes` (default 24000, capped by server settings), and `includeCode`
(default true).

Returns `Required`, `Results`, `Sources`, `ResultBytes`, `Truncated` and
`VesselId`. Required guidance is complete and outside optional text limits.
Results retain source class, reference and version. Search never builds a code
index. It calls an embedding endpoint only when `contextRetrieval.semanticEndpointId`
pins a local one, and `Sources` then carries a `semantic` entry. A source can be unavailable; inspect `Sources`
before treating an empty result as proof of absence. Existing context fetch
settings control the kill switch, per-mission calls and optional byte cap.
Mission recall shares one durable counter with mission-token `search_memory`
calls. The limit
is the lower of `contextRetrieval.maxCallsPerMission` and, while code indexing
is enabled, `codeIndex.captainSearchMaxCallsPerMission`. Restart, service
registration and normal mission updates cannot reset the counter. A supplied
mission or vessel cannot change a mission credential's scope or budget.
`Sources` reports search mode and code-index freshness even without matches.
See [unified retrieval](CONTEXT_INDEX.md#unified-retrieval-and-the-armada_search_context-tool)
for details and current migration limits.

### Native record tools

Durable native memory for captains, separate from the shared external memory
repository. Records are classified as **Episodic** (what happened), **Semantic**
(a standalone fact) or **Procedural** (how to do something). Working memory is
never stored. Every tool uses the authenticated caller. The memory service
applies that caller's tenant, user, and record-scope permissions.

### search_memory

Search before writing, to correct an existing record instead of duplicating it.
Ordered by salience, then newest.

- `search` (string) - substring over content, summary, topic, key and tags
- `type` (string) - `Episodic`, `Semantic` or `Procedural`
- `topic` (string) - exact topic
- `vesselId` (string) - the vessel a record is about or came from
- `pageNumber`, `pageSize` (integer)

Returns a paged `EnumerationResult` of memory records.

This is a compatibility record-management query. Generated task instructions
use `armada_search_context`. A mission token fixes the vessel and ordinary
owner scope, ignores a different supplied vessel, and spends the shared recall
budget. Operator listing retains its existing filters and ordering.

### get_memory

Read one record with its full content. Args: `memoryId` (required).

### create_memory

Record a finding, or update the record that already carries the same `key`.
Args: `content` (required); optional `type` (default `Semantic`), `topic`, `key`,
`summary`, `salience` (0.0 to 1.0, default 0.5), `tags`, `sourceKind`,
`sourceVoyageId`, `sourceMissionId`, `sourceVesselId`, `sourceDetail`,
`vesselId`, `scope`, `expectedVersion`. A write by key replaces the record's
fields, so send every field you want kept.

### update_memory

Change one record. Only the fields supplied change, and the version increases.
Args: `memoryId` (required), then any of `type`, `topic`, `key`, `summary`,
`content`, `salience`, `tags`, `vesselId`, `sourceDetail`, `scope`,
`expectedVersion`.

### delete_memory

Delete a record that went stale or wrong. Args: `memoryId` (required).

### Refusals

A refused call returns `Error` with a `Code`: `invalid`, `not_found`,
`forbidden`, `conflict`, or `native_memory_forbidden`. A conflict means the record
changed since it was read, or the key belongs to another record. Read the record
again and retry.

`native_memory_forbidden` refuses `create_memory` and `update_memory` for a
captain whose mission belongs to an objective that forbids native memory: the
objective carries the tag `no-native-memory`, or a rollout constraint or non-goal
saying the work writes no memory or no native memory. The rule covers every stage
of the objective's voyages and every rescue stage descended from them. A captain's
launch credential names its mission, so the rule reads the mission from the
caller, not from a tool argument. Operator credentials name no mission and are
not limited. Deliver the finding in the report instead.

A rule from the Shared Memory section of a brief wins over a native record on
conflict. The memory tools never write to that repository.

## Captain Typed Decisions

These mission-scoped tools let a captain consult the typed-decision system
(TypeSafe Jev) for a structured second reading on a judgement it is about to
make. They are caller-scoped, next to the memory tools. Authority does not
travel with them: every call redacts its state before egress (there is no
per-mission call cap) and has **no side effect on any Armada record** - it
dispatches nothing, lands nothing, edits no objective, and writes no memory.
Every call writes exactly one event, carrying only a `state_sha256` and byte
count (never the state): `typed_decision.captain`, except that a custom
decision which reaches the provider records under its own decision point. A call returns typed answers, or an
`available: false` result with an `unavailableReason` the captain treats as
"decide it yourself".

The tools gate nothing, so no threshold applies: every answer comes back with
its confidence, for the captain to weigh. A helper answers while its decision
is in `Gate`. In `Shadow` it consults and records, then returns
`unavailableReason: shadow`; in `Off` it returns `unavailableReason: disabled`.
`docs/TYPED_DECISIONS.md` owns the details.

The tool ships enabled (`typedDecisions.captainTool.enabled` is `true`); when an
operator sets it `false`, every call returns `unavailableReason: disabled`. The
global `typedDecisions.mode` is the kill switch for every tool: while it is
`Off`, no call reaches the provider and each returns
`unavailableReason: typed_decisions_off` (`typed_decisions_no_key` when no
provider key resolves).
The work is authorized engineering on owned systems; authentication and
access-control protocol content is ordinary engineering and is passed like any
other state.

### armada_typed_decision

Answer typed questions about a piece of state. Args: `state` (required, a string
or an object whose string fields are redacted), `questions` (required, an object
keyed by question id where each value is `{ type: choice|score|noul,
instructions, criteria|levels, trueMeaning?, falseMeaning? }`), and optional
`missionId` for scope and event attribution. `choice.criteria` is a
name-to-meaning map; `score` takes an ordered `criteria`/`levels` array; `noul`
takes optional `trueMeaning`/`falseMeaning`. Returns `{ available, answers }` or an
unavailable result whose reason is one of `disabled`, `invalid`, or a client
reason (`timeout`,
`http_401`, `http_429`, `parse`, `exception`, ...).

Every typed-decision tool records its call on one mission. A captain's
credential names the mission it was issued to, and that mission is used even when
the call passes a different `missionId`; the ignored id is logged. A caller
without a mission credential, such as an operator session, is recorded against the
`missionId` it passes, or against the authenticated caller when it passes none.

When several independent judgements share the same state, send them in this one
call. When the judgement is a list of real items, use `armada_score_items`
instead of asking the model to count.

### armada_score_items

Ask one yes/no question per listed item and let code tally. Args: `items`
(required, an array of strings or objects with optional `id` and `text` /
`content` / `excerpt`), `claim` (required, the statement that is true or false
of each item), optional `trueMeaning`, `falseMeaning`, `pick` (Choice
instructions for the single best item, with `none` and `unclear` options), and
optional `missionId`. Empty slots are dropped. At most 32 items. Returns
`{ available, answers, items, ranked, expectedCount, best, model }`: each
item carries its `noul`, `ranked` is highest noul first, and `expectedCount` is
the sum of the noul values. The tools gate nothing; the captain weighs the
answers. Same unavailable reasons as `armada_typed_decision`. When `pick` is
set, item IDs must be unique and cannot be `none` or `unclear`; an invalid
list returns `invalid` before a provider call.

For evidence review, pass a short list of source excerpts or trace steps as
`items`, each with a stable local ID such as `evidence_0`. Include the claim
in `claim`, for example, “This excerpt supports the claim that the retry uses
the same commit.” Use `pick` to select the strongest supporting excerpt.
Resolve the returned ID against the original list and read that evidence.
A selected ID is a review pointer, not proof of the claim. `none` and `unclear`
remain valid outcomes. Retrieval must keep the evidence needed to decide;
Jev cannot select an excerpt that the caller omitted.

### armada_check_premise

Check the captain's own reading before it starts (decision `premise_check`).
Args: `restatement` (required, the captain's one-paragraph restatement), and
optional `objectiveTitle`, `objectiveDescription`, `acceptanceCriteria`,
`stagePersona`, `facts`, `missionId`. It returns typed readings on whether the
restatement contradicts the scope, assumes an absent symbol or file, or names an
unrequested deliverable, plus whether a repository fact or an owner ruling is
missing. It never blocks the captain; it informs it. Dormant (returns
unavailable) until the `premise_check` decision is enabled.

An available answer also includes `missingPremiseFact`. A positive
`assumes_absent_symbol` reading (Noul greater than 0.5) and a valid fact choice
return `{status: "identified", key, value}`. Otherwise the identity is
`{status: "unknown", key: null, value: null}`. The choice offers up to eight
top-level scalar facts; nested values, secret-named keys, keys over 96 characters,
and values over 256 characters are excluded. The returned value is the centrally
redacted value sent to the provider. The full `facts` input remains in the
reasoning context. A selected fact is advice to verify, not proof of absence.

### armada_memory_triage

Triage a memory candidate before writing it (decision `memory_record`), for the
Recorder. Args: `candidate` (required), optional `existingRecords`, `missionId`.
It returns typed readings on whether the memory type fits, whether it duplicates
an existing record, whether it will go stale, and whether it belongs in shared
external memory rather than native memory. It writes nothing. Dormant until the
`memory_record` decision is enabled.

### armada_check_prior_art

Check whether the work already exists before writing a new type (decision
`prior_art`). Args: `plan` (required, what the captain is about to build:
the types, methods, and files it plans to write) and `missionId` (used to
resolve the vessel to search, and for scope and event attribution). The
tool runs a deterministic retrieval over the target tip, unlanded mission
branches, preserved and `recover/` refs, and open objectives for the
identifiers in the plan. It returns the candidates, each with its surface,
`path:line` (or objective id), ref, and excerpt, plus typed readings: a
per-candidate `delivers` choice (`same_capability`, `partial_overlap`,
`related_only`, `unrelated`) and the `already_done`, `integrate_not_duplicate`,
and `reimplements` readings. A branch or ref candidate carries a bounded excerpt
(at most 30 lines) read from that ref, because the file is not in the captain's
checkout. The captain verifies each `path:line` itself. The answer also
carries `searchComplete` and `unsearchedSurfaces`: a surface whose search failed
is named there and in the decision state, and when no repository search ran the
answer says so in `note`, so "nothing found" is never read as a verified
absence. The tool edits nothing and never blocks. It is caller-scoped like the other captain tools, and it is
dormant (returns unavailable) until the `prior_art` decision is enabled.

### armada_mission_attempts

List the recorded process attempts of one mission, oldest first. Args: `missionId` (required) and optional `limit`
(default 100, maximum 1000, newest kept). The REST route `GET /api/v1/missions/{id}/attempts` calls the same reader, so
both surfaces share one authorization rule and one answer.

Each attempt names the captain, runtime, model and runtime version that ran it, when it started and ended, and its
outcome (`Running`, `Completed`, `Failed` or `Interrupted`) with an exit code and a reason code
(`exit_nonzero`, `exit_unknown`, `superseded_by_relaunch`). The runtime version is the executable's own `--version`
line; it is empty when the probe was slower than the 3-second launch wait, failed, or the attempt ran on a Harbor
runner. The answer carries `MissionId`, `Attempts` and `Truncated`; `Truncated` true means older attempts exist beyond
`limit`.

The tool reads a mission through the caller's scope: an administrator reads any mission, a tenant administrator its
tenant's, and any other user its own. A mission outside that scope answers `{"Error": "Mission not found"}`. It is
not offered to mission callers. It is read-only and writes no record. Ask threads may call it.

### armada_battery_packs, armada_battery_runs, armada_battery_run

Read the fixture battery (see [Fixture Battery](BATTERY.md)). `armada_battery_packs` lists the packs with their tasks
and toolchain. `armada_battery_runs` lists runs newest first; args: optional `captainId`, `packId` and `limit`
(default 50, maximum 200). `armada_battery_run` reads one run by `runId`, with the grade, timings and evidence of each
task; an unknown run answers `{"Error": "Battery run not found"}`.

The tools are read-only and answer a global administrator only; any other caller, including a mission credential,
receives a refusal. No MCP tool starts a run, because a run dispatches real missions and spends captain tokens; start
one with `POST /api/v1/battery/runs`.

### armada_suite_lease

Acquire or release the Armada suite lease before a full-suite run (for example
`scripts/check.sh`). Args: `action` (`acquire` or `release`) and, for
`acquire`, optional `waitSeconds` (0-120, default 30). The lease takes the
per-vessel suite slot and then a host command slot, the same slots the
definition-of-done gate and armed checks use (`docs/reference/backend-dod.md`).
A mission credential acts only for its own mission; a caller with no mission
credential is refused (`mission_required`).

`acquire` waits at most `waitSeconds` for a grant, so it always answers inside
an MCP client's call timeout:

| `Status` | Meaning |
| --- | --- |
| `granted` | The lease is granted to this call. |
| `held` | The mission already holds the lease. |
| `queued` | The slots are held. `Position` is the request's place in the queue. |

A queued request stays in the queue after the call returns, including when the
client cancels or times out the call. The next `acquire` from the same mission
joins that request and keeps its place; it does not queue again. The acquire
that queues a request records `captain.suite_lease_wait` with the position when
the wait starts; a retry records no second event. A queued request counts as an active lease for stall detection, so
its wait is not a stall.

`release` frees a held lease (`released`), withdraws a queued request
(`withdrawn`), or answers `idle` when the mission has neither. When the captain
process exits or the mission ends, a held lease is released and a queued request
is cancelled.

### armada_mission_change_summary

List every path one mission stage committed, with its status. Args: optional
`missionId` and optional `maxPaths` (default 1000, maximum 5000). The REST
route `GET /api/v1/missions/{id}/change-summary` calls the same reader, so both
surfaces share one authorization rule, one set of bounds and one answer.

The tool compares the stage's recorded start commit with its recorded final
commit (`Mission.CommitHash`) in the vessel's bare repository. The start commit
is the stage start record written for that mission when its dock was assigned,
then the dock's stored Git anchor snapshot for that mission, then the dock start
record written at provisioning, then a mission start ref that is itself a full
commit id; `StartCommitSource` names which. The stage start record outlives the
dock, so a stage that has ended still answers. Only full commit ids are compared,
so moving a branch never changes an answer's commit pair, and the tool needs no
dock. The answer is not a diff against the current target branch and holds no
uncommitted files.

A mission credential always reads its own mission: a `missionId` naming another
mission is ignored (the answer says so in `Warnings`), even when the
credential's owner is an administrator, and the credential's own mission must
have its tenant and owner. Any other caller reads a mission its tenant and role
let it read: an administrator reads any mission, a tenant administrator its
tenant's, and any other user its own. A mission outside that scope answers
`mission_not_found`. The vessel and both commits come from the mission, never
from the request.

The answer carries `MissionId`, `VesselId`, `Available`, `Complete`, `Reason`,
`Message`, `StartCommit`, `StartCommitSource`, `FinalCommit`, `Paths` (each
`Status`, `Path`, and `PreviousPath` for a rename), `PathCount`,
`TotalPathCount`, `MaxPaths`, `CallsUsed` and `Warnings`. `Available: false`
means nothing was compared and never means nothing changed; `Reason` is one of
`invalid`, `mission_not_found`, `no_vessel`, `repository_unavailable`,
`start_commit_not_recorded`, `final_commit_not_recorded`, `git_unavailable`,
`start_commit_missing`, `final_commit_missing`, `invalid_commit`, `timeout`,
`output_limit_exceeded`, `git_failed`, `unreadable_output`, `busy`, `budget`,
or `exception`. `Complete: false` with `Reason: path_limit` lists the first
`maxPaths` paths and gives the total in `TotalPathCount`. Each read runs under
a 30-second deadline and a 4 MiB output limit, at most four reads run at once
(`busy` after a 2-second wait), and a mission credential may call it 60 times
per admiral process (`budget`). Caller cancellation propagates. It writes no
record and calls no embedding or classifier provider.

### armada_change_quality

Get a multi-dimension quality read of a focused diff before the Judge (decision
`change_quality`). Args: `diff` (required, the unified diff to review) and
`missionId` (scope and event attribution). Returns per-dimension model
signals — DRY, cognitive complexity, modularity, readability, maintainability —
as weaknesses to weigh, not a synthetic score. It takes no action: it never
lands, dispatches, fails a stage, or edits a record, and the diff is redacted
before egress. Caller-scoped like the other captain tools, with no call cap,
and dormant (returns unavailable) until the `change_quality` decision is enabled.
The deterministic backing (the Slop core-rule check and the complexity metric)
is authoritative at the orchestrator gate; this tool is the captain's advisory
read.

### armada_corpus_prelabel

Read one captured record and return the provisional KIND of the decision it
holds (decision `corpus_prelabel`), so a decision-corpus line starts from a
reading instead of a blank field. Args: `record` (required, the record's fields:
`input_type`, `title`, `summary`, `root_cause`, `failure_reason`, `payload`,
`platform_said`, `disposition`, `failed_questions`) and optional `missionId`. It
asks exactly one choice question over the corpus kinds and nothing else: it
proposes no answer to the decision itself, writes no corpus line, and changes no
record. The record is redacted before egress, and the one event it records
carries the state's hash and byte count under the `corpus_prelabel` decision,
never the state. An empty `record` is `invalid`, never a guessed kind. Dormant
(returns unavailable) while the `corpus_prelabel` decision is Off. Its
operator-side caller is `scripts/autonomy/draft-corpus-line.mjs`, which keeps
every answer as a draft a person confirms.

### armada_run_custom_decision

Run a custom decision an operator defined, by name. Args: `name` (required),
`context` (required, a non-empty object whose fields the decision's
`stateFields` select, for example `diff`, `output_tail`, `changed_paths`), and
optional `missionId`. Returns `{ available, name, flagged, confidence, model,
flaggedQuestions, answers }`: `confidence` is the gate value (the highest Noul
probability or finding-option confidence, or null when nothing can gate), and
`flagged` is true only when the decision is bound, in `Gate`, and at or above
its threshold. A decision in `Shadow` consults and records, then returns
`unavailableReason: shadow`. An unknown or `Off` decision, or a disabled
captain tool, returns `unavailable` and records one `typed_decision.captain`
event (`not_found`, `dormant`, or `disabled`). A call that reaches the
provider records one `typed_decision.gated`, `typed_decision.rule_kept`, or
`typed_decision.unavailable` event under the decision point `custom:<name>`.
A `MissionDiff` decision also runs by itself when a Worker stage hands off,
on the vessels its `vessels` list names (every vessel when empty). A call by
name through this tool ignores that scope.

## Retained Preflight Samples

### armada_typed_decision_sample

This operator-only tool reads one retained, redacted `preflight` input by its
typed-decision event id. It requires a global administrator, the retention
switch, and the `preflight` decision opt-in. The handler checks the caller
before it reads event metadata or the host-local sample store. Mission tokens,
tenant administrators, and ordinary users are refused even if they call the
registered handler directly.

| Argument | Type | Required | Meaning |
| --- | --- | --- | --- |
| `eventId` | string | yes | The typed-decision event id. |
| `offset` | integer | no | Character offset in the retained state; defaults to `0`. |
| `maxChars` | integer | no | Requested page size; defaults to `1024` and is capped at `4096`. |

The response gives the event id and timestamp, `preflight` decision point,
state SHA-256, state and page UTF-8 byte counts, redactor version, page text,
offsets, and completion state. It omits objective and mission ownership
fields, model answers, and request provenance. `q4SpecificPremise` is `null`
and `q4RationaleStatus` is `not_recorded` when the stored evidence has no
explicit premise reference.

Availability distinguishes `event_not_found`, `not_typed_decision`,
`wrong_decision_point`, `sample_not_retained`, `scan_incomplete`, and
`unavailable`. It returns `forbidden` for an unauthorized caller,
`invalid_request`, `invalid_offset`, or `invalid_page_size` for a malformed
request, and `sample_store_unavailable`, `retention_disabled`,
`event_unavailable`, or `event_metadata_unavailable` when the required local
service or event data cannot be used. The bounded lookup scans the event UTC date file and the next
date file to cover a write across midnight. It stops at fixed file, byte,
record, or line limits. The two files share an 8 MiB byte budget. A limit or
corrupt line returns `scan_incomplete`, never a false missing-sample result.
A returned state must have the current
redactor version, match the event digest and byte count, and pass current
redaction unchanged. This tool changes no gate or preparation state and
records no provider call.

## Typed Decision Outcomes

### armada_typed_decision_outcomes

This operator-only tool joins recorded typed-decision calls to what later
happened to the record each one judged. It requires a global administrator
outside a mission; any other caller receives `forbidden`.

| Argument | Type | Required | Meaning |
| --- | --- | --- | --- |
| `sinceDays` | integer | no | Window length in days, ending now; defaults to `30`, clamped to 1-365. |
| `decision` | string | no | One decision point to report; omit for every decision. |
| `maxEvents` | integer | no | Most calls to read, newest first; defaults to and is capped at `20000`. |

The response gives `sinceUtc`, `eventsRead`, `truncated`, and one row per
decision. A row counts `events`, `withSubject`, `joined`, `subjectMissing`,
`landed`, `failed`, `cancelled`, `objectiveCompleted`, `withIncident` and
`rescued`, gives `subjectShare` and `joinedShare`, and breaks the calls down
`bySubjectType` and `byEventType`. `byConfidenceBand` repeats the join for the
model's action confidence bands `<0.50`, `0.50-0.80`, `0.80-0.90`, `>=0.90` and
`none`. A mission subject joins when it is terminal; an objective subject when
it is completed or cancelled. A call recorded without `subject_type` falls back
to its mission, then its objective. Both rule-kept event names are read. The
response holds counts only, never record ids or state. Reading only; nothing
changes and nothing leaves the host.

## Typed Decision Evaluation

### armada_typed_decision_eval

Runs the synthetic typed-decision evaluation set against the live provider and
returns the report. Each case builds its requests through the decision's own
adapter, so it tests the questions production sends. A `Reference` case passes
when every stated answer holds for both variants; a `Consistency` case passes
when the named answers agree between its variants (choices equal, nouls within
0.20, scores within 0.50). A case the provider does not answer is `unavailable`,
neither passed nor failed. The run records one `typed_decision.eval` event and
spends about two provider calls per case. It also runs by itself when the
provider reports a model version that has not been evaluated.

Operator control: not caller-scoped, and the handler refuses any caller other
than a global administrator with `Reason: global_administrator_required`.

| Argument | Type | Required | Meaning |
| --- | --- | --- | --- |
| `decision` | string | no | Run only this decision point's cases (for example `failure_cause`). |

Returns `{ status: "complete", report }` with `model`, `total`, `passed`,
`failed`, `unavailable`, token totals, and per-case `outcome`, `failures`, and
`answers`; or `{ status: "busy" }` when a run is already in progress.

## Trace Records

Trace records describe each objective, voyage, mission, check run, incident,
merge entry and deployment compactly and outlive the raw rows. The record
content, the writers and retention are in `MCP_TOOL_CATALOG.md` section 8.28.
Both tools refuse any caller other than a global administrator with
`Reason: global_administrator_required`.

### armada_trace

| Argument | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | string | yes | Full id (`msn_`, `vyg_`, `obj_`, `chk_`, `inc_`, `mrg_`, `dpl_`) or short id: a prefix of a full id, at least four characters after the kind prefix, with or without that prefix. |
| `maxRecords` | integer | no | Most records returned; defaults to `60`, clamped to 1-200. |
| `includeMessages` | boolean | no | Return the final message of every record; defaults to `false`, which keeps it on the requested record only. |

Returns `requestedId`, `resolvedId`, `source` (`table`, `live`, `archive` or
`none`), `records` (the requested record first, then linked records breadth
first, including records read back from the archive),
`missingIds` (linked ids with no trace record and no source row),
`archivedKeys` (archive keys of missing records whose trace record is in the
bucket) and `truncated`. Each record has `id`, `kind`, `title`, `status`, the timestamps,
`vesselId`, `captainId`, `model`, `persona`, `stageOrder`, `branchName`,
`startRef`, `commitSha`, `landedSha`, `artifactIdentity`, `gate`, `check`,
`verdict`, `findingCount`, `failureReason`, `finalMessage`, `links`,
`timeline` and `archive`. A short id that matches several records returns
`Error`, `requestedId` and `candidates`.

### armada_trace_backfill

| Argument | Type | Required | Meaning |
| --- | --- | --- | --- |
| `sinceUtc` | string | no | Oldest creation time a table starts from on its first call; defaults to 120 days ago. Later calls resume from each table's cursor. |
| `maxRows` | integer | no | Most source rows one call visits; defaults to `2000`, clamped to 1-20000. |

Returns `sinceUtc`, `rowsRead`, `recordsWritten`, `recordsMissing` (rows that
disappeared before they were recorded), `failures`, `rowsByKind`, `cursors`
(per table), `done` and `storedRecords`.

## Memory Proposals

Three operator tools read and close the memory proposal store. A memory proposal
is a durable lesson the typed-decision system nominated for the owner's external
AI-Memory: the weekly papercut sweep (`memory_candidate`) or the review of a
finished Recorder stage (`memory_review`). Proposals live in the database,
because the AI-Memory folder is read-only to the admiral. Armada never writes
AI-Memory: the owner writes the rule by hand, then an operator records the
promotion with the AI-Memory commit that holds it. The model never dismisses or
promotes a proposal. An Open proposal moves once, to `Dismissed` or to
`Promoted`. The boundary between the two stores is stated in AI-Memory
`shared/sole-memory-source.md`.

All three tools are operator control. They are not caller-scoped, so a mission caller
can neither list nor call them, and each handler also refuses any caller other
than a global administrator with `Reason: global_administrator_required`.

### armada_list_memory_proposals

List proposals, newest first. Args: optional `state` (`Open`, the default,
`Dismissed`, `Promoted`, or `All`) and `limit` (default 25, maximum 200). Returns `{ State,
Count, Proposals }`. Each proposal carries `Id` (`mpr_` prefix), `Source`
(`papercut_sweep` or `recorder_seam`), `SourceKey` (a SHA-256 fingerprint of the
subject), `Title` and `Body` (both redacted), `TargetHint` (a plain-text
suggestion: `shared`, `repos/<vessel>`, or `machine-notes`), `Confidence`,
`RelatedRecordIds` (the missions or memory records it came from), `State`, the
dismissal fields, the promotion fields, `CreatedUtc`, and `LastUpdateUtc`.

### armada_dismiss_memory_proposal

Dismiss one open proposal. Args: `id`, `reason`, and `operator` (all required).
The proposal is kept with `State: Dismissed`, `DismissedBy`, `DismissedReason`,
and `DismissedUtc`; nothing is deleted and AI-Memory is not touched. Returns
`{ Dismissed: true, Proposal }`, or an `Error` when the id is unknown, a field is
missing, or the proposal is not Open. A dismissed subject is not proposed
again.

### armada_promote_memory_proposal

Record that the owner wrote one open proposal into AI-Memory. Args: `id`,
`commitSha` (the full 40-character hexadecimal AI-Memory commit that holds the
rule), and `operator` (all required). The proposal is kept with
`State: Promoted`, `PromotedBy`, `PromotedCommitSha` (stored in lower case), and
`PromotedUtc`. Armada does not read or write AI-Memory, so it does not check that
the commit exists. Returns `{ Promoted: true, Proposal }`. A short or
non-hexadecimal SHA returns `Reason: commit_sha_not_full`; a dismissed or already
promoted proposal returns `Reason: proposal_not_open` and changes nothing. A
promoted subject is not proposed again.

## Saved Ask Conversations

Two read-only tools list and read the saved Ask conversations of the authenticated
caller. They are operator tools: they are not caller-scoped, so a mission caller and
an Ask turn can neither list nor call them, and each handler also refuses a credential
that is not a human owner. A caller sees only conversations it owns, as on the REST
routes. No MCP tool sends a message, approves or rejects a proposal, changes a
conversation or deletes one; those stay human actions. The stdio server does not
register them.

### armada_list_ask_threads

List the caller's saved conversations, pinned first and then by newest activity.
Args: optional `pageNumber` (default 1), `pageSize` (default 25, maximum 100) and
`includeArchived` (default false). Returns the REST enumeration result. Each thread
carries `id`, `title`, `captainId`, `pinned`, `archived`, `lastActivityUtc`,
`activeTurnId` and `unreadCount`.

### armada_get_ask_thread

Read one conversation. Args: `threadId` (required), optional `beforeSequence`,
`pageSize` (default 10, maximum 50) and `includeToolCalls` (default false). Returns
`{ Thread, Turns }`, with turns oldest first inside the page. Page back by passing the
first `sequence` of the previous page as `beforeSequence`. Tool-call cards are left out
unless `includeToolCalls` is true. An unknown thread, or one the caller does not own,
returns an `Error`.

### armada_cli_permission_prompt

The permission prompt tool of a saved Ask turn on Claude Code. It is not part of the
general catalog: only the turn's own scoped credential lists or calls it, and every other
credential is refused. Args follow Claude Code's contract: `tool_name`, `input` and
`tool_use_id`. It waits for the conversation owner's decision and returns
`{"behavior":"allow","updatedInput":{...}}` or `{"behavior":"deny","message":"..."}`. A request
with no decision before `ask.toolApprovalTimeoutSeconds` is denied. See
[Approving native tool calls](ASK_CONVERSATIONS.md#approving-native-tool-calls).

## Captain Writes

`armada_create_captain` accepts `name` (required), `runtime`, `model`, `apiKey`,
`apiBaseUrl`, `systemInstructions`, `allowedPersonas`, `preferredPersona`, `tier`,
`preferenceRank`, `reasoningEffort`, `defaultPlaybooks` and the `mux*` options.
Mux callers can set `muxMaxTurns` as an optional integer from 1 to 100. Armada
clamps values to that range when it builds the Mux command. A new captain starts
`Idle`, unassigned and not quarantined.

`tier` is the captain's capability tier (`Economy`, `Standard`, or `Premium`); an
empty string classifies it from the model name, and any other value returns a tool
error. `preferenceRank` is an integer from -1000 to 1000 (default 0); among captains
of one tier a higher rank is tried first.

`armada_update_captain` accepts `captainId` (required) and the same fields. A field
left out keeps its stored value; an empty string clears a string field.

`create_persona` and `update_persona` accept `minimumTier` (`Economy`, `Standard`,
`Premium`, or `null`). It is the persona's capability floor, combined with the
mission's request by taking the higher tier. `update_persona` leaves it unchanged
when it is omitted and clears it on an explicit `null`. The retired `specialist`
flag is refused with a tool error that starts with `specialist_retired:`, and
nothing is written.

`create_persona` and `update_persona` also accept `defaultCaptainId` (string), the
captain that missions of this persona prefer. On `update_persona`, `null` or an empty
string clears it and an omitted field leaves it unchanged; on `create_persona`, an
omitted, `null` or empty value sets none. An id that names no captain in the persona's tenant returns a tool
error that starts with `default_captain_not_found:`, and a captain whose
`AllowedPersonas` excludes the persona returns one that starts with
`default_captain_persona_locked:`. A refused call writes nothing.

Both tools apply the same rule as the REST and WebSocket captain writes. Captain
state, assignment, process, recovery, heartbeat, quarantine, identity and
timestamps are server-owned: `id`, `tenantId`, `userId`, `state`,
`currentMissionId`, `currentDockId`, `processId`, `processStartedUtc`, `recoveryAttempts`,
`lastHeartbeatUtc`, `lastProcessAliveUtc`, `quarantineUntilUtc`,
`quarantineReason`, `createdUtc` and `lastUpdateUtc`. A call that sends one with a
value other than its default (create) or its stored value (update) returns a tool
error that starts with `captain_server_owned_field:` and names every refused field.
Nothing is written. Change captain state with `armada_stop_captain`,
`armada_bench_captain` and `armada_unbench_captain`.

`armada_create_captain` with a `name` another captain already has returns the tool
error `A captain with that name already exists.` and creates nothing.

A model check that fails returns the same readable message as before and a `Code`
that names the reason (`EndpointRequired`, `EndpointNotFound`, `EndpointNotInference`,
`EndpointDisabled`, `ProviderDisabled`, `RuntimeUnavailable`, `ModelRejected`,
`TimedOut`, `InvalidRuntimeOptions`, `NamedEndpointRequired`,
`UnsupportedContractVersion` or `EndpointProbeFailed`). REST returns that object as
`Data` on the `400` and keeps `Message`. A provider credit or quota failure still
keeps the captain.

`armada_create_mission` accepts an optional `tier` of `Economy`, `Standard` or
`Premium`. The mission then admits only captains at or above that tier. Omit `tier`
and the mission keeps the default routing. Any other value returns
`Unknown captain tier` and creates nothing. The match ignores case.

`armada_create_mission` and `armada_dispatch` accept optional `isEvaluation`
(default false). When it is true, the mission, or the voyage and every mission
created for it, is evaluation work and stays out of the production summary. A
mission created for an evaluation voyage inherits that voyage's flag. WebSocket
`create_voyage` accepts the same field.

`armada_probe_captain_model` takes `captainId` and sends one tiny prompt through
the captain's existing model validation. It creates no mission. The result is
`captainId`, `success`, `latencyMs` and, on failure, a short `error`. A caller
who is not a global administrator receives an error and the model is not called.
The same captain cannot be probed again for 30 seconds; the tool then returns
`error` `captain_model_probe_rate_limited` and `retryAfterUtc`. An unknown
captain returns `error` `Captain not found`. An API-endpoint captain is admitted
by the captain validation and then probed through the existing model-endpoint
validation.

## Vessel Writes

`armada_add_vessel` and `armada_update_vessel` accept `gitHubTokenOverride`, the
per-vessel GitHub token REST accepts on `POST` and `PUT /api/v1/vessels`. It is
write-only and follows the one rule every vessel write applies (REST, MCP,
WebSocket and remote control):

- Omitted: the stored value is unchanged.
- An explicit empty string: the stored value is cleared.
- Any other value: it replaces the stored value, trimmed.

No tool result, enumeration or event returns the value. Vessel results carry
`HasGitHubTokenOverride` instead.

The transport normally treats an empty string for an optional argument as
omitted. A string property whose schema declares `emptyStringClears: true` is
the exception: its empty value reaches the tool. `gitHubTokenOverride` declares
it, so `""` clears the override rather than leaving it unchanged.

Both tools run as the authenticated caller. `armada_add_vessel` records the
caller's tenant and user as the vessel owner, as a REST create does.

When `armada_add_vessel` gets no `workingDirectory` and `repoUrl` is a local
clone, the clone becomes the vessel's `WorkingDirectory`. A local clone is a
`file://` URL or a rooted path to an existing directory that holds a `.git`
entry, or a bare repository directory with `HEAD` and `hooks`. `LocalPath` is
not set, because vessel removal deletes that directory. A remote URL, a
relative path and a directory that is not a repository leave
`WorkingDirectory` empty.
`armada_update_vessel` returns `Vessel not found` for a vessel the caller may not
change and writes nothing to it, the token override included.

`localPath`, `workingDirectory` and a `repoUrl` that is a local path, a `file:`
URL or a `<transport>::<address>` helper are server paths. Only a global
administrator may set them; for any other caller both tools return an error and
write nothing. A vessel name must be one path segment: no `/` or `\`, no `..`,
no `:`, no control characters, and no leading `.`.

## Configuration Record Writes

Pipelines, personas, prompt templates, playbooks, workflow profiles and
objectives each have one service that owns their create, update and delete:
validation, defaults, the field allow-list, the caller-scoped lookup and the
edit check. The MCP tools, the REST routes and the WebSocket commands only map
its result, so one payload is stored, or refused, the same way on all three. A
refusal is a tool error with `Error` (the message REST returns) and `Code`
(`invalid`, `not_found`, `forbidden`, `conflict`, or a specific code such as
`specialist_retired`). A record the caller may not read returns `not_found`; one
it may read but not change returns `forbidden`.

### Pipelines

`create_pipeline` requires `name` and a non-empty `stages` list, and accepts
`description`, `active` and `ownershipScope`. A name already used in the
caller's tenant returns `conflict`. Each stage requires `personaName` and
accepts `order`, `requiresReview`, `reviewDenyAction`
(`RetryStage` or `FailPipeline`), `description` and `preferredModel`. Give every
stage an `order` (stages that share one run as parallel siblings) or none, and
list position numbers the stages 1..n; a list that orders only some stages is
refused.

`update_pipeline` changes only the supplied fields. A non-empty `stages` list
replaces the stages; an empty list is refused. In a replacement list, a stage
field left out keeps the value of the existing stage for the same persona, so an
update that does not name `requiresReview` keeps the review gate; send `false`
to turn it off, or `null` to clear a stage's `description` or `preferredModel`.
The `update_pipeline` and `delete_pipeline` responses carry `RunningVoyageCount`:
the Open and InProgress voyages dispatched with the pipeline, by their recorded
stage lists. Those voyages keep the stages they were dispatched with, and a
rescue of one runs those stages (docs/PIPELINES.md, "Rescue stage list").

### Personas

`create_persona` requires `name` and `promptTemplateName` and accepts
`description`, `minimumTier`, `defaultCaptainId`, `defaultPlaybooks`, `active`
and `ownershipScope`. A name already used in the caller's tenant returns
`conflict`. `update_persona` changes only the supplied fields; `description`
and `defaultCaptainId` declare `emptyStringClears`, and an empty
`defaultPlaybooks` array clears the playbooks. REST and WebSocket accept the
same fields, so `defaultPlaybooks` set through any surface is stored the same
way.

### Workflow Profiles

`create_workflow_profile`, `update_workflow_profile`, `delete_workflow_profile`
and `validate_workflow_profile` call the same service methods as the REST
routes. Update is a complete replacement, including `environmentVariables`. A
validation refusal carries the validation result as `Details`. A profile that
cannot be read (for example a blank `name`) returns code `invalid` instead of a
protocol error. `validate_workflow_profile` resolves the tenant exactly as
create does, so its answer matches the create. Validation refuses a
`failedTestLinePattern` that is not a valid .NET regular expression or has no
named group `name`, a `gateInputPaths` entry that is rooted or holds a `..`
segment, and a `gateCommandTimeoutSeconds` outside 30 to 3600.
`abbreviatedUnitTestCommand` is stored and returned with the profile. Checks
and the definition-of-done gate keep running `unitTestCommand`.

`armada_suggest_vessel_toolchain` reads marker files in a vessel working
directory and returns suggested build, test and lint commands. It does not
create or change a workflow profile or a vessel command. The same read is
`GET /api/v1/vessels/{id}/toolchain-suggestion`.

### Playbooks

`create_playbook` and `update_playbook` call the same service methods as
`POST` and `PUT /api/v1/playbooks`. `update_playbook` changes only the supplied
fields, and `description` declares `emptyStringClears`. A file name that does
not end in `.md` or missing content returns code `invalid` instead of a protocol
error; a duplicate file name returns `conflict`.

### Prompt Templates

`create_prompt_template` requires `name`, `category` and `content`, trims the
name and category, and accepts `description`, `active` and `ownershipScope`.
`update_prompt_template` changes only the supplied fields of an existing
template (`content`, `description`, `category`, `active`); `description`
declares `emptyStringClears`. It never creates a template: a missing name
returns code `not_found`. REST `PUT /api/v1/prompt-templates/{name}` and the
WebSocket `update_prompt_template` command use the same service, find the
template through the caller scope, and require edit rights.

### Objectives And Backlog Items

`create_objective`, `create_backlog_item`, `update_objective` and
`update_backlog_item` share one input schema and call the same service methods
as `POST` and `PUT /api/v1/objectives` (and `/api/v1/backlog`). Setting status to
`Completed` stamps `CompletedUtc` to that transition; leaving `Completed` clears
it, so a later re-completion records the new time. The update tools
change only supplied top-level fields, including `autoDispatchEnabled`. A
top-level field the schema does not list is refused and nothing is written: the
tools return code `objective_unknown_field` with outcome `Invalid` and the
names in `UnknownFields`, and REST returns 400 with a message that names each
field. Preflight answers are written through `preparation.preflight.questions`. If an
update supplies `preparation`, that nested object is a complete replacement.
Send every nested value that must remain: `requiredForDispatch`,
`requiredClaimKinds`, `requiredSiblingInputs`, `executionRequirements`,
`source`, `target`, `claims`, `preflight`, `stageSkip`, and `gateInputSources`. Omitted preparation
members reset to their model defaults or `null`. The MCP input schema exposes
each member, including all preflight-answer and stored stage-skip fields.
A replacement that moves the `source` or `target` anchor to another commit of
the same vessel and ref marks a dependent claim `NeedsRecheck` only when the
files changed between the two commits include a path the claim cites in its
text or evidence; other claims keep their state. A claim that cites no path, an
unreadable or oversized change, and a `startFromRef` or `vesselIds` change mark
every dependent claim `NeedsRecheck`. The invalidation reason names the changed
cited paths.
Each `requiredSiblingInputs` entry may set `pinnedCommit` and
`provisionPinnedCommit`. `provisionPinnedCommit: true` requires `pinnedCommit`
and makes every dock of the dispatch check out that commit in its own sibling
checkout; landing waits until the commit is on the sibling vessel's default
branch unless the entry also sets `landBeforeSibling: true`, which requires
`provisionPinnedCommit` (`docs/PERSONAS.md`).
`confirmStageSkip: true` explicitly confirms the skip supplied in preparation,
or the existing skip when preparation is omitted. Armada records the authenticated
operator, server time, and a server-issued proof; inbound `confirmedBy`,
`confirmedUtc`, and `operatorConfirmationId` values do not authorize a skip.
Without the flag, unchanged preparation preserves its proof and changed
preparation leaves the skip unconfirmed; the returned skip then carries
`confirmationNotice` and `droppedConfirmation`, and writing back the confirmed
stages and reason restores the confirmation. Legacy skips remain stored but require
explicit reconfirmation. The clearable text fields (`description`, `category`,
`owner`, `targetVersion`, `parentObjectiveId`, `refinementSummary`,
`suggestedPipelineId`,
`startFromRef`) declare `emptyStringClears`, so an explicit `""` clears the
stored value as it does on REST. A refusal carries `Outcome`: `NotFound` for an
objective the caller cannot read, `Invalid` for a missing title, an unknown enum
value, or a linked record outside the caller's scope. `delete_objective` and
`delete_backlog_item` return the same refusal for an unknown id instead of a
protocol error.

`delete_backlog_refinement_session` deletes one refinement session and its
transcript and removes it from the backlog item's links, as
`DELETE /api/v1/objective-refinement-sessions/{id}` does. An active session is
stopped first. A session outside the caller's scope returns `not_found`.

### Incidents

`armada_close_incident` requires `rootCause`: the cause the operator
determined. It must be non-empty and, after trimming, must differ from the
incident's `OpenedReason`, the text the incident was opened with. For an
incident a system path opened, that text is an automatic reading, such as a
mission failure reason. `armada_update_incident` with `status` `Closed`, or
with a changed root cause on a closed incident, applies the same rule, and so
does `armada_create_incident` for an incident created `Closed`. A refusal
returns `Error` and `Code`: `incident_root_cause_required` or
`incident_root_cause_unchanged`. Nothing changes. REST `PUT` and `POST
/api/v1/incidents` use the same rule and codes (`REST_API.md`, "Incidents").

An accepted cause is stored with its author in `RootCauseWrittenBy` and the
time in `RootCauseWrittenUtc`. `OpenedReason` keeps the automatic text. A null
`RootCauseWrittenBy` means that the cause is automatic. A close by the incident
lifecycle or by autonomous recovery sets `ClosedAutomatically` and never counts
as person-written. Such a close leaves `RootCause` null when it still holds the
`OpenedReason` text.

## Client Names

MCP clients can add a transport prefix to tool names in their own UI or prompt
surface. For example, a client can expose Armada's `armada_status` as a longer
name that contains the configured server name. The JSON-RPC `tools/call`
request still uses the advertised tool name.

## Operator Guidance

Use [armada-ops.md](armada-ops.md) for:

- the standard objective-to-closeout workflow;
- the complete tool catalog;
- risk labels for read, write, execute, interrupt, and destructive tools;
- dispatch, monitoring, Check, landing, delivery, recovery, and incident
  procedures.

Use [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
procedures.

## Account usage and persona routing

See [Smart Routing](USAGE_ROUTING.md) for the opt-in policy (usage filter,
persona model lists, the `capacity_escalation` decision, route restrictions),
collectors, credential references, and Dashboard controls. The settings REST API
exposes `providerUsage`; `POST /api/v1/settings/usage-preview` previews the
Legacy Routing order, a verdict per captain naming the layer that decided it
(eligibility, routes, or usage), model groups, capacity reading, and chosen
captain for a saved or draft policy with settings write permission. No new MCP tool is
required. Policy updates use `PUT /api/v1/settings` and hot-reload.

### Local checkout import

`armada_discover_vessel_checkouts` lists direct child checkouts under configured
`vesselImport.allowedRoots`. `armada_import_vessel_checkout` accepts
`checkoutPath`, `name`, and optional `fleetId`. Both require current unscoped
global administrator authority. Mission and saved Ask credentials cannot use
these tools. Import registers an external working tree and leaves its Git
references unchanged. A missing import service or an unconfigured import
request returns the named `Unavailable` error. See
[Local checkout import](VESSEL_CHECKOUT_IMPORT.md) for path and ownership rules.
