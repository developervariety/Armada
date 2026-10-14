---
type: "Reference"
title: "Proxy password policy, login limits, forwarded headers, secure cookie (F-30)"
description: "The proxy refuses to start with a blank or default (armadaadmin) password unless allowDefaultPassword; instance list needs a session; logins and tunnel handshakes rate limited (429); X-Forwarded-For ignored unless trustForwardedHeaders."
timestamp: "2026-10-05T05:35:27.6212400Z"
created: "2026-10-05T05:35:27.2836300Z"
slug: "proxy-password-and-rate-limits"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["proxy-route-policy", "proxy-operations", "open-o11-proxy-shared-identity"]
version: "1"
salience: "0.5"
---
F-30 fixed part of O-11: password from ArmadaProxy.password or ARMADA_PROXY_PASSWORD (env wins); blank or the built-in default "armadaadmin" refused at start unless allowDefaultPassword / ARMADA_PROXY_ALLOW_DEFAULT_PASSWORD (local test only, logs a warning); the Admiral logs a warning while remoteControl.password is still the default. GET /proxy-api/v1/instances needs a proxy session. Browser logins and tunnel handshakes are rate limited per client address (loginMaxFailures 10 within loginFailureWindowSeconds 900, lockout loginLockoutSeconds 900; 429 with Retry-After). X-Forwarded-For / Forwarded / X-Forwarded-Proto are ignored unless trustForwardedHeaders (enable only behind a proxy that overwrites them). secureCookie marks the session cookie Secure (enable when served over HTTPS). Authentication is a challenge-response: GET /proxy-api/v1/auth/challenge then POST /proxy-api/v1/auth/login; the tunnel proves the shared password with a SHA-256 challenge with timestamp and nonce plus optional enrollment token. docker/proxy/compose.yaml requires ARMADA_PROXY_PASSWORD.
