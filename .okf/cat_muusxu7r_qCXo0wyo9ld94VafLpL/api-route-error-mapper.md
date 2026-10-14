---
type: "Reference"
title: "RouteErrorMapper: exception type to HTTP status (never message text)"
description: "Routes map service exceptions by TYPE: KeyNotFoundException -> 404, ArgumentException -> 400, UnauthorizedAccessException -> 403, InvalidOperationException -> 400 or 409 (route chooses), anything else -> 500 InternalError; MCP uses the same mapping into McpToolError."
timestamp: "2026-10-05T05:48:44.3848500Z"
created: "2026-10-05T05:48:43.9267230Z"
slug: "api-route-error-mapper"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-rest-error-response", "api-mcp-error-codes", "decision-typed-not-found", "pref-structural-not-textual-logic"]
version: "1"
salience: "0.5"
---
src/Armada.Server/Routes/RouteErrorMapper.cs: IsMapped(ex) true for KeyNotFoundException, ArgumentException (incl. ArgumentNullException), UnauthorizedAccessException, InvalidOperationException. StatusCodeFor(ex, invalidOperationStatusCode = 400): 404 / 400 / 403 / invalidOperationStatusCode (delete routes pass 409) / 500. ResultFor(status): 400 BadRequest, 403 Forbidden, 404 NotFound, 409 Conflict, else InternalError. ToResponse(req, ex, ...) sets the response status and returns ApiErrorResponse {Error, Message = ex.Message}.
Service rule (FRAGILITY_REMEDIATION R2, see [[decision-typed-not-found]]): missing or invisible entities throw KeyNotFoundException; bad input ArgumentException; state conflicts InvalidOperationException; role problems UnauthorizedAccessException; unsupported features NotSupportedException (MCP Unavailable; REST routes return 501 explicitly). Never branch on exception message text. The MCP twin is McpToolError.FromException ([[api-mcp-error-codes]]), which additionally maps NotSupportedException -> Unavailable and InvalidOperationException -> Conflict.
