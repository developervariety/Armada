---
type: "Project"
title: "V1 D2: MCP authenticated by default, loopback exception"
description: "MCP requires a credential except loopback callers of a loopback-bound listener when Mcp.AllowUnauthenticatedLoopback is true (default)."
timestamp: "2026-10-05T05:23:55.7216700Z"
created: "2026-10-05T05:23:55.5514810Z"
slug: "decision-v1-mcp-auth-d2"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["open-security-followups"]
version: "1"
salience: "0.5"
---
Decided 2026-10-04: MCP is authenticated by default; unauthenticated calls are allowed only when the MCP listener is bound to loopback and Mcp.AllowUnauthenticatedLoopback is true (the default), so the local Claude Code setup works without a token. Such callers act as the default tenant's tenant admin; open finding O-20 notes any local process (including local captains) gets that access. Non-loopback bindings always require Authorization: Bearer, X-Token, or X-Api-Key.
