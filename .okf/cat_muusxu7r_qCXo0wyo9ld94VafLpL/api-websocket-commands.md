---
type: "Reference"
title: "WebSocket command actions (59), global-admin only"
description: "{\"Route\":\"command\",\"action\":...} supports status/stop_*, fleet/vessel/voyage/mission/captain CRUD and lists, signals, events, docks, merge queue, persona/pipeline/prompt-template edits, logs/diffs, enumerate, backup/restore, stop_server; every command requires a global administrator."
timestamp: "2026-10-05T05:50:12.5864480Z"
created: "2026-10-05T05:50:11.5662930Z"
slug: "api-websocket-commands"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-websocket-connection", "arch-websocket-hub"]
version: "1"
salience: "0.5"
---
Handler src/Armada.Server/WebSocket/WebSocketCommandHandler.cs (rejects any undeclared action); list frozen in docs/API_SURFACE_1.0.md: backup, cancel_merge, cancel_mission, cancel_voyage, create_captain, create_fleet, create_mission, create_persona, create_pipeline, create_vessel, create_voyage, delete_captain, delete_fleet, delete_persona, delete_pipeline, delete_vessel, enqueue_merge, enumerate, get_captain, get_captain_log, get_fleet, get_merge_entry, get_mission, get_mission_diff, get_mission_log, get_persona, get_pipeline, get_prompt_template, get_vessel, get_voyage, list_captains, list_docks, list_events, list_fleets, list_merge_queue, list_missions, list_missions_summary, list_signals, list_vessels, list_voyages, process_merge_queue, purge_mission, purge_voyage, restart_mission, restore, send_signal, status, stop_all, stop_captain, stop_server, transition_mission_status, update_captain, update_fleet, update_mission, update_persona, update_pipeline, update_prompt_template, update_vessel, update_vessel_context.
Fields: id, data (entity body), query (EnumerationQuery for list_*), entityType (enumerate), captainId, status, lines/offset (logs), outputPath (backup), filePath (restore). Authorization: commands read and write by id across tenants, so all of them (reads included) require a global administrator (API key identity or admin user); others get command.error "Forbidden: WebSocket commands require a global administrator. Use the REST API, which is tenant-scoped." REST-only features (workspace, planning sessions, request history, GitHub helpers, runtime helpers, OpenAPI) have no commands. Prefer REST/MCP for normal clients; the WS command surface is legacy plus real-time monitoring.
