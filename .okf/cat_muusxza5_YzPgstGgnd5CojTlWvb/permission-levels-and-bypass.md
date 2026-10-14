---
type: "Reference"
title: "Permission levels and admin bypass rules"
description: "NoAuthRequired < Authenticated < TenantAdmin < AdminOnly; global admins pass all; tenant admins pass TenantAdmin within their tenant but cannot modify, delete, or mint credentials for a global admin (F-09)."
timestamp: "2026-10-05T05:33:19.6784760Z"
created: "2026-10-05T05:33:19.4012780Z"
slug: "permission-levels-and-bypass"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["route-authorization-registry", "mcp-tool-caller-scoping", "websocket-authentication"]
version: "1"
salience: "0.5"
---
PermissionLevel (src/Armada.Core/Authorization/PermissionLevel.cs): NoAuthRequired, Authenticated, TenantAdmin, AdminOnly. Rules: global admins (User.IsAdmin) pass every level in any tenant; tenant admins (IsTenantAdmin) pass TenantAdmin within their own tenant; regular users see only their own entities. Data scoping follows the same rule everywhere (REST handlers, McpCallerScope): global admin any tenant, tenant admin own tenant, user own entities; out-of-scope ids answer as not found. Special cases: tenant admins cannot modify, delete, or mint credentials for a global admin account (403; F-09); only global admins can delete audit.* events (F-16); tokens are always server-generated and never changed by update. WebSocket /ws commands all require a global admin. Notable levels: 19 POST .../enumerate routes are Authenticated (scoped like GET lists, F-17); check-run writes, harbor probe, workspace exec are TenantAdmin; server stop/restart/rebuild/rollback, backup/restore, tenant create are AdminOnly.
