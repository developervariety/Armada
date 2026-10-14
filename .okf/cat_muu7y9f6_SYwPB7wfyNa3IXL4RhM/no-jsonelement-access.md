---
type: "Project"
title: "C#: deserialize JSON into typed classes; no JsonElement/JsonNode walking"
description: "Use JsonSerializer.Deserialize<T>(json) into a strongly typed class; never GetProperty()/GetString() on JsonElement; only exception: preserving unknown keys when editing third-party config files."
timestamp: "2026-10-05T05:42:37.0701080Z"
created: "2026-10-05T05:42:36.7497260Z"
slug: "no-jsonelement-access"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["typed-json-rule-5", "test-assertions-structured-rule-7"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md, reinforced as fragility rule 5): always deserialize JSON into a strongly typed class (e.g. JsonSerializer.Deserialize<Fleet>(json)) instead of walking JsonElement or JsonNode with GetProperty/GetString/TryGetProperty. Add a small DTO class when needed (tests do this too: McpToolResultProbe, McpRpcEnvelope, UpgradeHealthResponse). Exception (FRAGILITY_REMEDIATION.md rule 5): JsonNode editing is allowed only to preserve unknown keys when modifying third-party config files (for example an agent CLI's MCP config). Fragility R3 replaced four hand-walked stream parsers with typed classes in Armada.Core.Protocol for this reason.
