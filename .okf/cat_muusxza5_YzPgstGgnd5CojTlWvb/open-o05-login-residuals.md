---
type: "Project"
title: "O-05 (Medium, closed with residuals): login limiter gaps"
description: "PBKDF2 and rate limiting fixed it (F-26), but lockouts are in memory, the WebSocket upgrade and Harbor link are not limited, reverse proxies collapse client addresses, and old Admirals cannot read new hashes."
timestamp: "2026-10-05T05:36:15.5746400Z"
created: "2026-10-05T05:36:14.6879900Z"
slug: "open-o05-login-residuals"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["login-rate-limiting", "password-hashing-pbkdf2"]
version: "1"
salience: "0.5"
---
Original finding: unsalted SHA-256 passwords and no rate limiting. Closed in W1.9 by F-26 (PBKDF2-HMAC-SHA256 600k, loginRateLimit). Residuals (owner: W1 follow-up), status 2026-10-04: lockout counters live in memory and reset on Admiral restart; the WebSocket /ws upgrade and the Harbor link are not rate limited; behind a reverse proxy every client shares one address (operators should raise loginRateLimit.maxFailuresPerAddress); an upgraded database cannot be used for password login by an older Admiral (documented in UPGRADING.md).
