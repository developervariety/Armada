---
type: "Project"
title: "Fragility rule 7: tests assert on typed models, status codes, and error codes"
description: "Deserialize responses and stub request bodies, assert on fields, HTTP status, and McpToolError ErrorCode (McpToolResultProbe); select stub requests by route, not position or body text; \"the call failed\" is never \"the call was denied\"."
timestamp: "2026-10-05T05:45:25.3280340Z"
created: "2026-10-05T05:45:25.0296320Z"
slug: "test-assertions-structured-rule-7"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["test-data-isolation-rules", "no-branching-on-exception-text-rule-1", "tui-headless-testing", "decision-mcp-typed-error-codes"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 7 and workstream R5 (about 260 test sites, open as of 2026-10-05). Do: deserialize REST responses into models and assert on fields and status codes; deserialize MCP tool results with McpToolResultProbe.From(result) and assert on ErrorCode (NotFound, InvalidArgument, Conflict, Forbidden, Unavailable), Deleted, Purged, Status; select StubHttpHandler requests by route ("METHOD path"), not by index or body substring; for authorization tests require the specific denial (401/403 or ErrorCode Forbidden/NotFound), never accept any failure as a denial. Do not: match English message text, write OR chains like "status is X or text contains Y", write always-pass checks (a historical bug: asserting a JSON string "contains Description", which DescriptionLength also satisfied), or convert exceptions to strings and skip them. Make tests host-locale independent and use exit codes rather than tool wording. Dashboard tests: assert on parsed messages and matched calls.
