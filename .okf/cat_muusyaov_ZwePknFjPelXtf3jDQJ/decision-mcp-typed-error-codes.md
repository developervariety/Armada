---
type: "Project"
title: "MCP tool errors carry a typed ErrorCode"
description: "All MCP tool errors return McpToolError {Error, ErrorCode (NotFound/InvalidArgument/Conflict/Forbidden/Unavailable/Failed), Code, StatusCode}; exceptions map by type."
timestamp: "2026-10-05T05:24:25.0441590Z"
created: "2026-10-05T05:24:24.7198150Z"
slug: "decision-mcp-typed-error-codes"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["decision-typed-not-found", "pref-structural-not-textual-logic"]
version: "1"
salience: "0.5"
---
Decided and implemented 2026-10-05 (FRAGILITY_REMEDIATION R0) after the owner rejected text matching: Armada.Core/Enums/McpToolErrorCodeEnum and Armada.Core/Models/McpToolError (factories NotFound, InvalidArgument, Conflict, Forbidden, Unavailable, Failed, FromException mapping KeyNotFoundException->NotFound, ArgumentException->InvalidArgument, UnauthorizedAccessException->Forbidden, NotSupportedException->Unavailable, InvalidOperationException->Conflict). All 210 former `new { Error = "..." }` returns converted; existing Code (vessel import detail codes) and StatusCode (fleet actions) fields kept for compatibility. McpToolRegistrar.MapToolExceptions (R2) maps handler exceptions once. Clients branch on ErrorCode; tests use Test.Shared/Infrastructure/McpToolResultProbe. Documented in docs/MCP_API.md "Error Responses".
