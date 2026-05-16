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

```json
{
  "mcpServers": {
    "armada": {
      "type": "http",
      "url": "http://localhost:7891/rpc"
    }
  }
}
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

For Codex to effectively orchestrate Armada, paste the contents of [`INSTRUCTIONS_FOR_CODEX.md`](INSTRUCTIONS_FOR_CODEX.md) into your system prompt or project configuration. That document contains the complete tool reference, workflow patterns, and decision-making guidance Codex needs to manage fleets, voyages, missions, and captains.

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

---

## Appendix: Manual Configuration

If you prefer to configure MCP manually instead of using `armada mcp install`, add the equivalent MCP server entry to `~/.codex/config.toml`.

**HTTP Transport (recommended)** — requires Admiral server running (`armada server start`):

```json
{
  "mcpServers": {
    "armada": {
      "type": "http",
      "url": "http://localhost:7891/rpc"
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
