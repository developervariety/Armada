---
type: "Reference"
title: "Server control and high-risk routes are always AdminOnly or TenantAdmin (F-01, F-04, F-06)"
description: "server stop/restart/rebuild/rollback are AdminOnly always (requireAuthForShutdown deprecated); check-run writes with CommandOverride, harbor probe, and workspace exec are TenantAdmin and audited."
timestamp: "2026-10-05T05:37:37.0506920Z"
created: "2026-10-05T05:37:36.3682020Z"
slug: "server-control-routes-admin-only"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["route-authorization-registry", "audit-command-events", "server-rebuild-and-rollback"]
version: "1"
salience: "0.5"
---
F-01 (Critical): POST /api/v1/server/stop, restart, rebuild, rollback ran unauthenticated unless RequireAuthForShutdown was set; rebuild accepts a SourcePath and runs dotnet publish and npm run build from it, so it was unauthenticated RCE wherever the REST port was reachable (including through the proxy, which only blocked server/stop). Now always AdminOnly, enforced centrally; requireAuthForShutdown is deprecated and ignored; the CLI sends the local API key. F-04 (High): POST /api/v1/check-runs with CommandOverride and retry let any authenticated user run shell commands on the host; now every check-run write (REST and MCP run_check / retry_check_run) is TenantAdmin with audit records. F-06 (High): POST /api/v1/harbors/{id}/probe runs any executable on the Harbor host; now TenantAdmin plus audit. POST /api/v1/workspace/vessels/{vesselId}/exec is declared TenantAdmin. Rule of thumb for new routes: anything that executes a process or changes the server is at least TenantAdmin, emits audit.command, and gets a pinned level in E2E.AuthorizationCoverage.
