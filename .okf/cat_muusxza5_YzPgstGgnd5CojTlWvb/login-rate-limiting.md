---
type: "Reference"
title: "Login rate limiting and lockout (loginRateLimit settings)"
description: "Per account 10 failures / 15 min and per address 50; lockout 15 min doubling to 24 h; 429 with Retry-After on /authenticate, credential-bearing REST/MCP, /tenants/lookup, /onboarding; in-memory."
timestamp: "2026-10-05T05:34:05.0324160Z"
created: "2026-10-05T05:34:04.7277870Z"
slug: "login-rate-limiting"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["password-hashing-pbkdf2", "open-o05-login-residuals", "proxy-password-and-rate-limits"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Settings/LoginRateLimitSettings.cs and Services/LoginRateLimiter.cs. Settings (loginRateLimit): enabled true; maxFailuresPerAccount 10 (1-1000; account = tenant + email, password logins); maxFailuresPerAddress 50 (1-100000; failed password, bearer, API key auths, tenant lookups, onboarding per client address); windowMinutes 15 (1-1440); lockoutMinutes 15 (1-1440); maxLockoutMinutes 1440 (1-10080). When a counter hits its limit the key is locked out: requests get 429 with Retry-After even with the correct password; each further lockout doubles up to the max; a successful password login clears the account counter. Applies to POST /api/v1/authenticate, credential-bearing REST and MCP requests, /api/v1/tenants/lookup, /api/v1/onboarding (F-26). Residual risks (O-05): counters are in memory and reset on restart; WebSocket upgrade and Harbor link are not limited; behind a reverse proxy all clients share one address (raise maxFailuresPerAddress). Tests: Services.LoginRateLimiter, E2E.LoginSecurity. The proxy has its own limiter.
