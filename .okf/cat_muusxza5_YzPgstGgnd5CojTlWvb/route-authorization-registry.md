---
type: "Reference"
title: "REST authorization: RouteAuthorizationRegistry checked centrally in PreRouting"
description: "Every REST route declares Resource:Operation and a permission level in Armada.Core/Authorization/RouteAuthorizationRegistry.cs; ArmadaServer checks it in Watson PreRouting before any handler; undeclared routes fail closed as AdminOnly."
timestamp: "2026-10-05T05:33:06.5366890Z"
created: "2026-10-05T05:33:06.1995140Z"
slug: "route-authorization-registry"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["authorization-coverage-test", "permission-levels-and-bypass", "mcp-tool-authorization-registry", "api-surface-generator-and-contract-test"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Authorization/RouteAuthorizationRegistry.cs holds one line per HTTP method and route template (about 350 declarations: 348 API routes plus /openapi.json and /swagger). Each has an AuthorizationRequirement: resource type, ResourceOperationEnum (Read, Create, Update, Delete, Execute, Admin) and PermissionLevel (NoAuthRequired, Authenticated, TenantAdmin, AdminOnly). ArmadaServer sets _App.Routes.PreRouting (ArmadaServer.cs) to resolve the request to the exact template Watson will dispatch to, look it up with RouteAuthorizationRegistry.TryGetByTemplate, authenticate once, and reject before any handler runs; handlers reuse that authentication, re-check the registry by concrete path, and apply tenant and owner scoping to the data (AuthorizationService in Armada.Core/Services). Fail closed: a route without a declaration is treated as AdminOnly and logged at startup and at use. Adding a REST route therefore requires adding its registry line (and updating docs/SECURITY_REVIEW.md tables and the API surface baseline), or E2E.AuthorizationCoverage fails.
