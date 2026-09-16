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

The built-in catalog currently has 175 tools and fits in the first 500-tool
page. Pagination remains active so extension catalogs can grow without an
unbounded response.

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
until the job reaches a terminal state.

`armada_dispatch` persists the voyage and mission rows before background
assignment starts. A successful dispatch response is not evidence that a
captain has started work.

For objective work, call `preview_objective_dispatch` before `armada_dispatch`.
It performs the same read-only preflight that operator and autonomous objective
dispatch enforce. It reports all structural blockers, a complete typed
dependency graph, and bounded diagnostic chains. A busy compatible captain remains valid configured coverage; its idle
count is capacity information only.

An objective must also pass the dispatch preflight. The preflight is an answer
for each numbered question in the operator dispatch-preflight battery, recorded
in the objective's `preparation.preflight.questions` array through
`update_objective`. Each entry carries a question `number`, an `answer` of
`Yes`, `No`, or `Unanswered`, an operator `note`, an `answeredUtc`, and an
`answeredBy`. Dispatch is refused while any question is unanswered, a question
that must be yes is answered no, or the open-owner-question question is answered
yes; the preview reports this as a blocking `objective_preflight_incomplete`
finding listing the offending question numbers, and reports the deterministic
questions as facts to check the recorded answers against the repository. An
`armada_dispatch` call may set `forcePreflight: true` to override an incomplete
preflight. It overrides only the preflight; any other blocking issue still
refuses the dispatch, and the override is recorded as an
`objective.preflight_overridden` event naming the operator.

After the deterministic preflight the preview also consults the D5 `preflight`
typed decision when it is enabled (`typedDecisions`, ships `Gate`). It reads the
title, description, acceptance criteria, non-goals, refinement summary, Kind,
vessel name, pipeline stages and the deterministic facts, and asks the text-half
battery questions the code cannot settle (Q1 premise-versus-facts, Q4–Q9, Q12,
and a Q13 owner-question choice). A question the model answers at or above the
threshold adds a blocking `objective_preflight_model_flag` finding to the
preview, which the autonomous scheduler skips dispatch on exactly as it does for
any other Error finding; a Q13 owner ruling also posts an owner-addressed board
note. The model only adds findings — it never dispatches, lands, or removes a
deterministic finding — and when the decision is `Off`, unavailable, or below the
threshold the preview is exactly the deterministic result.

## Errors

Armada uses two error levels:

1. A JSON-RPC or MCP protocol error for an invalid request, unknown tool,
   invalid cursor, or unhandled call failure.
2. A structured Armada error result for a valid tool call that cannot perform
   the requested operation.

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
`create_pipeline`, `create_playbook`, `create_prompt_template`, a new name
written by `update_prompt_template`, `create_workflow_profile` and the
service-backed creates (objectives, backlog items, incidents, releases,
deployments, environments, runbooks and memories). `create_playbook` checks
file-name uniqueness inside the caller's tenant. `create_workflow_profile`
keeps a tenant named in the record only for a global administrator. A progress
signal a tool writes about an existing mission, such as the one from
`armada_restart_mission`, belongs to that mission's owner.

Dispatch creates follow the record they act on, the same way on REST and MCP.
`armada_dispatch`, `armada_decompose_plan` and `dispatch_backlog_planning_session`
create voyages and missions owned by the target vessel's owner. `run_check` and
`retry_check_run` create a check run in the vessel's tenant for the calling
user. `start_runbook_execution` creates an execution in the runbook's tenant for
the calling user.

What a caller may use:

| Caller | Tools listed and callable |
| --- | --- |
| Global administrator (admiral API key, or a global-admin user credential) | The whole catalog |
| Any other authenticated user, including a tenant administrator | Only caller-scoped tools: `get_persona`, `get_pipeline`, `get_prompt_template`, `list_prompt_templates`, `create_memory`, `get_memory`, `search_memory`, `update_memory`, `delete_memory`, `armada_typed_decision`, `armada_check_premise`, `armada_memory_triage`, and while Harbor is enabled `armada_harbor_jobs`, `armada_harbor_job`, `armada_harbor_job_stop` |

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
override, so it holds in whichever `CODEX_HOME` an account selects. Scoped and
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

The captain tool inventory probes a Mux captain's configured HTTP servers with
the same credential its `auth` object declares, and probes a running mission
captain's Armada endpoint with the mission owner's own scoped token.

Because the token is caller-scoped, a mission reaches only its owner's records
and the tools that owner's tenant and user may use - never an operator-only or
cross-tenant tool - even though captains use the operator catalog. The admiral
also holds a random launch credential (recognised at the endpoint for its own
internal use), but no mission or chat captain is ever launched with it. Captain
prompts must still keep dispatch, administration, deployment, restore, purge and
server-control actions outside mission scope.

**Local stdio.** `armada mcp stdio` runs the tools in-process with the local
settings file and database credentials. It sets an explicit local operator
identity; it does not fall back to a default context.

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
never stored. Every tool acts as an administrator of the default tenant and
reaches no other tenant.

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

Three mission-scoped tools let a captain consult the typed-decision system
(TypeSafe Jev) for a structured second reading on a judgement it is about to
make. They are caller-scoped, next to the memory tools. Authority does not
travel with them: every call redacts its state before egress, is bounded by the
per-mission call budget in `typedDecisions.captainTool.*`, writes exactly one
`typed_decision.captain` event carrying only a `state_sha256` and byte count
(never the state), and has **no side effect on any Armada record** - it
dispatches nothing, lands nothing, edits no objective, and writes no memory. A
call returns typed answers, or an `available: false` result with an
`unavailableReason` the captain treats as "decide it yourself".

The system ships disabled (`typedDecisions.captainTool.enabled` is `false`), so
every call returns `unavailableReason: disabled` until an operator enables it.
The domain is authorized heavy-duty vehicle diagnostics; seed-key and
SecurityAccess content is ordinary engineering and is passed like any other
state.

### armada_typed_decision

Answer typed questions about a piece of state. Args: `state` (required, a string
or an object whose string fields are redacted), `questions` (required, an object
keyed by question id where each value is `{ type: choice|score|noul,
instructions, criteria|levels, trueMeaning?, falseMeaning? }`), and optional
`missionId` for budget scope and event attribution. `choice.criteria` is a
name-to-meaning map; `score` takes an ordered `criteria`/`levels` array; `noul`
takes optional `trueMeaning`/`falseMeaning`. Returns `{ available, answers,
callsUsed, maxCallsPerMission }` or an unavailable result whose reason is one of
`disabled`, `budget_exhausted`, `invalid`, or a client reason (`timeout`,
`http_401`, `http_429`, `parse`, `exception`, ...).

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

## Captain Writes

`armada_create_captain` accepts `name` (required), `runtime`, `model`, `apiKey`,
`apiBaseUrl`, `systemInstructions`, `allowedPersonas`, `preferredPersona`,
`reasoningEffort`, `defaultPlaybooks` and the `mux*` options. A new captain starts
`Idle`, unassigned and not quarantined.

`armada_update_captain` accepts `captainId` (required) and the same fields. A field
left out keeps its stored value; an empty string clears a string field.

Both tools apply the same rule as the REST and WebSocket captain writes. Captain
state, assignment, process, recovery, heartbeat, quarantine, identity and
timestamps are server-owned: `id`, `tenantId`, `userId`, `state`,
`currentMissionId`, `currentDockId`, `processId`, `recoveryAttempts`,
`lastHeartbeatUtc`, `lastProcessAliveUtc`, `quarantineUntilUtc`,
`quarantineReason`, `createdUtc` and `lastUpdateUtc`. A call that sends one with a
value other than its default (create) or its stored value (update) returns a tool
error that starts with `captain_server_owned_field:` and names every refused field.
Nothing is written. Change captain state with `armada_stop_captain`,
`armada_bench_captain` and `armada_unbench_captain`.

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

See [Usage-aware routing](USAGE_ROUTING.md) for the opt-in policy, collectors,
credential references, reserve behavior, and Dashboard controls. The settings
REST API exposes `providerUsage`; `POST /api/v1/settings/usage-preview` previews
an optional draft policy with settings write permission. No new MCP tool is
required. Policy updates use `PUT /api/v1/settings` and hot-reload.
