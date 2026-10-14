---
type: "Reference"
title: "Harbor link authentication and id binding (F-05, F-29)"
description: "Harbor links authenticate with x-access-key validated as an Armada credential (tenant and user come from it); unauthenticated links only from loopback to a loopback-bound Admiral with harbor.requireAuth off; Harbor ids bound to their registering identity."
timestamp: "2026-10-05T05:35:34.4226810Z"
created: "2026-10-05T05:35:34.0833660Z"
slug: "harbor-link-authentication"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["harbor-setup-split-mode", "open-o04-harbor-mcp-credential", "captain-auto-approve-flags"]
version: "1"
salience: "0.5"
---
WebSocket at harbor.linkPath (default /v1.0/harbor/connect) on the Admiral port. x-access-key (or Authorization) is validated as an Armada credential (bearer, session token, or API key) and determines tenant and user; a global admin may name a tenant with x-tenant-guid. With no credential the link is accepted only from loopback to a loopback-bound Admiral while harbor.requireAuth is false (default). Before F-05 any host could link and pick any tenant via the unauthenticated x-tenant-guid header, receiving prompts, environment, and model endpoint keys. F-29: a Harbor id is bound to the identity (tenant and user) that registered it; another identity or an unauthenticated link is refused before the live connection is replaced, and a refused link no longer marks the owner's Harbor disconnected. First message must be a handshake; messages parsed by HarborProtocol, malformed ones dropped. Server-to-Harbor messages (launch, git, kill, stdin, deferredLaunch) route only to Harbors eligible for the mission's user or tenant; launch carries the resolved auto-approve decision (F-28). POST /api/v1/harbors/{id}/probe is TenantAdmin and audited (F-06). Tests: Services.HarborLaunchSecurity. Open: no MCP credential for split-mode captains (O-04); link not rate limited (O-05).
