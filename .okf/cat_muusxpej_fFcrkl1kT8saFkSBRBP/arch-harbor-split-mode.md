---
type: "Project"
title: "Harbor split mode internals: link endpoint, connection manager, router"
description: "HarborLinkEndpoint (WebSocket on Harbor.LinkPath, REST port) authenticates the upgrade, reads the handshake, registers with HarborConnectionManager, acks with the MCP URL; HarborRouter picks Harbors by dock affinity > preferred > least-loaded eligible; RemoteHostCommandExecutor delegates git/commands."
timestamp: "2026-10-05T05:41:33.8475340Z"
created: "2026-10-05T05:41:32.6351680Z"
slug: "arch-harbor-split-mode"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-harbor", "arch-harbor-link-protocol", "rt-remote-harbor-runtime"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Server/HarborLinkEndpoint.cs, HarborLinkIdentity.cs; src/Armada.Core/Services/HarborConnectionManager.cs, HarborConnection.cs, HarborRouter.cs (HarborRoutingRequest/Decision), HarborService.cs, WebSocketHarborTransport.cs, HarborLinkClient.cs (Harbor side), RemoteHostCommandExecutor.cs / LocalHostCommandExecutor.cs (IHostCommandExecutor); settings HarborServerSettings (LinkPath /v1.0/harbor/connect, RequireAuth, HeartbeatIntervalSeconds 15, HeartbeatTimeoutSeconds 45, DefaultMaxJobsPerHarbor 4, AdvertisedMcpBaseUrl).

Link: Harbor dials out; upgrade authenticated via AuthenticationService (a credential is mandatory unless the Admiral is loopback-bound and the Harbor local); session re-validated for the link's life; handshake registers/self-registers the Harbor (tenant/user of the credential); handshakeAck carries mcpBaseUrl. Router (decided once when a dock is provisioned, then pinned): (1) Harbor owning the mission's dock, (2) eligible vessel.PreferredHarborId, (3) least loaded eligible (headroom, then name). Eligible = enabled, connected, under capacity, advertises the requested runtime and every vessel.RequiredCapabilities. Otherwise hold with a reason (no connected Harbor / none capable / all at capacity). Users of the executors: captain launches ([[rt-remote-harbor-runtime]]), fleet Command actions on vessels with a preferred Harbor, Harbor probes (POST /api/v1/harbors/{id}/probe), captain tool discovery, rebuild cutover. Disconnected Harbor: its missions' host operations wait; jobs keep running and are rebound on reconnect via heartbeat liveJobIds. Experimental in 1.0; scripts/common/e2e-split-mode.sh exercises it.
