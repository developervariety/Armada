---
type: "Project"
title: "Armada.Client: typed .NET SDK mirroring the dashboard API client"
description: "Armada.Client.ArmadaClient exposes one async method per server call in the dashboard's api/client.ts (PascalCase + Async, partial files per area), raises ArmadaApiException on errors (Unauthorized event first on 401), plus ArmadaSocket for the /ws stream; used by the TUI."
timestamp: "2026-10-05T05:44:01.6084810Z"
created: "2026-10-05T05:44:01.1598360Z"
slug: "arch-armada-client-sdk"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-helm-cli-client", "decision-tui-plan-d1-d5", "arch-solution-projects"]
version: "1"
salience: "0.5"
---
src/Armada.Client: ArmadaClient.cs plus partials ArmadaClient.<Area>.cs (Missions, Voyages, Vessels, Captains, Fleets, Ask, Harbors, FleetActions, VesselHealth, VesselImport, Deployments, Releases, Objectives, PlanningSessions, Memories, MergeQueue, ModelEndpoints, Jobs, Workspace, Credentials, Users, Tenants, Auth, Status, Server, History, RequestHistory, ...), ArmadaClientOptions, ArmadaRequestOptions, ArmadaPaging/PageWindow, ArmadaJson (PascalCase on the wire), ArmadaApiException, ArmadaRequestCompletedEventArgs, Http/ArmadaQueryString, Models/* (request/response DTOs not in Core), Socket/* (ArmadaSocket, ClientWebSocketTransport, IArmadaSocketTransport, SubscribeMessage/SubscribeAllMessage, typed events: EntityChangedEvent, AskTurn/Delta/Tool/Message/Proposal/Work/ThreadEvent, PlanningSessionEvent, RefinementSessionEvent, ArmadaEventTypes).

Thread-safe for concurrent requests; SetToken may be called any time (in-flight requests keep their credential). Decision D2 of the TUI plan: generated from the dashboard client/OpenAPI and hand-finished; Helm is meant to migrate to it over time (Helm still uses Core's ArmadaApiClient today). scripts/tui/generate-client-methods.py and client_exports.py support the parity work; tests use FakeSocketTransport.
