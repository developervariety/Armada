---
type: "Project"
title: "O-11 (Medium, partly closed): proxy has one shared password and no per-user identity"
description: "Remaining after F-30 and R1c: single shared password, allowInvalidCertificates disables tunnel TLS validation, lockouts in memory, /proxy-api/v1/auth/challenge not rate limited. Owner: proxy."
timestamp: "2026-10-05T05:36:46.3567380Z"
created: "2026-10-05T05:36:45.9496260Z"
slug: "open-o11-proxy-shared-identity"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["proxy-password-and-rate-limits", "proxy-route-policy", "proxy-operations"]
version: "1"
salience: "0.5"
---
Armada.Proxy authenticates browsers and tunnels with one shared password; there is no per-user identity at the proxy (relayed calls still need Armada credentials at the Admiral). remoteControl.allowInvalidCertificates disables TLS validation on the tunnel. Lockouts are in memory. GET /proxy-api/v1/auth/challenge is not rate limited (challenges are one-time and expiring). The route policy used to block by exact string; R1c moved it to canonicalized route templates. Closed parts (F-30): default password refused, instance list authenticated, login and handshake rate limiting, forwarded headers ignored by default, Secure cookie option. Status 2026-10-04: remaining items owned by the proxy owner.
