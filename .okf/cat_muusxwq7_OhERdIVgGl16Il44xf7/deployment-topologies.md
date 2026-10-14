---
type: "Reference"
title: "Deployment topologies: single box, Docker, split with Harbor, remote via proxy"
description: "Default is single-box Local mode (Admiral + captains as child processes in ~/.armada/docks); Docker runs ApiEndpoint captains only; CLI captains from a container need split mode; proxy gives NAT-traversing remote access."
timestamp: "2026-10-05T05:32:50.8656030Z"
created: "2026-10-05T05:32:50.6209270Z"
slug: "deployment-topologies"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["docker-compose-stack", "harbor-setup-split-mode", "proxy-operations", "service-registration-flags"]
version: "1"
salience: "0.5"
---
docs/OPERATIONS.md: the shapes differ by where agent CLIs run, because a captain needs the code, git credentials, and the agent's own login on the machine that launches it.
1. Single box (deploymentMode Local, default, recommended): Admiral, dashboard, captains on one machine; captains are child processes in git worktrees under ~/.armada/docks. Keep it running with --install-service or the RUN_ON_STARTUP scripts.
2. Docker (docker/armada/compose.yaml): Admiral + standalone dashboard + observability; binds 0.0.0.0; image has git but no agent CLIs, so only ApiEndpoint captains (tool loop runs inside the Admiral) work in Local mode.
3. Split mode with Harbors (experimental): containerized or remote Admiral, Harbors on developer machines dial out over WebSocket (/v1.0/harbor/connect) and run git, worktrees, and captain launches locally.
4. Remote access through Armada.Proxy: the Admiral opens an outbound tunnel to the proxy (port 7893), which serves a portal and the relayed dashboard with some admin routes blocked; no inbound ports needed.
