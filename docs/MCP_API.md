---
topic: "MCP API"
summary: "Transport, discovery, authentication scope, and error behavior for the MCP endpoint."
read_when: "Connecting an MCP client, or debugging discovery, credentials, or a tool error."
applies_to: orchestrator
tier: leaf
---
# Armada MCP API

**Version:** 0.9.0
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
notification and never does. A client that must be reachable identifies itself
per request instead:

| Header | Value | Effect |
| --- | --- | --- |
| `X-Armada-Participant` | The caller's coordination `participantKey` | Pending directed wakes are appended to every tool result |

The key must be 1-128 characters of `A-Z a-z 0-9 . _ : -`. Armada drops a key
of any other shape rather than echoing it into a tool result.

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
`"auth": { "scheme": "api_key", "key": "${ARMADA_API_KEY}", "headerName": "X-Api-Key" }`.

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

Catalog size depends on the running build, enabled services, and caller role.
Follow every `nextCursor`; do not infer completeness from a fixed tool count.

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

## Coordination And AgentWake

Every concurrent operator uses one stable coordination `participantKey`.

Send that key as the `X-Armada-Participant` header on every request. Armada
then appends an `[ARMADA WAKE]` block to the result of ANY tool the session
calls, so a status poll inside a monitor loop delivers directed messages.
`armada_coordination_read` and `armada_coordination_heartbeat` are excluded
from the banner because they already return the same wakes as a full
`UnreadWakes` payload. Process the work first, then acknowledge each signal
with `armada_mark_signal_read`; the banner repeats until you do.

A session that sends no header receives no wake banner, and must heartbeat or
read the board with its `participantKey` between monitor-loop iterations to see
addressed work at all.

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
is not held (`not_held`). `clear` writes `mission.hold_cleared` and `fail`
writes `mission.hold_failed`, each naming the operator and the reason. The model
never resolves a hold and nothing resolves one automatically.

When the `inbox_triage` typed decision is enabled (`Gate`), the `inbox`
tool's items and the `armada_coordination_read` notes each carry an extra
`attention` field (`informational`, `today`, `this_hour`, or
`blocking_live_voyage`) and are ordered by it, and each board note also carries a
`noteKind` (`handoff`, `status`, `question`, `stop_sign`, or `hold_notice`).
Triage only annotates and re-orders — it never hides or drops an item. With the
decision `Off` (it ships `Gate`) `attention` and `noteKind` are absent and the
deterministic severity order stands.

Set `remoteTrigger.agentWake.participantKey` for a stable addressed process
owner that survives an Admiral restart. `armada_register_agentwake_session`
registers one in-memory process target with a concrete `runtime` (`Claude`,
`Codex`, or `OpenCode`) and an optional `participantKey`, session ID, command,
working directory, and client name. A registration key overrides the configured
key until restart. The settings file controls delivery:
`SpawnProcess`, `StoredWake`, or `Both`. `StoredWake` stores the wake row and
sends no MCP notification, because this transport cannot carry one. Any other
delivery-mode value is rejected when settings load.

An addressed board note always retains a Wake signal. When its key matches the
effective participant key and delivery is `SpawnProcess` or `Both`, Armada also
starts the effective runtime. `armada_agentwake_status` shows both the
configured and transient state. OpenCode always starts a fresh session; it does not use
resume state. Put the complete task in the note and reconstruct context from
the board and durable memory. Do not give one participant key to both a
resident helper process and an AgentWake process owner.

## Long Operations

Dispatch, code-index refresh, and merge processing can return an accepted job
instead of blocking the request. Save the job ID and call `armada_job_status`
until the job reaches a terminal state: `Succeeded`, `Failed` or `Lost`.

`armada_dispatch` validates the request, checks the dispatch hold and runs the
objective preflight before it accepts anything. A refusal at that point returns
its named code and creates nothing. Only then is the job accepted, and it is
written to the job journal in the data directory before the `Accepted` response
is returned. The voyage and mission rows are created when the job runs, not
before the response: an `Accepted` job is not yet a voyage. Read the voyage ID
from the job's result. A dispatch refused after acceptance (the hold engaged in
between, or another dispatch won the objective) ends `Failed` with the refusal
body as its `FailureMessage`. `armada_job_status` reports a `Failed` or `Lost`
job's reason in `FailureMessage`, and reading it is a successful call. A
successful job is not evidence that a captain has started work.

While the dispatch hold is engaged, every dispatch entry point refuses at
submission with `dispatch_hold_active`, the holder (`SetBy`), `SetByUtc` and
the reason, and accepts no job: `armada_dispatch` (plain and alias-ordered),
REST `POST /api/v1/voyages`, WebSocket `create_voyage`, planning-session
dispatch, and mission, architect and remote-control dispatch. The objective
scheduler records `dispatch_hold`, autonomous rescue defers, and automatic
Checks wait until the hold clears.

A job survives the process that accepted it. When the admiral restarts before a
job finishes, the next start records it as `Lost` with the reason
`job_lost_on_restart`, and `armada_job_status` returns that record, never
`job_not_found`. A lost job is never resumed: check whether its effect exists,
then submit it again. Every job that ends `Failed` or `Lost` writes a
`job.failed` or `job.lost` event on its objective (on the job when it names no
objective). Finished records are kept for 14 days. `armada_dispatch_hold`
`status` and `engage` list `UnfinishedJobs`, the jobs a restart would lose;
wait for that list to empty before restarting the admiral.

A global administrator can also list every job, newest first, through REST
`GET /api/v1/jobs` and the dashboard Jobs page, and read one through
`GET /api/v1/jobs/{id}`. Jobs have no cancel operation.

A job that stays `Accepted` or `Running` for 30 minutes is reaped as `Failed`
with the reason `reaped as stale`, and its operation is cancelled. A terminal
status is final: an operation that finishes after its job was reaped does not
change the status in memory or in the journal.

For objective work, call `preview_objective_dispatch` before `armada_dispatch`.
It performs the same read-only preflight that operator and autonomous objective
dispatch enforce. It reports all structural blockers, a complete typed
dependency graph, and bounded diagnostic chains. A busy compatible captain remains valid configured coverage; its idle
count is capacity information only.

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
answers against the repository. An `armada_dispatch` call may set
`forcePreflight: true` to override an incomplete preflight or a
`objective_preflight_model_flag` finding, including question 14. It overrides
only those preflight-class findings; any other blocking issue still refuses the
dispatch, and the override is recorded as an `objective.preflight_overridden`
event naming the operator, the blocking question numbers, and the
model-flagged question numbers. A refusal without the force flag lists both
`IncompleteQuestions` and `ModelFlaggedQuestions`.

`armada_dispatch` accepts `skipStages`, an array of persona names the operator
confirms the voyage does not need (for example `["TestEngineer"]`), and an
optional `skipStagesReason`. The named stages are dropped when the voyage is
materialised and the remaining stages chain across the gap: each kept stage
depends on the last kept stage before it. One `voyage.stage_skipped` event is
recorded per dropped stage; its payload carries `Persona`, `StageOrder`,
`Reason` and `Confirmer` (the calling principal). The same rule applies to REST
`POST /api/v1/voyages` (`SkipStages`), WebSocket `create_voyage` (`skipStages`), alias dispatch,
and the autonomous scheduler. The dispatch is refused, and nothing is created,
when a name is the Judge (`stage_skip_judge_refused`), is blank or is not a stage
of the effective pipeline (`stage_skip_unknown_persona`), or when only the Judge
would remain (`stage_skip_leaves_no_work`). A `stage_optional` preview Warning
is advice only: nothing is skipped unless the operator names it. The autonomous
scheduler reads an operator-confirmed skip from the objective's
`preparation.stageSkip` (`stages`, `reason`, `confirmedBy`, `confirmedUtc`) and
honours it only when `confirmedBy` is set; otherwise it skips the objective as
`stage_skip_unconfirmed`. A refinement summary never writes this field.
`preview_objective_dispatch` lists `effectivePipelineStages` after that same
`PipelineStageSkip` rule, names `skippedPipelineStages`, reports whether the
stored skip is confirmed, and surfaces the named refusal a stored skip would hit
(`stage_skip_unconfirmed`, `stage_skip_judge_refused`,
`stage_skip_unknown_persona`, `stage_skip_leaves_no_work`) without creating a
voyage. Captain coverage in the preview follows the effective stages, so preview
and dispatch agree.

After the deterministic preflight the preview also consults the `preflight`
typed decision when it is enabled (`typedDecisions`, ships `Gate`). It reads the
title, description, acceptance criteria, non-goals, refinement summary, Kind,
vessel name, pipeline stages and the deterministic facts, and asks the text-half
battery questions the code cannot settle (Q1 premise-versus-facts, Q4–Q9, Q12,
and a Q13 owner-question choice). A question the model answers at or above the
threshold adds a blocking `objective_preflight_model_flag` finding to the
preview, which the autonomous scheduler skips dispatch on exactly as it does for
any other Error finding (an operator dispatch may pass it with `forcePreflight`); a Q13 owner ruling also posts an owner-addressed board
note. The model only adds findings — it never dispatches, lands, or removes a
deterministic finding — and when the decision is `Off`, unavailable, or below the
threshold the preview is exactly the deterministic result.

### Dispatch starting commits


`armada_dispatch` accepts `missions[].startFromRef` (branch, tag, or commit).
An omitted value inherits the linked objective's `StartFromRef`; an explicit
mission value takes precedence. This also applies to mission-alias dispatch.
The root mission pins the resolved commit; dependent stages continue their
predecessor's branch. The response preserves the voyage fields and adds
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
result with a JSON-RPC internal error that carries the message.

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

The gates are review approval, Judge authority, captain process release, Check
state, and target ancestry of the mission commit when no active dock will land
it. The same request on another surface gets the same decision.

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

What a caller may use:

| Caller | Tools listed and callable |
| --- | --- |
| Global administrator (admiral API key, or a global-admin user credential) | The whole catalog |
| Tenant administrator | The caller-scoped tools below, plus `create_persona`, `update_persona`, `delete_persona`, `create_pipeline`, `update_pipeline` and `delete_pipeline`, as on REST. Each change finds the record through the caller scope and applies `OwnershipPolicy.CanEdit`, so it changes only the caller's own tenant's records; another tenant's record reads as not found |
| Any other authenticated user | Only caller-scoped tools: `get_persona`, `get_pipeline`, `get_prompt_template`, `list_prompt_templates`, `create_memory`, `get_memory`, `search_memory`, `update_memory`, `delete_memory`, `armada_typed_decision`, `armada_score_items`, `armada_check_premise`, `armada_check_prior_art`, `armada_memory_triage`, `armada_change_quality`, `armada_corpus_prelabel`, `armada_run_custom_decision`, `armada_fetch_context`, `armada_mission_code_search`, and while Harbor is enabled `armada_harbor_jobs`, `armada_harbor_job`, `armada_harbor_job_stop` |

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
session token. The owner of a mission is its tenant and user, and an autonomous
mission with no owner of its own falls back to the objective owner carried on its
voyage. When no owner resolves, the launch presents no credential and the
endpoint refuses it (fail closed).

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
| Mux | `"auth": { "scheme": "bearer_token", "token": "${ARMADA_MCP_TOKEN}" }` |

The captain tool inventory of a running Mux mission captain lists the MCP
servers that mission's launch delivers: it reads the servers file the launch
plan builds, not the captain's config directory, whose servers `mux print`
never loads. With dock MCP delivery disabled the launch passes no
`--mcp-config`, so the inventory lists no MCP server. It probes each delivered
server with the credential its `auth` object declares, which for the Armada
entry is the mission owner's own scoped token. The only Mux CLI call is
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
and the tools that owner's tenant and user may use - never an operator-only or
cross-tenant tool - even though captains use the operator catalog. The admiral
also holds a random launch credential (recognised at the endpoint for its own
internal use), but no mission or chat captain is ever launched with it. Captain
prompts must still keep dispatch, administration, deployment, restore, purge and
server-control actions outside mission scope.

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
coordinators, the coordination board, harbor jobs and the context index) are
not built, so their tools are not registered on stdio or report the service as
unavailable.

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
`forbidden`, or `conflict`. A conflict means the record changed since it was read,
or the key belongs to another record. Read the record again and retry.

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
checkout. The captain verifies each `path:line` itself. The tool edits nothing
and never blocks. It is caller-scoped like the other captain tools, and it is
dormant (returns unavailable) until the `prior_art` decision is enabled.

### armada_mission_code_search

Search the code index of the calling mission's own vessel. Args: `missionId`
(required), `query` (required), optional `limit`, `pathPrefix`, `language`, and
`includeContent`. The tool resolves the vessel from the mission and takes no
vessel or fleet argument, so a captain reaches only its own vessel's index. It
refuses a mission outside the caller's tenant (`mission_not_found`) or one that
has finished (`mission_not_active`), never returns reference-only records, and
clamps `limit` to `codeIndex.captainSearchMaxResults`. Each result carries
`Score`, `Path`, `StartLine`, `EndLine`, `Language`, `Excerpt`, and `Content`
when asked.

The index covers the vessel's default branch, not the dock branch. When the
index is `Missing` or `Error` no search runs and the answer is unavailable with
`index_missing` or `index_error`, so an empty result never stands in for "the
code is absent". A `Stale` or lexical-only index still searches and says so in
`Warnings`. The tool writes no record, is budgeted per mission by
`codeIndex.captainSearchMaxCallsPerMission` (reason `budget` when spent), and is
caller-scoped like the other captain tools. The operator tools
`armada_code_search` and `armada_fleet_code_search` stay outside mission scope.

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
provider records one `typed_decision.gated`, `typed_decision.shadow`, or
`typed_decision.unavailable` event under the decision point `custom:<name>`.
A `MissionDiff` decision also runs by itself when a Worker stage hands off,
on the vessels its `vessels` list names (every vessel when empty). A call by
name through this tool ignores that scope.

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

## Memory Proposals

Two operator tools read and close the memory proposal store. A memory proposal
is a durable lesson the typed-decision system nominated for the owner's external
AI-Memory: the weekly papercut sweep (`memory_candidate`) or the review of a
finished Recorder stage (`memory_review`). Proposals live in the database,
because the AI-Memory folder is read-only to the admiral. Armada never writes
AI-Memory; the owner promotes a proposal by hand. The model never dismisses a
proposal.

Both tools are operator control. They are not caller-scoped, so a mission caller
can neither list nor call them, and each handler also refuses any caller other
than a global administrator with `Reason: global_administrator_required`.

### armada_list_memory_proposals

List proposals, newest first. Args: optional `state` (`Open`, the default,
`Dismissed`, or `All`) and `limit` (default 25, maximum 200). Returns `{ State,
Count, Proposals }`. Each proposal carries `Id` (`mpr_` prefix), `Source`
(`papercut_sweep` or `recorder_seam`), `SourceKey` (a SHA-256 fingerprint of the
subject), `Title` and `Body` (both redacted), `TargetHint` (a plain-text
suggestion: `shared`, `repos/<vessel>`, or `machine-notes`), `Confidence`,
`RelatedRecordIds` (the missions or memory records it came from), `State`, the
dismissal fields, `CreatedUtc`, and `LastUpdateUtc`.

### armada_dismiss_memory_proposal

Dismiss one open proposal. Args: `id`, `reason`, and `operator` (all required).
The proposal is kept with `State: Dismissed`, `DismissedBy`, `DismissedReason`,
and `DismissedUtc`; nothing is deleted and AI-Memory is not touched. Returns
`{ Dismissed: true, Proposal }`, or an `Error` when the id is unknown, a field is
missing, or the proposal is already dismissed. A dismissed subject is not
proposed again.

## Captain Writes

`armada_create_captain` accepts `name` (required), `runtime`, `model`, `apiKey`,
`apiBaseUrl`, `systemInstructions`, `allowedPersonas`, `preferredPersona`, `tier`,
`preferenceRank`, `reasoningEffort`, `defaultPlaybooks` and the `mux*` options. A
new captain starts `Idle`, unassigned and not quarantined.

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
accepts `order`, `isOptional`, `requiresReview`, `reviewDenyAction`
(`RetryStage` or `FailPipeline`), `description` and `preferredModel`. Give every
stage an `order` (stages that share one run as parallel siblings) or none, and
list position numbers the stages 1..n; a list that orders only some stages is
refused.

`update_pipeline` changes only the supplied fields. A non-empty `stages` list
replaces the stages; an empty list is refused. In a replacement list, a stage
field left out keeps the value of the existing stage for the same persona, so an
update that does not name `requiresReview` keeps the review gate; send `false`
to turn it off, or `null` to clear a stage's `description` or `preferredModel`.

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
create does, so its answer matches the create.

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
as `POST` and `PUT /api/v1/objectives` (and `/api/v1/backlog`). The update tools
change only the supplied fields, including `autoDispatchEnabled`. The clearable
text fields (`description`, `category`, `owner`, `targetVersion`,
`parentObjectiveId`, `refinementSummary`, `suggestedPipelineId`,
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
