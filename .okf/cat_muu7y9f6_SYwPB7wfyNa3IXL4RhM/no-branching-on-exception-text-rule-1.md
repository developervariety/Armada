---
type: "Project"
title: "Fragility rule 1: never branch on exception message text"
description: "Throw and catch typed exceptions: KeyNotFoundException for missing or invisible entities, ArgumentException for bad input, a dedicated type for anything else callers must distinguish; routes map types via RouteErrorMapper."
timestamp: "2026-10-05T05:44:50.0036170Z"
created: "2026-10-05T05:44:49.6010180Z"
slug: "no-branching-on-exception-text-rule-1"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["decision-typed-not-found", "decision-mcp-typed-error-codes", "pref-structural-not-textual-logic", "no-substring-classification-rule-2"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 1 (applies to all new code and fixes). Bad: `catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))` or choosing 404 vs 400 by searching ex.Message. Good: services throw KeyNotFoundException for a missing or out-of-scope entity (R2 converted ~114 sites), ArgumentException for bad input, and dedicated types such as GitCommandException (exit code, stderr), UnsafeListenerConfigurationException, MigrationBackupRequiredException. REST routes map exception types to 404/400/403/409 through src/Armada.Server/Routes/RouteErrorMapper.cs; MCP handlers map through McpToolRegistrar.MapToolExceptions to McpToolError codes (NotFound, InvalidArgument, Conflict, Forbidden, Unavailable). Same rule in clients: dashboard ApiError.status/code and ArmadaApiException codes, never message compares (R4). Owner preference: string matching on text or exception messages is unacceptable (pref-structural-not-textual-logic).
