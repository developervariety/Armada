---
type: "Reference"
title: "Armada.Proxy API: proxy-local routes, relayed routes, tunnel endpoint"
description: "Proxy (default http://localhost:7893) owns /, /proxy-api/v1/status/health, auth challenge/login/logout, instances, session/context|instance|logout-instance, /dashboard; relays /api/v1/* and /ws for the selected instance; Admirals connect at WS /tunnel; versioned separately from the 1.0 surface."
timestamp: "2026-10-05T05:51:21.3012690Z"
created: "2026-10-05T05:51:20.6336670Z"
slug: "api-proxy-routes"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["arch-remote-tunnel-and-proxy", "proxy-route-policy", "proxy-operations", "proxy-password-and-rate-limits"]
version: "1"
salience: "0.5"
---
Docs: docs/PROXY_API.md, docs/TUNNEL_PROTOCOL.md (tunnel protocol version 2026-04-04, Constants.RemoteTunnelProtocolVersion), docs/REMOTE_MGMT.md; code src/Armada.Proxy/ArmadaProxyServer.cs. Config section "ArmadaProxy" in proxysettings.json (hostname localhost, port 7893, password via ARMADA_PROXY_PASSWORD env which wins; refuses to start with blank or default "armadaadmin" unless allowDefaultPassword; requireEnrollmentToken, enrollmentTokens, handshakeTimeoutSeconds 15, staleAfterSeconds 90, requestTimeoutSeconds 20, maxRecentEvents 50, loginMaxFailures/window/lockout, trustForwardedHeaders, secureCookie).
Proxy-local routes: GET / (portal), GET /proxy-api/v1/status/health (no auth), GET /proxy-api/v1/auth/challenge (nonce, expiresUtc), POST /proxy-api/v1/auth/login {nonce, proofSha256} -> {token, expiresUtc, selectedInstanceId} + cookie armada_proxy_session (HttpOnly, SameSite=Lax; header X-Armada-Proxy-Session for non-browsers), POST /proxy-api/v1/auth/logout, GET /proxy-api/v1/instances (connected deployment summaries; instance state connected/stale/disconnected), GET /proxy-api/v1/session/context (selected instance + relay flags), POST /proxy-api/v1/session/instance, POST /proxy-api/v1/session/logout-instance, GET /dashboard[/*] (shared React bundle + i18n catalog), WS /tunnel (Admiral outbound tunnel).
Relayed: ALL /api/v1/* (401 without proxy session, 409 without a selected instance) and WS /ws; blocked routes and write-blocked families are enforced by ProxyRoutePolicyService ([[proxy-route-policy]]). Users still sign into Armada inside the relayed dashboard (proxy session != Armada session). MCP is not relayed. Architecture: [[arch-remote-tunnel-and-proxy]]; operations: [[proxy-operations]].
