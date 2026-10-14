---
type: "Reference"
title: "REST pagination: GET query or POST /enumerate, EnumerationResult shape, unpaged exceptions"
description: "List via GET ?pageNumber&pageSize&order&createdAfter&createdBefore&<filters> or POST /api/v1/<entity>/enumerate with a JSON body (query string overrides body); pageSize default 10, max 1000; result {Success, PageNumber, PageSize, TotalPages, TotalRecords, Objects, TotalMs}; some routes return arrays."
timestamp: "2026-10-05T05:48:28.5276230Z"
created: "2026-10-05T05:48:27.9781340Z"
slug: "api-rest-pagination"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["data-enumeration-query", "api-mcp-enumerate", "api-rest-conventions"]
version: "1"
salience: "0.5"
---
docs/REST_API.md "Pagination"; model [[data-enumeration-query]].
Parameters: pageNumber (1-based), pageSize (default 10, clamped 1-1000), order (CreatedAscending | CreatedDescending default), createdAfter / createdBefore (ISO 8601). Filters: status (missions, voyages, captains), fleetId (vessels), vesselId / captainId (missions, docks, events), voyageId (missions, events), missionId (events), type or eventType (events), signalType / toCaptainId / unreadOnly (signals), userId (admin-only narrowing of Category A rows; ignored for regular users), tenantId (global-admin narrowing).
Frozen unpaged exceptions (do not expect EnumerationResult): JSON arrays from GET /api/v1/harbors, /model-endpoints, /inbox, /signals/recent, /signals/recipient/{captainId}, /planning-sessions, /objectives/{id}/refinement-sessions, /backlog/{id}/refinement-sessions, /ask/quick-actions, /releases/{id}/github/pull-requests, /doctor; GET /api/v1/jobs -> {Success, Objects, TotalRecords}; POST /ask/threads/{id}/messages/enumerate -> AskMessagePage {Messages, HasMore} (cursor BeforeSequence); GET /workspace/vessels/{id}/search -> WorkspaceSearchResult; captain/mission logs -> {Log, Lines, TotalLines, ...} with line offsets (lines, offset). Vessel health has its own POST /api/v1/vessel-health/enumerate with richer sort/filter.
