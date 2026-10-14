---
type: "Reference"
title: "Experimental surfaces: Harbor split mode and self-rebuild (ExperimentalSurface.cs)"
description: "ExperimentalSurface lists items outside the 1.0 promise: REST prefixes /api/v1/harbors, /api/v1/server/rebuild, /api/v1/server/rollback; MCP get/create/update/delete_harbor, set_harbor_enabled; settings harbor.*, deploymentMode, requireHarborForLaunch, rebuildSupervisorHarborId, rebuildSlotRetentionCount, selfVesselId; WS endpoint harbor-link."
timestamp: "2026-10-05T05:51:11.1767660Z"
created: "2026-10-05T05:51:10.7684750Z"
slug: "api-experimental-surfaces"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-frozen-surface-1-0", "arch-harbor-split-mode", "arch-server-rebuild", "decision-v1-not-ready-surfaces-d3"]
version: "1"
salience: "0.5"
---
Single list: src/Armada.Core/ApiSurface/ExperimentalSurface.cs (Marker "[Experimental] ", Tag "Experimental"; IsExperimentalRoute (prefix match incl. sub-paths), IsExperimentalTool, IsExperimentalSetting (key or key.* / key[] prefix), IsExperimentalWebSocketEndpoint). ArmadaServer.MarkExperimentalRoutes prefixes the OpenAPI summary with "[Experimental] " and adds the Experimental tag; experimental MCP tool descriptions start with "[Experimental] "; the surface JSON records "experimental": true and E2E.ApiContract ignores changes to them but verifies the markers. Counts at 1.0: 11 routes (Harbor management incl. enable/disable/probe, rebuild, rebuild/status, rollback), 5 MCP tools, 12 settings keys, 1 WebSocket endpoint (Harbor link at Harbor.LinkPath /v1.0/harbor/connect; protocol HarborProtocol 1.0). New surfaces may ship experimental by adding them here in the same change; graduating = remove from the list in a minor, drop banners, regenerate, CHANGELOG; a covered item never becomes experimental again within a major. Decision context: [[decision-v1-not-ready-surfaces-d3]].
