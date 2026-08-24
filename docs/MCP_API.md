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

For parameterless discovery, clients can send `tools/list` with an empty
`params` object or without a `params` member. Armada accepts both protocol
forms.

Do not start `armada mcp stdio` inside a host that already runs the Admiral.
That command creates a second service graph. It does not control the running
Admiral.

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
Heartbeat or read with that key between monitor-loop iterations. Both responses
can contain full `UnreadWakes` payloads from addressed notes; process the work
first and acknowledge each signal with `armada_mark_signal_read`.

Set `remoteTrigger.agentWake.participantKey` for a stable addressed process
owner that survives an Admiral restart. `armada_register_agentwake_session`
registers one in-memory process target with a concrete `runtime` (`Claude`,
`Codex`, or `OpenCode`) and an optional `participantKey`, session ID, command,
working directory, and client name. A registration key overrides the configured
key until restart. The settings file controls delivery:
`SpawnProcess`, `McpNotification`, or `Both`.

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

## Errors

Armada uses two error levels:

1. A JSON-RPC or MCP protocol error for an invalid request, unknown tool,
   invalid cursor, or unhandled call failure.
2. A structured Armada error result for a valid tool call that cannot perform
   the requested operation.

When a structured result includes an action hint, follow it before you retry.
Do not repeat a dispatch call until you have checked whether it created a
voyage.

Common protocol errors:

| Condition | Result |
| --- | --- |
| Unknown tool name | Invalid parameters |
| Cursor outside the catalog | Invalid parameters |
| Missing required JSON-RPC fields | Invalid request |
| Malformed tool arguments | Invalid parameters or structured validation error |

## Authentication And Scope

The MCP surface does not currently provide per-request user authentication.
It operates with the configured default administrative tenant context. Bind
the service to a trusted interface and use a protected transport. Do not expose
the MCP port to an untrusted network.

Supported captains receive a local MCP client configuration, but the URL is not
a credential and the endpoint has no per-captain authorization. Keep the port
on a trusted interface. Captain prompts must keep dispatch, administration,
deployment, restore, purge, and server-control actions outside mission scope.
The operator normally owns those actions.

## Catalog Availability

Some families register only when their backing service is available. Examples
include merge processing, code indexing, Checks, delivery records, objective
scheduling, backups, and unlanded-branch reporting.

Use live discovery to decide what the connected Admiral supports. Do not infer
availability from repository source or from this document.

## Client Names

MCP clients can add a transport prefix to tool names in their own UI or prompt
surface. For example, a client can expose Armada's `armada_status` as a longer
name that contains the configured server name. The JSON-RPC `tools/call`
request still uses the advertised tool name.

## Operator Guidance

Use [armada-ops.md](armada-ops.md) for:

- the standard objective-to-closeout workflow;
- the complete 175-tool catalog;
- risk labels for read, write, execute, interrupt, and destructive tools;
- dispatch, monitoring, Check, landing, delivery, recovery, and incident
  procedures.

Use [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
procedures.
