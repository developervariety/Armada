---
type: "Reference"
title: "MCP enumerate tool: entity types, filters, include flags and length hints"
description: "enumerate(entityType, pageNumber, pageSize<=1000 default 10, order, createdAfter/Before, status, search, ids...) returns EnumerationResult; large fields are omitted unless includeDescription/includeContext/includeTestOutput/includePayload/includeMessage/includeOutput is true, returning *Length hints instead; DiffSnapshot is never included."
timestamp: "2026-10-05T05:49:15.1711720Z"
created: "2026-10-05T05:49:14.4622940Z"
slug: "api-mcp-enumerate"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-mcp-tool-families", "api-rest-pagination", "data-enumeration-query"]
version: "1"
salience: "0.5"
---
src/Armada.Server/Mcp/Tools/McpEnumerateTools.cs, args EnumerateArgs; docs/MCP_API.md "enumerate".
entityType values: objectives (aliases backlog, backlog_item, backlog_items), fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue, harbors, playbooks, personas, memories, prompt_templates, pipelines, workflow_profiles, project_profiles, skills, check_runs, releases, deployments, incidents, runbooks, runbook_executions, jobs, model_endpoints, vessel_import_batch (aliases), fleet_action, fleet_action_run, fleet_action_run_target (requires runId), vessel_health (aliases vessel-health, health; every active vessel, sorted by name).
Filters: status, search (releases, deployments, incidents, runbooks, runbook_executions, memories, objectives, vessel_health name), fleetId, vesselId, captainId, voyageId, missionId, eventType, signalType, toCaptainId, unreadOnly, runId, includeInactive (fleet_action).
Include flags (default false -> length hint): includeDescription (missions, voyages -> descriptionLength), includeContext (vessels ProjectContext/StyleGuide -> length hints), includeTestOutput (merge_queue -> testOutputLength), includePayload (events -> payloadLength), includeMessage (signals -> messageLength), includeOutput (fleet_action_run_target RenderedText/OutputText/ErrorText -> outputLength, errorLength, renderedLength). Missions never include DiffSnapshot (use get_mission_diff). Guidance (CLAUDE.md, INSTRUCTIONS_FOR_*): use small pageSize 10-25 and filters; set include flags only when needed. Owned entities are scoped to the caller.
