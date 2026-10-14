---
type: "Reference"
title: "REST authentication headers and authorization tiers (client view)"
description: "Send Authorization: Bearer <credential token> (recommended), X-Token: <session token from POST /api/v1/authenticate, 24h>, or X-Api-Key: <local API key, deprecated>; tiers NoAuthRequired, Authenticated, TenantAdmin, AdminOnly; 401 unauthenticated, 403 role, 404 cross-tenant, 429 locked out."
timestamp: "2026-10-05T05:48:20.5070870Z"
created: "2026-10-05T05:48:19.9752220Z"
slug: "api-rest-auth-headers"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["security-identities", "route-authorization-registry", "login-rate-limiting", "api-rest-error-response"]
version: "1"
salience: "0.5"
---
Client contract (docs/REST_API.md Authentication; enforcement and identity details are in the security category: [[security-identities]], [[route-authorization-registry]], [[login-rate-limiting]]).
- Authorization: Bearer <token>: 64-char credential token tied to tenant+user; returned in full only by POST /api/v1/credentials, masked (****last4) on reads; seeded "default" works until the admin password changes.
- X-Token: <session token>: AES-256-CBC self-contained token (tenant, user, expiry) from POST /api/v1/authenticate {Email, Password, TenantId}; 24-hour lifetime; used by the dashboard and TUI.
- X-Api-Key: <key>: deprecated; the local "ak_..." key in settings.json maps to the synthetic global admin (used by the CLI).
NoAuthRequired routes: GET /api/v1/status/health, POST /api/v1/authenticate, POST /api/v1/tenants/lookup, POST /api/v1/onboarding (self-registration, off by default). Other tiers: Authenticated (reads and own-resource writes; POST .../enumerate routes need only auth), TenantAdmin (operational writes: dispatch, captains, check runs, fleet actions, import...), AdminOnly (tenants, server stop/restart/rebuild/rollback/reset, backup/restore). Failures: 401 NotAuthorized, 403 Forbidden (includes the forced password change state), 404 for objects in another tenant, 429 SlowDown with Retry-After when the account/address is locked. GET /api/v1/whoami returns the caller identity; PUT /api/v1/account/password changes it. Thread-scoped Ask tokens are refused on REST.
