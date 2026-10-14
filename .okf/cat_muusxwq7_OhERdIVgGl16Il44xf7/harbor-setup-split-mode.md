---
type: "Reference"
title: "Harbor setup and split mode operation (experimental)"
description: "Harbor (src/Armada.Harbor, Avalonia tray app) reads ~/.armada-harbor/settings.json (serverLinkUrl default ws://127.0.0.1:7890/v1.0/harbor/connect), dials out and self-registers; split mode sets deploymentMode Split and requireHarborForLaunch true."
timestamp: "2026-10-05T05:32:21.3479620Z"
created: "2026-10-05T05:32:20.9547480Z"
slug: "harbor-setup-split-mode"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["harbor-link-authentication", "open-o04-harbor-mcp-credential", "split-mode-e2e-script", "service-registration-flags"]
version: "1"
salience: "0.5"
---
Run: `dotnet run --project src/Armada.Harbor` (or installers). First run writes ~/.armada-harbor/settings.json (%USERPROFILE%\.armada-harbor on Windows): serverLinkUrl (code default ws://127.0.0.1:7890/v1.0/harbor/connect in HarborAppSettings.cs; the README table saying 7891 is stale), dashboardUrl http://127.0.0.1:7890/dashboard, harborId (hbr_, generated), name (machine name), capabilities (runtimes and tools, e.g. git, claude), maxConcurrentJobs 4, heartbeatIntervalMs 15000, accessKey/secret (an Armada credential; empty for an unauthenticated local link). The Harbor dials out and self-registers on its first handshake; manage via /api/v1/harbors and MCP (get/create/update/delete_harbor, set_harbor_enabled, enumerate harbors).
Split mode (experimental for 1.0, outside the compatibility promise): Admiral detached (docker/armada/compose.split.yaml, armada.split.json) with deploymentMode Split and requireHarborForLaunch true, so missions wait for a connected eligible Harbor owned by the mission's user. Captains call home to harbor.advertisedMcpBaseUrl (must be reachable from the Harbor host; split compose uses http://127.0.0.1:7891/mcp, which only works when the Harbor is on the Docker host). harbor.requireAuth defaults false, so do not expose a split Admiral to untrusted networks. Pending forever usually means no eligible Harbor or heartbeats missing past harbor.heartbeatTimeoutSeconds (45). Split-mode captains get no MCP credential (O-04).
