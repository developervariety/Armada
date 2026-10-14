---
type: "Project"
title: "O-04 (Medium, open by design): split-mode captains receive no MCP credential"
description: "With authenticated MCP on a non-loopback Admiral, Harbor-launched captains' call-home MCP calls are refused; proposed fix is a mission-scoped MCP-only token in a per-launch config."
timestamp: "2026-10-05T05:36:09.4914290Z"
created: "2026-10-05T05:36:09.2644800Z"
slug: "open-o04-harbor-mcp-credential"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["harbor-link-authentication", "open-o20-loopback-mcp-default-tenant", "harbor-setup-split-mode"]
version: "1"
salience: "0.5"
---
Split mode is experimental (D3). The Harbor runner ignores the advertised MCP URL and writes no MCP config, and the token identity is unsettled: the mission owner's token would be exposed to a Harbor run by another tenant user or a shared Harbor; the Harbor owner's identity can exceed the mission's tenant for an admin-registered shared Harbor. Id takeover was closed by F-29. Proposed: a mission-scoped token type (MCP only, bound to the mission's tenant and user, expiring with the mission) written into a per-launch URL-based MCP config by the Harbor runner; the same token would fix O-20. Owner: Harbor split mode. Status 2026-10-04: open deliberately.
