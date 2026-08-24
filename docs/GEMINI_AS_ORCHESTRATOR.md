# Gemini CLI as Orchestrator

Connect the Gemini CLI to Armada's MCP server and use natural language to orchestrate parallel AI agents across your repositories.

## Prerequisites

1. **Armada installed** — `dotnet tool install -g armada`
2. **Gemini CLI installed** — See [Google Gemini CLI docs](https://github.com/google-gemini/gemini-cli) for installation
3. **At least one vessel registered** — a git repository for agents to work in

## Setup

```bash
armada mcp install
```

This now writes the MCP configuration for all supported tools automatically. For Gemini CLI specifically, it writes `~/.gemini/settings.json`. If you prefer to edit manually, use:

```json
{
  "mcpServers": {
    "armada": {
      "httpUrl": "http://localhost:7891/mcp"
    }
  }
}
```

Use `--dry-run` to preview without writing.

## Default Permission Mode

Armada runs Gemini captains with `--sandbox none` by default, giving them full filesystem access without approval prompts. This is configurable via the captain's `SandboxMode` property.

## Sandbox Modes

Gemini CLI supports three sandbox modes:

| Mode | Description |
|------|-------------|
| `none` | No restrictions — full read/write/execute access |
| `permissive` | Some operations require approval |
| `strict` | All file writes and shell commands require approval |

For orchestration, `none` is recommended since the orchestrator only calls Armada MCP tools:

```bash
gemini --sandbox none -p "Check Armada status and dispatch a test voyage"
```

## Verify It Works

Start the Admiral server (`armada server start`), then:

```bash
gemini --sandbox none -p "Check Armada status and tell me what's running."
```

Gemini will call `armada_status` and report active captains, missions, and voyages.

## Giving Gemini Full Instructions

Use [`INSTRUCTIONS_FOR_GEMINI.md`](INSTRUCTIONS_FOR_GEMINI.md) as the Gemini
prompt bootstrap. The canonical workflow and complete MCP catalog are in
[`armada-ops.md`](armada-ops.md). Live tool schemas come from paginated
`tools/list` discovery.

## Quick Start

> "Register this repository as a vessel in the default fleet, then dispatch a voyage to add input validation to all REST API endpoints."

> "Check on voyage vyg_abc123. If any missions failed, look at the events and redispatch with better prompts."

> "Refactor the authentication system. Decompose into parallel missions and dispatch them."

## Concurrent Sessions And Autonomous Cycles

Use one stable coordination participant key. Read and heartbeat before work,
drain full `UnreadWakes` payloads between monitor iterations, and acknowledge
each processed Wake. Addressed notes always retain a signal and can also start
the registered AgentWake runtime in `SpawnProcess` or `Both` mode. OpenCode
wakes are fresh sessions, so the note carries the task and the session rebuilds
state from the board and durable memory.

The objective scheduler is the built-in unattended dispatcher. Optional lead
cycles use [`autonomy/lead-bootstrap-prompt.md`](autonomy/lead-bootstrap-prompt.md);
bounded read-only helpers use `scripts/autonomy/spawn-helper.sh`. Do not assign
one participant key to both a resident helper and AgentWake.

---

## Appendix: Manual Configuration

If you prefer to configure MCP manually instead of using `armada mcp install`, add to `~/.gemini/settings.json`:

**HTTP Transport (recommended)** — requires Admiral server running (`armada server start`):

```json
{
  "mcpServers": {
    "armada": {
      "httpUrl": "http://localhost:7891/mcp"
    }
  }
}
```

**Stdio Transport** — no server required, Armada runs as a subprocess:

```json
{
  "mcpServers": {
    "armada": {
      "type": "stdio",
      "command": "armada",
      "args": ["mcp", "stdio"]
    }
  }
}
```
