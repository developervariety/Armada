---
type: "Reference"
title: "MCP tool catalog: 146 tools by family"
description: "Tool families registered by Mcp*Tools classes: status/inbox/papercut/token usage, enumerate, fleets, vessels, vessel import, vessel health, voyages (dispatch), missions, captains, signals, events, docks, playbooks, fleet actions, merge queue, harbors (experimental), objectives/backlog, delivery (checks, releases, deployments, runbooks), personas, pipelines, prompt templates, memory, model endpoints, backup."
timestamp: "2026-10-05T05:49:07.4945290Z"
created: "2026-10-05T05:49:06.4271010Z"
slug: "api-mcp-tool-families"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-mcp-transport-and-auth", "api-mcp-enumerate", "api-mcp-error-codes", "api-mcp-ask-gate"]
version: "1"
salience: "0.5"
---
Registration order in McpToolRegistrar.RegisterAll (src/Armada.Server/Mcp/Tools/Mcp*Tools.cs, args classes in src/Armada.Server/Mcp/*Args.cs). Frozen list: docs/API_SURFACE_1.0.md (146 tools, 5 experimental). Families:
- Status: status, inbox, papercut_summary, token_usage_summary, stop_server (AdminOnly); backup, restore (AdminOnly, SQLite).
- Enumeration: enumerate ([[api-mcp-enumerate]]).
- Fleets: get/create/update/delete_fleet, delete_fleets. Vessels: get_vessel, add_vessel, update_vessel, update_vessel_context, delete_vessel(s). Vessel import: discover_vessels, import_vessels, categorize_vessel_import, apply_fleet_recommendations. Vessel health: vessel_health, evaluate_vessel_health, set_vessel_health_override.
- Voyages: dispatch, voyage_status, cancel_voyage, purge_voyage, delete_voyages. Missions: mission_status, evaluate_autoland, create/update_mission, cancel_mission, restart_mission, retry_landing, purge_mission, delete_missions, transition_mission_status, get_mission_diff, get_mission_log.
- Captains: get_captain, get_captain_tools, create/update_captain (autoApprove, muxApprovalPolicy...), stop_captain, release_captain, stop_all, delete_captain(s), get_captain_log. Signals: send_signal, delete_signals. Events: delete_event(s). Docks: get_dock, delete_dock(s), purge_dock, repair_dock, unstick_dock.
- Playbooks: get/create/update/delete_playbook. Fleet actions: create/update/delete_fleet_action, run_fleet_action, fleet_action_run_status, cancel_fleet_action_run. Merge queue: get_merge_entry, enqueue_merge, cancel_merge, process_merge_entry, process_merge_queue, delete_merge, purge_merge_queue, purge_merge_entry, purge_merge_entries. Harbors [Experimental]: get/create/update/delete_harbor, set_harbor_enabled.
- Objectives/backlog: get/create/update/delete_objective, list_objectives, reorder_objectives, list_backlog, get/create/update/delete_backlog_item, reorder_backlog_items, *_backlog_refinement_* (create, get, list, send message, summarize, apply, stop), create/get/dispatch_backlog_planning_session.
- Delivery: run_check, get_check_run, retry_check_run, create_release, get_release, create_deployment, get_deployment, approve/verify/rollback_deployment, get_runbook, get_runbook_execution, start_runbook_execution.
- Config: get/create/update/delete_persona, get/create/update/delete_pipeline, get/list/create/update/reset_prompt_template. Memory: search_memory, get/create/update/delete_memory. Model endpoints: get/create/update/delete/validate_model_endpoint, health_check_model_endpoints.
Not on MCP: workspace, standalone planning transcripts, request history, GitHub helpers, Mux discovery, OpenAPI.
