---
type: "Reference"
title: "MCP tool errors: McpToolError with typed ErrorCode"
description: "Failing tools return {\"Error\": msg, \"ErrorCode\": NotFound|InvalidArgument|Conflict|Forbidden|Unavailable|Failed, \"Code\": detail?, \"StatusCode\": http?}; branch on ErrorCode; unexpected errors, auth-gate refusals, and rate limits come back as isError results instead."
timestamp: "2026-10-05T05:49:27.1503280Z"
created: "2026-10-05T05:49:26.5487850Z"
slug: "api-mcp-error-codes"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["decision-mcp-typed-error-codes", "api-route-error-mapper", "api-mcp-transport-and-auth"]
version: "1"
salience: "0.5"
---
Model src/Armada.Core/Models/McpToolError.cs; enum McpToolErrorCodeEnum (NotFound, InvalidArgument, Conflict, Forbidden, Unavailable, Failed); mapping McpToolError.FromException: KeyNotFoundException -> NotFound, ArgumentException -> InvalidArgument, UnauthorizedAccessException -> Forbidden, NotSupportedException -> Unavailable, InvalidOperationException -> Conflict, else Failed. McpToolRegistrar wraps every registration (MapToolExceptions) so typed service exceptions become McpToolError results; other exceptions propagate and Voltaic reports isError (IncludeToolExceptionMessages=true keeps the message). Factories: McpToolError.NotFound/InvalidArgument/Conflict/Forbidden/Unavailable/Failed(error, code).

Meanings: NotFound = missing or not visible to the caller (including another tenant's entity, same text as missing); InvalidArgument = missing/malformed/out-of-range argument; Conflict = state forbids it or already exists; Forbidden = caller lacks permission; Unavailable = feature not configured/available (e.g. no saved diff, planning on non-SQLite); Failed = anything else. Code carries feature detail codes (vessel import BatchNotFound, PathNotAllowed...); StatusCode keeps the HTTP-equivalent status fleet action tools always returned. Success results have no Error field. The { "Error": ... } convention and ErrorCode values are part of the 1.0 contract. Decision: [[decision-mcp-typed-error-codes]]; tests use Test.Shared McpToolResultProbe.
