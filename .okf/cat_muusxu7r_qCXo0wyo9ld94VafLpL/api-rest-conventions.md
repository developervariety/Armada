---
type: "Reference"
title: "REST API conventions: base path, casing, OpenAPI, route registration"
description: "REST lives under /api/v1 on AdmiralPort 7890 (Watson 7); bodies are PascalCase JSON with string enums and nulls omitted (requests matched case-insensitively); /openapi.json and /swagger are canonical; each route declares auth in RouteAuthorizationRegistry."
timestamp: "2026-10-05T05:48:11.9379570Z"
created: "2026-10-05T05:48:10.9486180Z"
slug: "api-rest-conventions"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-rest-pagination", "api-rest-error-response", "api-rest-auth-headers", "route-authorization-registry", "api-frozen-surface-1-0"]
version: "1"
salience: "0.5"
---
Defined in src/Armada.Server/Routes/*Routes.cs (one class per area: Auth, Tenant, Status, Fleet, Vessel, VesselImport, VesselHealth, Voyage, Mission, Captain, Ask, Signal, Event, Dock, MergeQueue, Harbor, Job, FleetAction, Inbox, Skill, ProjectProfile, Playbook, PromptTemplate, Persona, Pipeline, WorkflowProfile, Environment, CheckRun, Release, Deployment, Incident, Runbook, Objective, ObjectiveRefinement, PlanningSession, History, RequestHistory, Memory, ModelEndpoint, Runtime (Mux helpers), TokenUsage, Workspace, Backup), registered in ArmadaServer.RegisterRoutes; documented in docs/REST_API.md (version 1.0.0) and frozen in docs/API_SURFACE_1.0.md (351 routes, 11 experimental).

Conventions: base URL http://localhost:7890, prefix /api/v1; PascalCase response/request property names (PageNumber, VesselId), enums as strings, null properties omitted; request bodies case-insensitive so camelCase input works (the dashboard camelizes REST responses). WebSocket and MCP use camelCase instead (MCP results keep PascalCase entity shapes). OpenAPI tags per area (Status, Fleets, Vessels, Missions, ..., Experimental); live OpenAPI at GET /openapi.json and Swagger UI at /swagger (public, O-10). Every route has an explicit requirement in Armada.Core/Authorization/RouteAuthorizationRegistry checked in PreRouting (undeclared -> AdminOnly). Common shapes: list routes paged ([[api-rest-pagination]]), errors as ApiErrorResponse ([[api-rest-error-response]]), POST /<entity>/delete/multiple batch deletes returning DeleteMultipleResult with skipped reasons, DELETE .../purge for hard deletes, GET /api/v1/status/health unauthenticated.
