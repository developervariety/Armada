---
type: "Reference"
title: "Harbor: host-side runner for split mode (hbr_)"
description: "Harbor (hbr_) = Avalonia app on a developer machine that dials the Admiral and runs agent CLIs, git, and worktrees there; split mode is experimental in 1.0."
timestamp: "2026-10-05T05:30:31.8418840Z"
created: "2026-10-05T05:30:30.7874320Z"
slug: "term-harbor"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-harbor-split-mode", "arch-harbor-link-protocol", "term-dock"]
version: "1"
salience: "0.5"
---
Term: Harbor (model src/Armada.Core/Models/Harbor.cs, prefix hbr_; app src/Armada.Harbor). A detached host runner: the Admiral keeps DB, APIs, dashboard, orchestration; the Harbor runs on the machine where repos and tool logins live and executes host work (launch/stdin/kill captain processes, git/gh commands, deferred relaunch for rebuild cutover). The Harbor is the client: it dials outward over WebSocket to Harbor.LinkPath (default /v1.0/harbor/connect) on the Admiral REST port, so it works behind NAT.

Modes (ArmadaSettings.DeploymentMode, DeploymentModeEnum): Local (default; no Harbor, captains run in-process) and Split (experimental in 1.0; Harbor routes, *_harbor MCP tools, harbor.* / deploymentMode / requireHarborForLaunch settings excluded from the compatibility promise).
Fields reported by the link (ConnectionStatus HarborConnectionStatusEnum Unknown/Connected/Degraded/Disconnected, LastSeenUtc, ProtocolVersion, OsPlatform, Architecture) vs operator-editable (Name, MaxConcurrentJobs default 4, Enabled). Harbors self-register on first handshake; disabling stops new routing but keeps docks. Harbor app settings: ~/.armada-harbor/settings.json (serverLinkUrl default ws://127.0.0.1:7890/v1.0/harbor/connect per HarborAppSettings.cs; docs saying 7891 are wrong), accessKey = an Armada credential (required from another host). See [[arch-harbor-split-mode]], [[arch-harbor-link-protocol]].
