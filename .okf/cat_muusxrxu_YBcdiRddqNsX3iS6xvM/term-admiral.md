---
type: "Reference"
title: "Admiral: the coordinator server process"
description: "Admiral = Armada.Server (ArmadaServer class): the single long-running process that owns the DB, REST/WS on 7890, MCP on 7891, dashboard, and all orchestration."
timestamp: "2026-10-05T05:28:33.5284080Z"
created: "2026-10-05T05:28:33.0182150Z"
slug: "term-admiral"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-admiral-startup-sequence", "arch-admiral-health-check-loop", "term-captain"]
version: "1"
salience: "0.5"
---
Term: Admiral. The coordinator process, implemented by src/Armada.Server (entry Program.cs, host class ArmadaServer). It owns the database and runs: REST API + dashboard + /ws WebSocket on AdmiralPort (default 7890, Watson 7), MCP server on McpPort (default 7891, Voltaic McpHttpServer), the Harbor link WebSocket (Harbor.LinkPath, default /v1.0/harbor/connect, on the REST port), the outbound remote tunnel, and the health-check loop that dispatches, monitors, recovers, and lands work. Domain orchestration lives in Armada.Core.Services.AdmiralService (IAdmiralService), which delegates to CaptainService, MissionService, VoyageService, DockService, EscalationService.

Relationships: the Admiral launches Captains (locally or via a Harbor), provisions Docks, tracks Missions/Voyages, records Events and Signals. The Helm CLI, TUI, dashboard, and MCP clients are all clients of the Admiral. The CLI can start an embedded Admiral in-process when none is reachable (Armada.Helm/Infrastructure/EmbeddedServer.cs).
Example: `armada server start` or `dotnet run --project src/Armada.Server`. See [[arch-admiral-startup-sequence]], [[arch-admiral-health-check-loop]].
