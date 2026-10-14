---
type: "Project"
title: "Solution layout: projects and their dependency graph"
description: "src/Armada.sln: Core (models, DB, services, settings) <- Runtimes <- Server (Admiral) <- Helm (CLI, also hosts Tui); Client (typed SDK) <- Tui; Harbor (Avalonia runner) uses Core+Runtimes; Proxy, Publisher, PerfSeed, Dashboard (React), Test.Shared/Automated/Xunit/Nunit."
timestamp: "2026-10-05T05:38:17.4555450Z"
created: "2026-10-05T05:38:16.9433620Z"
slug: "arch-solution-projects"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-admiral", "arch-helm-cli-client", "arch-armada-client-sdk"]
version: "1"
salience: "0.5"
---
All C# projects target net8.0;net10.0 (src/Directory.Build.props). ProjectReference graph (from the .csproj files):
- Armada.Core: domain models, enums, Protocol (typed runtime streams), Harbor wire messages, database drivers (Sqlite/Postgresql/Mysql/SqlServer) and I*Methods interfaces, services (AdmiralService, MissionService, DockService, MergeQueueService, health, Ask, fleet actions...), settings, authorization registries, ApiSurface, hosting/service registration, ArmadaApiClient. No project deps.
- Armada.Runtimes -> Core: IAgentRuntime implementations, AgentRuntimeFactory, ApiAgentRuntime tools, MCP client, Harbor job runner.
- Armada.Server -> Core, Runtimes: the Admiral (ArmadaServer, Program), REST routes, MCP tools, WebSocket hub, Ask coordinators, landing/lifecycle handlers, planning/refinement coordinators, rebuild, remote tunnel.
- Armada.Helm -> Core, Server, Tui: `armada` CLI (Spectre.Console); references Server to run an embedded Admiral; hosts `armada tui`.
- Armada.Client -> Core: typed REST + WebSocket SDK mirroring the dashboard's api/client.ts; used by Armada.Tui.
- Armada.Tui -> Client: terminal UI library (TUIKit).
- Armada.Harbor -> Core, Runtimes: Avalonia host-runner app.
- Armada.Proxy -> Core: remote access portal/relay (ArmadaProxyServer).
- Armada.Publisher: packaging orchestrator driven by publisher.json (channels, checksums). Armada.PerfSeed -> Core: seeds a throwaway data dir for performance baselines.
- Armada.Dashboard: React/TypeScript SPA (committed dist/), served by the Admiral and the proxy.
- Tests: Test.Shared (suites + infrastructure, references everything) run by Test.Automated (console), Test.Xunit, Test.Nunit adapters.
