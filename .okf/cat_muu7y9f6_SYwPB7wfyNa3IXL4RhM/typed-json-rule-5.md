---
type: "Project"
title: "Fragility rule 5: deserialize JSON into typed classes (no JsonElement/JsonNode walking)"
description: "Same as the CLAUDE.md rule, with one exception: JsonNode may be used only to preserve unknown keys when editing third-party config files."
timestamp: "2026-10-05T05:45:13.1284280Z"
created: "2026-10-05T05:45:12.8920640Z"
slug: "typed-json-rule-5"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-jsonelement-access", "test-assertions-structured-rule-7"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 5 restates the project rule: deserialize JSON into typed classes; no JsonElement / JsonNode property walking except to preserve unknown keys when editing third-party config files (agent CLI MCP configs). R3 applied it to the MCP client (JsonRpcResponse<T>, McpListToolsResult, SSE frame chosen by id), runtime stream events, tool arguments (approve_deployment, start_runbook_execution, runtime tools rejecting wrong types with invalid_parameter), planning/refinement summaries via a shared string-aware JSON extractor (fenced block first), and Proxy settings and bodies. Tests deserialize too (McpToolResultProbe).
