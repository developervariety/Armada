# Ask MCP availability

Ask launches a temporary runtime for each chat turn. The launch receives a
runtime-specific isolated MCP plan. Claude, Gemini, Cursor, and Mux receive a
scoped configuration file. Codex receives its inline MCP override. OpenCode
receives an MCP-only `OPENCODE_CONFIG_CONTENT` overlay; provider settings and
existing MCP entries remain intact, and no filesystem permission is granted by
this overlay.

`GET /api/v1/captains/{id}/tools` reports two states. For an idle captain it
runs an endpoint preflight against the same Armada URL used by the next Ask
launch and sets `mcpConnectionPlanned=true`. This proves endpoint reachability
and tool discovery only; it does not claim that a captain process is running.
A running mission captain is inspected through its active launch configuration.
Custom and unsupported runtimes remain unverified.

API-endpoint captains have no MCP client. For both contexts the tools endpoint
returns the runtime's own workspace tool registry with
`availabilitySource=api-endpoint-workspace-tools`, `mcpConnectionPlanned=false`
and zero Armada tools; no shell or administrative tool is listed. Their tool
calls reach chat as tool cards, never as answer text, and planning sessions
report these captains as unsupported.

The dashboard shows preflight failures and zero-tool results with the returned
summary. It shows manual connection instructions only for a confirmed
nonplanned runtime configuration failure. Existing captain-chat caller scope remains enforced. The local MCP endpoint
still has no per-captain authorization boundary.

The tools endpoint accepts `context=ask` for this preflight. The default
context remains the captain tools page and inspects the active captain launch.
Unknown context values are rejected.
