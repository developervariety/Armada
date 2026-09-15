# Ask MCP availability

Ask launches a temporary runtime for each chat turn. The launch receives a
runtime-specific isolated MCP plan. Claude, Gemini, Cursor, and Mux receive a
scoped configuration file. Codex receives its inline MCP override. OpenCode
receives an MCP-only `OPENCODE_CONFIG_CONTENT` overlay; provider settings and
existing MCP entries remain intact, and no filesystem permission is granted by
this overlay.

A chat turn authenticates to Armada MCP one way: the authenticated caller's own
session token. The plan carries that token in a `ARMADA_MCP_CHAT_TOKEN`
environment variable, and each runtime's configuration references it by variable
name, so the token value never lands in a configuration file. The admiral launch
credential, which the endpoint maps to operator access, is a mission-launch
credential and never enters a chat captain's environment. A chat turn with no
authenticated caller leaves the variable unset, so the runtime presents no
credential and reaches no MCP tool.

`GET /api/v1/captains/{id}/tools` reports two states. For an idle captain it
runs an endpoint preflight against the same Armada URL used by the next Ask
launch and sets `mcpConnectionPlanned=true`. This proves endpoint reachability
and tool discovery only; it does not claim that a captain process is running.
A running mission captain is inspected through its active launch configuration.
Custom and unsupported runtimes remain unverified.

An API-endpoint captain in an Ask chat turn connects to Armada MCP with the
caller's own session token, which the chat issues for an authenticated caller
with a tenant and a user. The runtime sends the token in the `X-Token` header;
it sends no other MCP credential and never uses the admiral launch credential.
The endpoint re-reads the user and tenant on each request and applies the shared
tool access policy, so the turn lists and calls only the tools and records the
caller may already reach. A turn with no authenticated caller, or whose endpoint
refuses the token, runs with the workspace tools only and logs the reason.

A CLI-runtime captain (Claude Code, Codex, Cursor, Gemini, OpenCode, Mux) in an
Ask chat turn reaches Armada MCP the same way: the caller's session token,
carried by the scoped configuration and presented in that runtime's native
credential header (an Authorization bearer for these runtimes). The endpoint
accepts a session token from the bearer header, re-reads the user and tenant on
each request, and applies the shared tool access policy, so a non-admin caller
is offered only caller-scoped tools and is refused an operator tool such as
`armada_stop_server`. A CLI chat turn no longer carries the operator-scoped
launch credential, so a non-admin dashboard user who starts a captain chat can
no longer reach operator-only MCP tools through the captain.

The tools endpoint does not yet reflect this. For both contexts it still
returns the runtime's own workspace tool registry with
`availabilitySource=api-endpoint-workspace-tools`, `mcpConnectionPlanned=false`
and zero Armada tools; no shell or administrative tool is listed. A
caller-scoped Ask preflight for these captains is remaining work. Their tool
calls, workspace and MCP, reach chat as tool cards, never as answer text, and
planning sessions report these captains as unsupported.

The dashboard shows preflight failures and zero-tool results with the returned
summary. It shows manual connection instructions only for a confirmed
nonplanned runtime configuration failure. Existing captain-chat caller scope
remains enforced. A chat captain, CLI or API-endpoint, authenticates as the
caller and is scoped to the caller; only a mission-launch captain still carries
the operator-scoped launch credential.

The tools endpoint accepts `context=ask` for this preflight. The default
context remains the captain tools page and inspects the active captain launch.
Unknown context values are rejected.
