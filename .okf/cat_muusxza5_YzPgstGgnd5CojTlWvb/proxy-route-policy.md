---
type: "Reference"
title: "Proxy relay route policy on canonical paths (ProxyRoutePolicyService)"
description: "Relay blocks server/stop, server/reset, status/shutdown, status/factory-reset, restore for all methods and non-GET writes to settings, tenants, users, credentials; path canonicalized first by UrlPathCanonicalizer."
timestamp: "2026-10-05T05:35:20.5823290Z"
created: "2026-10-05T05:35:20.2302540Z"
slug: "proxy-route-policy"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["proxy-operations", "proxy-password-and-rate-limits", "path-containment-and-managed-deletes"]
version: "1"
salience: "0.5"
---
src/Armada.Proxy/Services/ProxyRoutePolicyService.cs is the central gate for relayed dashboard requests. The path is canonicalized once with UrlPathCanonicalizer (percent-decoded, slashes collapsed, trailing slash dropped; ambiguous encodings and dot segments rejected), route templates are matched on canonical segments after /api/v1, and on success the request path is replaced with the canonical one so the relayed request is exactly what was evaluated (fragility R1c; the old exact-string policy was bypassable). Blocked for every method: status/shutdown, server/stop, status/factory-reset, server/reset, restore. POST allowed before the admin write block: authenticate, tenants/lookup. Read-only administrative resources (first segment): settings, tenants, users, credentials (non-GET/HEAD gets 403 with a policy message). Still allowed: normal reads, other writes, GET /api/v1/backup downloads, /ws traffic. Relayed calls still need Armada credentials at the Admiral. Tests: Services.ProxyRoutePolicyService, Services.ProxySecurity.
