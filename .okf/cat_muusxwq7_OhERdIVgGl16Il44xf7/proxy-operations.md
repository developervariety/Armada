---
type: "Reference"
title: "Armada.Proxy and the remote tunnel: setup and failure modes"
description: "Admiral dials remoteControl.tunnelUrl (ws/wss/http/https) to Armada.Proxy :7893 /tunnel with the shared password; proxy serves portal, /dashboard relay, /api/v1/* and /ws; password required (not armadaadmin)."
timestamp: "2026-10-05T05:32:13.6110910Z"
created: "2026-10-05T05:32:12.9530150Z"
slug: "proxy-operations"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["proxy-route-policy", "proxy-password-and-rate-limits", "open-o11-proxy-shared-identity"]
version: "1"
salience: "0.5"
---
docs/PROXY_API.md, docs/TUNNEL_OPERATIONS.md, docs/REMOTE_MGMT.md. Armada side (settings.json remoteControl): enabled false, tunnelUrl (default constant http://proxy.armadago.ai:7893/tunnel), instanceId (optional override), enrollmentToken, password (must equal the proxy password; default armadaadmin triggers a warning), connectTimeoutSeconds 15, heartbeatIntervalSeconds 30, reconnectBaseDelaySeconds 5, reconnectMaxDelaySeconds 60, allowInvalidCertificates false (dev only). Prefer wss://.
Proxy side (ArmadaProxy section in proxysettings.json, or ARMADA_PROXY_SETTINGS_FILE): hostname localhost, port 7893, password (ARMADA_PROXY_PASSWORD wins; blank or armadaadmin refused unless allowDefaultPassword / ARMADA_PROXY_ALLOW_DEFAULT_PASSWORD), requireEnrollmentToken, enrollmentTokens, handshakeTimeoutSeconds 15, staleAfterSeconds 90, requestTimeoutSeconds 20, loginMaxFailures 10, loginFailureWindowSeconds 900, loginLockoutSeconds 900, trustForwardedHeaders false, secureCookie false. Docker: docker/proxy/compose.yaml requires ARMADA_PROXY_PASSWORD. Health: /proxy-api/v1/status/health. Instance states connected, stale, offline.
Inspect: Armada Server page, GET /api/v1/status (state, lastError, reconnectAttempts, latencyMs). Failure modes: enabled without tunnelUrl (state Error), invalid scheme, handshake rejected (instanceId, enrollment token), TLS validation failure, unreachable proxy, stale instance; each shows differently in admiral.log.
