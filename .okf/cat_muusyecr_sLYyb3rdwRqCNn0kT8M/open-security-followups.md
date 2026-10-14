---
type: "Project"
title: "Open security findings O-04, O-11, O-20 and other O-xx items"
description: "docs/SECURITY_REVIEW.md open items: O-04 split-mode captain MCP credential, O-11 proxy identity/TLS validation, O-20 loopback MCP as tenant admin, O-06 tenant seeding, O-17 self-service password change, O-19 locale strings."
timestamp: "2026-10-05T05:27:00.8321480Z"
created: "2026-10-05T05:27:00.2341700Z"
slug: "open-security-followups"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["decision-v1-mcp-auth-d2", "open-v1-readiness-remaining"]
version: "1"
salience: "0.5"
---
As of 2026-10-05 (see docs/SECURITY_REVIEW.md for the authoritative table): O-04 split-mode (Harbor) captains receive no MCP credential so authenticated MCP on a non-loopback Admiral refuses their call-home tools (id takeover fixed as F-29); O-11 Armada.Proxy uses one shared password with no per-user identity, AllowInvalidCertificates disables tunnel TLS validation, lockouts are in memory (canonical path matching was fixed in R1c); O-20 with Mcp.AllowUnauthenticatedLoopback on (default, D2) any process on the Admiral host can call MCP as the default tenant's tenant admin, including local captains; O-06 creating a tenant seeds admin@armada with the default password; O-17 self-service password change via PUT /api/v1/users/{id} does not require the current password; O-19 new security strings are English in every locale.
