---
type: "Reference"
title: "Helm CLI (`armada`): server start/stop and config"
description: "armada server start|status|stop|restart controls the Admiral; the CLI talks to http://127.0.0.1:<AdmiralPort> with the local apiKey (X-Api-Key); armada config show|set|init; armada doctor."
timestamp: "2026-10-05T05:30:12.8257600Z"
created: "2026-10-05T05:30:11.9427490Z"
slug: "helm-cli-server-commands"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["local-api-key-system-identity", "source-install-scripts", "tui-launch-and-options", "open-o18-stdio-mcp-no-auth"]
version: "1"
salience: "0.5"
---
The armada CLI (src/Armada.Helm, Spectre.Console) is a thin HTTP client. Groups: common (go, status, watch, ask, inbox, log, diff, doctor), mission, voyage, backlog, playbook, vessel, captain, fleet, server (start, status, stop, restart), config (show, set, init), mcp (install, remove, stdio), tui, danger zone (reset). `armada <cmd> --help` (or -h, /?, -?) shows per-command help.

It reads admiralPort and apiKey from ~/.armada/settings.json, calls http://127.0.0.1:<admiralPort>, and sends the key as X-Api-Key (the synthetic global admin system@armada). Commands that need a server can auto-start an embedded one; `armada server start` works from the NuGet tool install (fixed in W5.5). Point at another port or credential with `armada config set AdmiralPort 7890` / `armada config set ApiKey <key>`. `armada mcp stdio` runs an unauthenticated stdio MCP server that opens the database directly (security O-18, accepted). Server stop/restart routes are AdminOnly; the CLI sends the local API key.
