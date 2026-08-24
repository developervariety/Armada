# Codex as Orchestrator

Connect OpenAI Codex CLI to Armada's MCP server and use natural language to orchestrate parallel AI agents across your repositories.

## Prerequisites

1. **Armada installed** — `dotnet tool install -g armada`
2. **Codex CLI installed** — `npm install -g @openai/codex`
3. **At least one vessel registered** — a git repository for agents to work in

## Setup

```bash
armada mcp install
```

This writes MCP configuration for all supported tools automatically. For Codex specifically, Armada uses the native `codex mcp add` flow and the current `~/.codex/config.toml` format. If you prefer to edit manually, add an Armada MCP server entry equivalent to:

```toml
[mcp_servers.armada]
url = "http://localhost:7891/mcp"
```

Use `--dry-run` to preview without writing.

## Default Permission Mode

Armada runs Codex captains with `--approval-mode full-auto` by default, so all commands are auto-approved without user prompts. This is configurable via the captain's `ApprovalMode` property.

## Verify It Works

Start the Admiral server (`armada server start`), then:

```bash
codex --approval-mode full-auto "Check Armada status and tell me what's running."
```

Codex will call `armada_status` and report active captains, missions, and voyages.

## Giving Codex Full Instructions

Use [`INSTRUCTIONS_FOR_CODEX.md`](INSTRUCTIONS_FOR_CODEX.md) as the Codex
prompt bootstrap. The canonical workflow and complete MCP catalog are in
[`armada-ops.md`](armada-ops.md). Live tool schemas come from paginated
`tools/list` discovery.

## Quick Start

> "Register this repository as a vessel in the default fleet, then dispatch a voyage to add input validation to all REST API endpoints."

> "Check on voyage vyg_abc123. If any missions failed, look at the events and redispatch with better prompts."

> "Refactor the authentication system. Decompose into parallel missions and dispatch them."

## Current Operator Surfaces

For non-trivial work, prefer this flow:

1. Create or find an objective/backlog item first.
2. Use objective refinement, Planning, Workspace, and context packs to scope the mission set.
3. Dispatch with objective IDs, selected playbooks, workflow profile/check expectations, and explicit file boundaries.
4. Monitor through voyage/mission status, structured check runs, request history, and timeline history.
5. Use review gates for human approval points, then let merge queue/audit/PR fallback handle landing safety.
6. Link releases, deployments, incidents, runbooks, and GitHub evidence back to the objective before closing it.

See [`armada-ops.md`](armada-ops.md) for the current operator playbook.

## Concurrent Sessions And Autonomous Cycles

Use one stable coordination participant key. Read and heartbeat before work,
drain full `UnreadWakes` payloads between monitor iterations, and acknowledge
each processed Wake. Addressed notes always retain a signal and can also start
the effective AgentWake process owner in `SpawnProcess` or `Both` mode. A
persistent settings key survives restarts; a transient registration can
override it. OpenCode
wakes are fresh sessions, so the note carries the task and the session rebuilds
state from the board and durable memory.

The objective scheduler is the built-in unattended dispatcher. Optional lead
cycles use [`autonomy/lead-bootstrap-prompt.md`](autonomy/lead-bootstrap-prompt.md);
bounded read-only helpers use `scripts/autonomy/spawn-helper.sh`; `offer` mode
allows a bounded lead reassignment window before fallback work. Do not assign
one participant key to both a resident helper and AgentWake.

---

## Appendix: Manual Configuration

If you prefer to configure MCP manually instead of using `armada mcp install`, add the equivalent MCP server entry to `~/.codex/config.toml`.

**HTTP Transport (recommended)** — requires Admiral server running (`armada server start`):

```toml
[mcp_servers.armada]
url = "http://localhost:7891/mcp"
```

**Stdio Transport** — no server required, Armada runs as a subprocess:

```toml
[mcp_servers.armada]
command = "armada"
args = ["mcp", "stdio"]
startup_timeout_sec = 120
```

**Remote Admiral over SSH** — keeps one Admiral process authoritative and
bridges Codex stdio requests to its loopback HTTP endpoint:

```toml
[mcp_servers.armada]
command = "node"
args = ["/path/to/Armada/scripts/mcp-ssh-http-bridge.mjs"]
env = { ARMADA_SSH_HOST = "your-ssh-host", ARMADA_SSH_USER = "your-user" }
startup_timeout_sec = 30
tool_timeout_sec = 600
```

The bridge requires `curl` on the SSH host. It remains alive when an individual
request is interrupted by an Admiral restart, so later requests can recover
without launching a second embedded Armada process.
