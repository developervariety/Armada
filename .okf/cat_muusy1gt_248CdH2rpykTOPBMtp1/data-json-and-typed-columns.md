---
type: "Reference"
title: "Column conventions: typed columns for anything queried, *_json text for lists, enums as strings"
description: "Anything the API sorts, filters, or reports is a typed, indexed column (statuses, kinds, ids, timestamps); list-valued fields are serialized with System.Text.Json into *_json text columns; enums are stored by name; free text (tool payloads, model output) stays unmanaged text."
timestamp: "2026-10-05T05:47:11.5694490Z"
created: "2026-10-05T05:47:11.0645760Z"
slug: "data-json-and-typed-columns"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-methods-pattern", "data-dialect-add-column", "rt-mission-failure-kind"]
version: "1"
salience: "0.5"
---
Conventions seen across src/Armada.Core/Database/*/Implementations and model comments (Job, VesselHealth, AskThread design):
- Typed columns: every status, kind, identifier, and timestamp used for sorting/filtering/reporting is its own column, indexed where queried (e.g. missions.status, missions.failure_kind (v77, string enum; read via MissionFailureKindColumn.FromDbValue which maps unknown text to Unknown), jobs.kind/status, vessel_health per-criterion status columns, ask_messages.sequence). Never infer a classification from free text later (FRAGILITY_REMEDIATION rule: persist kinds as columns).
- Lists: JSON-serialized into text columns named *_json, e.g. vessels.protected_branch_patterns_json, protected_path_patterns_json, private_identifier_denylist_json, auto_land_path_allow_globs_json, auto_land_path_deny_globs_json (JsonSerializer.Serialize(list ?? new List<string>())); captains.allowed_personas (JSON array) and runtime_options_json; voyages captain_overrides_json; docks git_anchors_json. Child collections that need filtering get their own tables instead (vessel_health_findings, vessel_dependencies, ask_message_tool_calls, fleet_action_run_targets).
- Enums: stored as their string names (ToString) and parsed back case-insensitively.
- Unmanaged text: tool arguments/results, content_text, thinking_text, diff snapshots, agent output, ResultJson are opaque text; their content is outside the compatibility promise.
- Code rule: deserialize JSON into typed classes, never poke JsonElement (CLAUDE.md).
