---
type: "Reference"
title: "Ask Armada MCP gate: read-only allowlist, proposals, approval contract"
description: "On a thread-scoped token, AskToolPolicy read-only tools (status, enumerate, inbox, *_status, vessel_health, papercut_summary, token_usage_summary, search_memory, evaluate_autoland, get_*, list_*) run; any other tool returns \"Proposed as aap_<id> and waiting for the user's approval...\" unless the thread auto-approves."
timestamp: "2026-10-05T05:49:35.9004300Z"
created: "2026-10-05T05:49:35.2145770Z"
slug: "api-mcp-ask-gate"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["arch-ask-armada-flow", "rt-ask-thread-mcp-token", "term-ask-thread", "api-ask-rest-routes"]
version: "1"
salience: "0.5"
---
Contract from docs/MCP_API.md "Ask Armada Thread-Scoped Calls"; code src/Armada.Core/Services/Ask/AskToolPolicy.cs (explicit allowlist; anything not listed, including future tools, is state-changing) and src/Armada.Server/Ask/AskActionService.cs (WrapTool).
Read-only allowlist: status, enumerate, inbox, voyage_status, mission_status, fleet_action_run_status, vessel_health, papercut_summary, token_usage_summary, search_memory, evaluate_autoland, every get_* reader (get_backlog_item, get_backlog_planning_session, get_backlog_refinement_session, get_captain, get_captain_log, get_captain_tools, get_check_run, get_deployment, get_dock, get_fleet, get_harbor, get_memory, get_merge_entry, get_mission_diff, get_mission_log, get_model_endpoint, get_objective, get_persona, get_pipeline, get_playbook, get_prompt_template, get_release, get_runbook, get_runbook_execution, get_vessel), list_backlog, list_backlog_refinement_sessions, list_objectives, list_prompt_templates.
Other tools with AutoApprove off: not executed; Pending AskActionProposal + ActionProposal message + ask.proposal event; result text "Proposed as aap_<id> and waiting for the user's approval in this conversation. Do not retry; tell the user what you proposed." With AutoApprove on: executed and recorded as an Executed proposal with an ActionResult message. Approve: POST /api/v1/ask/threads/{id}/proposals/{pid}/approve executes in-process under the approving user's context; reject: .../reject. A token whose thread is gone or belongs to someone else gets {"Error":"The conversation for this session no longer exists."}. Calls without the askThreadId claim (normal clients, stdio) are unaffected.
