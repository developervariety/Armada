---
type: "Project"
title: "Remote access: outbound tunnel (RemoteTunnelManager) and Armada.Proxy relay"
description: "With remoteControl.enabled the Admiral dials Armada.Proxy's /tunnel (default http://proxy.armadago.ai:7893/tunnel, protocol 2026-04-04) and serves generic relays armada.http.request and armada.ws.open/message/close; the proxy serves the shared dashboard and relays /api/v1/* and /ws to the selected instance with a route policy."
timestamp: "2026-10-05T05:41:52.8844380Z"
created: "2026-10-05T05:41:52.1950720Z"
slug: "arch-remote-tunnel-and-proxy"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["api-proxy-routes", "arch-websocket-hub"]
version: "1"
salience: "0.5"
---
Admiral side: src/Armada.Server/RemoteTunnelManager.cs (outbound WebSocket, URL normalization, handshake armada.tunnel.handshake with instanceId, optional enrollmentToken, and a timestamped nonce SHA-256 password proof (RemoteTunnelAuth), ping/pong heartbeat, exponential reconnect, RemoteTunnelStatus/RemoteTunnelStateEnum Disabled/Disconnected/Connecting/Connected/Error/Stopping surfaced in /api/v1/status and status/health) and RemoteDashboardRelayService (executes relayed REST requests and WebSocket sessions locally, publishes events back). ArmadaServer.HandleRemoteTunnelRequestAsync accepts only armada.http.request, armada.ws.open, armada.ws.message, armada.ws.close; any other method returns 404 unsupported_method (the older feature-specific methods described in docs are no longer served). Settings remoteControl: enabled (false), tunnelUrl, instanceId, enrollmentToken, password (must match proxy), connect/heartbeat/reconnect timings, allowInvalidCertificates. Envelope: RemoteTunnelEnvelope {type request/response/event/ping/pong/error, correlationId, method, statusCode, success, errorCode, message, payload}. Bodies are base64 in one payload (no chunking; RemoteRelayMaxBodyBytes 8 MB).
Proxy side: src/Armada.Proxy (ArmadaProxyServer; Services InstanceRegistry, RemoteInstanceSession, ProxyAuthService, ProxyLoginRateLimiter, ProxyRoutePolicyService, DashboardAssetResolver), default port 7893. Browser: challenge/login with SHA-256 proof -> cookie armada_proxy_session -> select instance -> /dashboard (same React bundle) -> same-origin /api/v1/* and /ws relayed. Policy: always blocks server stop/reset, status shutdown/factory-reset, restore; blocks writes to settings, tenants, users, credentials. MCP is not relayed. See [[api-proxy-routes]].
