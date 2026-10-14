---
type: "Reference"
title: "CLI (`armada`) command surface: 58 commands in groups"
description: "Top-level go, status, watch, log, diff, doctor, tui, inbox, health, reset; groups mission, voyage, action, playbook, backlog, vessel, captain, fleet, server, config, mcp; common options --json --page --page-size --verbose; frozen names/options/exit codes for 1.0."
timestamp: "2026-10-05T05:50:22.3401770Z"
created: "2026-10-05T05:50:21.5717710Z"
slug: "api-cli-command-groups"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["arch-helm-cli-client", "api-frozen-surface-1-0", "helm-cli-server-commands"]
version: "1"
salience: "0.5"
---
Registered in src/Armada.Helm/Program.cs; frozen in docs/API_SURFACE_1.0.md "CLI" (58 commands). Help: `armada`, `armada help`, `armada <cmd> --help` (-h, /?, -?). Names and IDs are accepted interchangeably.
- Common: go <prompt> (--task/-t repeatable = one mission each, --vessel/-v, --repo/-r, --log/-l, --push/--no-push, --pr/--no-pr, --merge/--no-merge, priority), status [--all/-a], watch [--captain/-c, --interval/-i], log <captain|mission> [--follow/-f, --lines/-n], diff [mission], doctor, inbox [--critical], health [--status/-s, --fleet/-f, --evaluate/-e], tui [--server, --profile, --route], reset [--force/-f].
- mission list|create|show|cancel|restart|retry; voyage list|create (--mission/-m, --playbook/-p, --vessel/-v)|show|cancel|retry; action list [--runs, --include-inactive]|run <action> (--fleet, --vessel, --concurrency)|status <run>|cancel <run>; playbook list|add (--from-file, --content)|show|remove; backlog list|show|create|update|delete|reorder (--rank) with many metadata options.
- vessel list [--fleet]|add <name> <repoUrl> [--branch, --fleet]|import [paths] (--root, --depth, --dry-run, --apply, --yes, --fleet, --categorize, --captain, --prompt-file)|remove; captain list|add <name> (--runtime/-r claude|codex|gemini|cursor|mux|custom, --model/-m, --mux-*)|update|stop|remove|stop-all; fleet list|add (--description)|remove.
- server start|status|stop|restart; config show|set <key> <value>|init; mcp install|remove [--dry-run, --yes]|stdio.
Not present: `armada ask` (removed, D3); OpenCode captains cannot be added via `captain add`. Exit status 0 success / non-zero failure and --json field names are part of the compatibility promise. Architecture: [[arch-helm-cli-client]].
