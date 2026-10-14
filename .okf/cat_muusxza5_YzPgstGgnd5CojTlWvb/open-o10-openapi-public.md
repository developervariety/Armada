---
type: "Project"
title: "O-10 (Low, accepted): /openapi.json and /swagger are public"
description: "The OpenAPI document and Swagger UI are NoAuthRequired; accepted because they are documentation only with no data."
timestamp: "2026-10-05T05:36:40.6532120Z"
created: "2026-10-05T05:36:40.4823760Z"
slug: "open-o10-openapi-public"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["route-authorization-registry", "security-review-living-document"]
version: "1"
salience: "0.5"
---
/openapi.json and /swagger on the Admiral port are declared NoAuthRequired in RouteAuthorizationRegistry (they are the two non-API declarations among the ~350). Accepted risk: they expose only the API description, not data. The TUI's API Explorer and the dashboard's API Explorer read /openapi.json. Status 2026-10-04: accepted.
