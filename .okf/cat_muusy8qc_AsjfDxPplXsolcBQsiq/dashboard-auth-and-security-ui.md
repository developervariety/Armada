---
type: "Reference"
title: "Dashboard auth flow and security UI (login, banner, password change, scoping)"
description: "LoginFlow does email, tenant lookup, password (or token); AuthContext stores the session token and handles 401; DefaultCredentialsBanner and PasswordChangeRequired react to whoami flags; lib/scoping.ts decides who may edit what."
timestamp: "2026-10-05T05:42:13.3834880Z"
created: "2026-10-05T05:42:13.1417720Z"
slug: "dashboard-auth-and-security-ui"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["password-change-required-flag", "default-credentials-bind-guard", "dashboard-api-client", "permission-levels-and-bypass"]
version: "1"
salience: "0.5"
---
components/LoginFlow.tsx: email, POST /api/v1/tenants/lookup, tenant picker when several, password (POST /api/v1/authenticate) or token login; language selector on the login screen. context/AuthContext.tsx keeps the session token (localStorage armada_session_token), calls setAuthToken and setOnUnauthorized (401 returns to login), and exposes whoami flags (IsAdmin, IsTenantAdmin, PasswordChangeRequired). components/PasswordChangeRequired.tsx prompts admin@armada sessions still on the default password (PUT /api/v1/account/password); components/DefaultCredentialsBanner.tsx shows a persistent warning to admins and tenant admins while defaults are in use. ProtectedRoute guards routes; AdminBadge shows role. lib/scoping.ts implements who may edit Configuration items (regular users only their own personal items) and is mirrored by the TUI's ScopeRules. Credentials page shows a new token once. The server enforces everything; the UI only hides what the server would refuse.
