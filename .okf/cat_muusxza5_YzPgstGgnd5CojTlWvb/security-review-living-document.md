---
type: "Reference"
title: "docs/SECURITY_REVIEW.md is a living document updated with every entry-point change"
description: "Findings F-nn (fixed) and O-nn (open, with owner); REST and MCP tables generated from the registries; update findings and inventory in the same change that adds, removes, or fixes an entry point; external review (W1.8) not yet done."
timestamp: "2026-10-05T05:35:54.4077510Z"
created: "2026-10-05T05:35:53.8012630Z"
slug: "security-review-living-document"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["route-authorization-registry", "authorization-coverage-test", "living-plan-docs-update"]
version: "1"
salience: "0.5"
---
docs/SECURITY_REVIEW.md (V1_READINESS W1.1; last updated 2026-10-04 after the W1.9 follow-ups; external review W1.8 not yet performed). Sections: security model (identities, authorization, safe defaults, accounting, secrets), findings (F-01..F-32 fixed in W1; O-01..O-20 open or accepted with owners), running agents safely, and the inventory of REST routes, MCP tools, WebSocket commands, and other entry points (Harbor link, proxy, tunnel, stdio MCP, static files, file system and process entry points). The REST and MCP tables are generated from RouteAuthorizationRegistry and McpToolAuthorizationRegistry, and E2E.AuthorizationCoverage proves those registries match the server. Severity: Critical (unauthenticated RCE or takeover), High (privilege escalation, cross-tenant access, code execution by low-privilege user), Medium (disclosure, missing audit, defense in depth), Low (hardening). Rule: any change that adds, removes, or fixes an entry point updates the findings table and inventory in the same commit.
