---
type: "Reference"
title: "Security identities: session, bearer, API key, Ask thread token, loopback MCP, Harbor, proxy"
description: "Seven identities per docs/SECURITY_REVIEW.md: X-Token session, Bearer credential, X-Api-Key (system@armada global admin), Ask thread token (MCP only), loopback MCP caller, Harbor x-access-key, proxy shared password."
timestamp: "2026-10-05T05:32:59.9071970Z"
created: "2026-10-05T05:32:59.3558560Z"
slug: "security-identities"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["route-authorization-registry", "loopback-mcp-unauthenticated", "ask-thread-token-precedence", "local-api-key-system-identity"]
version: "1"
salience: "0.5"
---
- User session: POST /api/v1/authenticate (email+password, or any valid credential header) returns an encrypted session token (AES-256 with sessionTokenEncryptionKey), sent as X-Token. Scope: user's tenant; IsAdmin = global admin, IsTenantAdmin = tenant admin.
- Bearer credential: Authorization: Bearer <token>; server-generated, shown once at creation, masked (****abcd) on every read.
- Local API key: X-Api-Key with settings.json apiKey (generated ak_... on first start, read by the Helm CLI); acts as synthetic global admin system@armada in tenant ten_system, which can never password-login.
- Ask thread token: session token bound to one Ask thread for a captain's MCP connection; refused on REST and WebSocket; state-changing tools become proposals; always wins over other credentials on the request (F-31).
- Loopback MCP caller: no credential on a loopback-bound MCP listener from loopback with mcp.allowUnauthenticatedLoopback true (default, D2): default tenant's tenant admin, confined to the default tenant.
- Harbor: link upgrade with x-access-key (or Authorization) validated as an Armada credential; tenant and user from it.
- Proxy user: shared proxy password (challenge-response), reaches only the relay; relayed calls still need Armada credentials.
Header names: X-Token, Authorization, X-Api-Key (CORS allows these).
