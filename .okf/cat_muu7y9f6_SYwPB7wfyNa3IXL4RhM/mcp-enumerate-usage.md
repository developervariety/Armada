---
type: "Project"
title: "Using Armada's own MCP tools from an agent: small pages, filters, include flags off"
description: "Use enumerate with pageSize 10-25 and filters (vesselId, status, dates); set includeDescription/Context/TestOutput/Payload/Message/Output only when needed; start with status; confirm destructive actions."
timestamp: "2026-10-05T05:44:40.5814110Z"
created: "2026-10-05T05:44:40.3252370Z"
slug: "mcp-enumerate-usage"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["id-prefixes", "mcp-tool-caller-scoping"]
version: "1"
salience: "0.5"
---
CLAUDE.md "Using Armada's MCP Tools": use enumerate with a small pageSize (10-25) to conserve context; filter by vesselId, status, date ranges; set include flags (includeDescription, includeContext, includeTestOutput, includePayload, includeMessage, includeOutput) to true only when you need that data, since large fields are excluded by default and replaced with length hints. AGENTS.md (Codex/Cursor orchestrator rules): prefer Armada MCP tools over shell or file inspection for Armada state; start with status for broad questions; use voyage_status / mission_status for status; get_mission_log, get_captain_log, get_mission_diff when investigating; confirm before deleting, purging, cancelling, stopping captains, or stopping the server; ID prefixes flt_ vsl_ cpt_ msn_ vyg_ dck_ sig_ mrg_.
