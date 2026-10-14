---
type: "Reference"
title: "TLS: terminate in a reverse proxy; keep the Admiral on loopback"
description: "rest.ssl is unsupported (no cert path, no MCP TLS); put nginx/Caddy/Traefik in front forwarding to 127.0.0.1:7890/7891 with WebSocket upgrades for /ws and /v1.0/harbor/connect."
timestamp: "2026-10-05T05:29:51.0421480Z"
created: "2026-10-05T05:29:50.6181230Z"
slug: "tls-and-network-exposure"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["ports-and-listeners", "default-credentials-bind-guard", "proxy-operations"]
version: "1"
salience: "0.5"
---
docs/OPERATIONS.md: the Admiral's rest.ssl flag turns on TLS in the web server but there is no certificate path setting and the MCP listener has no TLS option, so treat it as unsupported for 1.0. Use a reverse proxy or load balancer that holds the certificate and forwards to 127.0.0.1:7890 and 127.0.0.1:7891; forward WebSocket upgrades for /ws and, in split mode, /v1.0/harbor/connect. Keep rest.hostname on loopback behind the proxy. Behind a reverse proxy every client shares one address for login rate limiting, so raise loginRateLimit.maxFailuresPerAddress. Built-in TLS exists only on the remote tunnel (remoteControl.tunnelUrl wss:// or https://, certificate validated unless allowInvalidCertificates). Database connections can require encryption with database.requireEncryption. Exposing beyond localhost requires changing the default admin password first (the bind guard refuses otherwise).
