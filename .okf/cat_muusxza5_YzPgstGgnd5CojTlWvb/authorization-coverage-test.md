---
type: "Reference"
title: "Security verification suites (E2E.AuthorizationCoverage and friends)"
description: "E2E.AuthorizationCoverage proves registries match registered routes/tools; E2E.SecurityDefaults, E2E.SecretsAndAudit, E2E.McpTenantIsolation, E2E.LoginSecurity and Services.* suites cover each control."
timestamp: "2026-10-05T05:33:27.1342920Z"
created: "2026-10-05T05:33:26.6987160Z"
slug: "authorization-coverage-test"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["route-authorization-registry", "mcp-tool-authorization-registry", "test-suite-filter-armada-test-suites"]
version: "1"
salience: "0.5"
---
docs/SECURITY_REVIEW.md Verification:
- E2E.AuthorizationCoverage: every registered route and MCP tool is declared, nothing stale is declared, concrete paths resolve to their own template, undeclared paths fail closed, sensitive levels are pinned, unauthenticated server-control calls get 401.
- E2E.SecurityDefaults: loopback MCP without credentials works; invalid credentials 401; loopback opt-out; non-loopback MCP requires a credential; non-loopback start refused with default credentials and allowed with the override; forced password change flow including Bearer default retirement; system identity password login refused; admin-only MCP tools refused for the loopback caller.
- E2E.SecretsAndAudit: seeded secrets absent from responses, request history, and logs; workspace exec audited; audit events protected; tenant admin cannot take over a global admin.
- E2E.McpTenantIsolation (F-25), E2E.McpAuthScoping, E2E.CrossTenantApi, E2E.WebSocketSecurity.
- Services.SecurityHardening (runtime flags with auto-approve off, managed-path guard, secret key detection, redaction, default password detection, loopback hostname detection), Services.PasswordHashing, Services.LoginRateLimiter, E2E.LoginSecurity, Services.ProxySecurity, Services.CaptainAutoApproveOverride, Services.HarborLaunchSecurity, Services.AskThreadTokenPrecedence, Services.PathContainment.
Run all with ARMADA_TEST_SUITES=E2E.Auth,E2E.Security,E2E.Secrets,E2E.Mcp,E2E.Login,Services.Security.
