# Request History and API Explorer Plan

Last updated: 2026-05-01

This document tracks the planned integration of Verbex-style `API Explorer` and `Request History` capabilities into Armada. It is intentionally actionable: work items are explicit, concrete file targets are called out, and the checklist is meant to be annotated as implementation progresses.

## Product Goal

Armada should expose a first-class, visually polished API exploration and request-audit experience inside the React dashboard. The goal is not to embed Swagger and call it done. The goal is to ship:

- a live `API Explorer` that can browse Armada's OpenAPI document, build requests, execute them, inspect responses, and replay recent requests
- a persistent `Request History` surface that records Armada REST traffic, summarizes activity, filters and inspects requests, and lets a user replay a captured request back into the explorer
- an Armada-native UI that matches the dashboard's existing typography, spacing, cards, pills, tables, modals, and chart language instead of copying Verbex styling verbatim

## Implementation Status

Status as of 2026-05-01:

- [x] Armada now ships a first-class `Requests` page in the React dashboard
- [x] Armada now ships a first-class `API Explorer` page in the React dashboard
- [x] Request history is persisted in the server database across SQLite, SQL Server, PostgreSQL, and MySQL
- [x] Request history can replay captured requests directly into API Explorer
- [x] Dashboard nav is updated to place `Requests` and `API Explorer` under `System`, with `Tenants`, `Users`, and `Credentials` under `Security`

Follow-up still worth tracking after this implementation:

- [ ] Audit and enrich older REST route OpenAPI summaries/descriptions where the explorer experience is still thin
- [ ] Improve route-template and path-parameter capture when Watson exposes a cleaner post-routing hook for matched route metadata

## Sources Reviewed

The plan below is based on the current Verbex implementation and the current Armada dashboard/server architecture.

### Verbex sources reviewed

- `C:\code\verbex\Verbex\dashboard\src\components\ApiExplorerView.jsx`
- `C:\code\verbex\Verbex\dashboard\src\components\ApiExplorerView.css`
- `C:\code\verbex\Verbex\dashboard\src\components\RequestHistoryView.jsx`
- `C:\code\verbex\Verbex\dashboard\src\components\RequestHistoryView.css`
- `C:\code\verbex\Verbex\src\Verbex.Server\API\REST\RestServiceHandler.cs`
- `C:\code\verbex\Verbex\src\Verbex\Database\RequestHistorySchema.cs`

### Armada surfaces reviewed

- `src/Armada.Server/ArmadaServer.cs`
- `src/Armada.Server/Routes/*.cs`
- `src/Armada.Core/Database/DatabaseDriver.cs`
- `src/Armada.Dashboard/src/App.tsx`
- `src/Armada.Dashboard/src/components/Layout.tsx`
- `src/Armada.Dashboard/src/App.css`
- `src/Armada.Dashboard/src/pages/Dashboard.tsx`
- `src/Armada.Dashboard/src/pages/Events.tsx`
- `src/Armada.Dashboard/src/pages/Notifications.tsx`
- `src/Armada.Dashboard/src/pages/Workspace.tsx`

## Capability Target

Armada should reach functional parity with the useful parts of the Verbex implementation, while presenting them through Armada's own UI language.

### API Explorer parity target

- [x] Load Armada's live OpenAPI document from `/openapi.json`
- [x] Offer a one-click link to raw OpenAPI and Swagger
- [x] Show a searchable, filterable operation catalog grouped by tag and method
- [x] Build request forms from OpenAPI parameters and request-body schema
- [x] Prefill path, query, header, and body values using schema defaults/examples where available
- [x] Execute authenticated API requests against the active Armada server
- [x] Abort in-flight requests
- [x] Render response views for preview, body, headers, status, and code snippets
- [x] Generate code snippets for at least `curl`, `fetch`, and `csharp`
- [x] Preserve a local browser-side recent-request list for quick replay
- [x] Support replay from both explorer-local history and persisted request-history entries

### Request History parity target

- [x] Persist Armada REST request history in the server database
- [x] Show top-level summary cards for total volume, success rate, failures, and average duration
- [x] Show a time-range activity chart with bucketed counts and success/failure breakdown
- [x] Support server-side filters for method, route, status, user scope, tenant scope, and time range
- [x] Show a paginated request table with core columns and quick actions
- [x] Show full request detail in an Armada-native modal or drawer
- [x] Support delete-single and bulk-delete flows
- [x] Support retention, redaction, truncation, and route exclusions
- [x] Support opening a stored request directly in `API Explorer`

## Current Status

This capability is implemented in Armada, with a few follow-up improvements still worth tracking.

- [x] Armada exposes OpenAPI and Swagger through Watson OpenAPI
- [x] Armada has first-class `API Explorer` and `Requests` pages in the React dashboard
- [x] Armada persists request-history storage in the database and exposes it through REST
- [x] Armada has request-history models, settings, capture logic, routes, dashboard pages, and replay handoff
- [ ] Armada still needs a dedicated post-implementation audit of older route metadata and deeper route-template/path-param capture

## Recommended Product Decisions

These decisions should be treated as the default implementation plan unless a later change explicitly supersedes them.

- Feature names:
  - `API Explorer`
  - `Request History`
- Sidebar labels:
  - `API Explorer`
  - `Requests`
- Primary UI: React dashboard only
- Navigation model: flat entries, no nested special case
- Recommended nav placement:
  - `SYSTEM` section with `Requests` and `API Explorer`
- Canonical dashboard routes:
  - `/api-explorer`
  - `/api-explorer/:operationId`
  - `/requests`
  - `/requests/:id`
- API Explorer data source:
  - Armada's live `/openapi.json`
- Request history persistence:
  - database-backed, server-side
- Request history default scope:
  - authenticated users see their own request history
  - tenant admins see tenant-scoped request history
  - global admins can query across tenants
- Request replay:
  - `Request History` should deep-link or hand state into `API Explorer`
- Visual direction:
  - use Armada's existing neutral card/table aesthetic
  - do not import Verbex gradients, shadows, or color variables directly
  - use existing Armada badges, tabs, page headers, modal framing, and chart language as the starting point

## Recommended Information Architecture

The clearest Armada-native placement is to treat these as first-class system tools inside Armada's existing operational shell rather than as generic activity feeds.

### Recommended sidebar structure

- Dashboard
- Operations
  - Planning
  - Dispatch
  - Voyages
  - Missions
  - Merge Queue
- Fleet
  - Fleets
  - Workspace
  - Vessels
  - Captains
  - Docks
- Activity
  - Signals
  - Events
  - Notifications
- System
  - Personas
  - Pipelines
  - Templates
  - Playbooks
  - Requests
  - API Explorer
  - Server
  - Doctor
- Security
  - Tenants
  - Users
  - Credentials

### Navigation rules

- `Requests` and `API Explorer` should be siblings in `System`
- the `Requests` page is the request-history surface
- `Requests` should link into `API Explorer` via a replay action
- `API Explorer` should expose a link back to the `Requests` page and show recent server-captured requests when available

## Armada-Native UX Direction

The request from the product side is not just capability parity. It is parity without losing Armada's design identity.

### Styling rules

- [x] Reuse Armada classes and visual primitives from `src/Armada.Dashboard/src/App.css` wherever practical
- [x] Use `page-header`, `page-actions`, existing button styles, card surfaces, table wraps, and modal shells
- [x] Reuse the `MissionHistoryChart` visual language for the request-activity chart instead of copying Verbex chart CSS directly
- [x] Use subtle method pills and status pills that fit Armada's current palette
- [x] Avoid full-surface gradients in panels, tab bars, or cards
- [x] Keep typography compact and readable, matching `Workspace` and `Events`
- [x] Ensure both pages work well on laptop screens without feeling cramped

### API Explorer layout

Recommended shell:

- left column:
  - operation search
  - tag filter
  - method/tag grouped operation list
  - recent requests
- main column:
  - request header with method badge, route, summary, execute/abort actions
  - parameter sections
  - request body editor
  - response area with tabs
  - code snippet tabs

UI notes:

- the operation list should feel closer to Armada `Workspace` and `Events` than to Swagger
- request/response tabs should use Armada's compact tab language
- code and response blocks should use the existing mono stack and neutral code surfaces

### Request History layout

Recommended shell:

- header row:
  - title
  - concise subtitle
  - refresh
  - bulk delete when filtered selection exists
- summary row:
  - total requests
  - success rate
  - failures
  - average duration
- activity section:
  - time-range tabs
  - optional route or method selector
  - bucketed chart
- filters section:
  - compact filter grid
  - clear/reset action
- results section:
  - paginated table
  - quick actions: view, replay, delete
- detail surface:
  - modal or drawer with request line, auth scope, headers, params, bodies, truncation notes, and response metadata

UI notes:

- treat request history as a polished operational console, not a raw log dump
- the detail experience should make large payloads readable
- replay should be obvious but not dominate the page

## Backend Architecture

### OpenAPI and Explorer

Armada already exposes OpenAPI through Watson, so the explorer can be built against the existing server output. The missing work is the dashboard experience, not schema generation from scratch.

- [ ] Audit current route metadata quality and fill gaps where summaries, descriptions, schemas, or tags are too sparse for a good explorer experience
- [x] Ensure all important REST routes have consistent `WithTag`, `WithSummary`, `WithDescription`, request-body, and response metadata
- [x] Add any missing OpenAPI schema hints needed for good parameter/body form generation

### Recommended server-side request-history components

- [x] `src/Armada.Core/Models/RequestHistoryEntry.cs`
- [x] `src/Armada.Core/Models/RequestHistoryDetail.cs`
- [x] `src/Armada.Core/Models/RequestHistoryRecord.cs`
- [x] `src/Armada.Core/Models/RequestHistorySummaryResult.cs`
- [x] `src/Armada.Core/Models/RequestHistorySummaryBucket.cs`
- [x] `src/Armada.Core/Models/RequestHistoryQuery.cs`
- [x] `src/Armada.Core/Models/RequestHistoryCaptureInput.cs`
- [x] `src/Armada.Core/Database/Interfaces/IRequestHistoryMethods.cs`
- [x] `src/Armada.Core/Services/RequestHistoryCaptureService.cs`
- [x] `src/Armada.Server/Routes/RequestHistoryRoutes.cs`

### Recommended database integration

- [x] Add `RequestHistory` access to `src/Armada.Core/Database/DatabaseDriver.cs`
- [x] Add `IRequestHistoryMethods` implementations for:
  - `src/Armada.Core/Database/Sqlite`
  - `src/Armada.Core/Database/SqlServer`
  - `src/Armada.Core/Database/Postgresql`
  - `src/Armada.Core/Database/Mysql`
- [x] Add schema creation/migration for:
  - `request_history`
  - `request_history_detail`
- [x] Add indexes for:
  - created time
  - method + created time
  - status code + created time
  - tenant + created time
  - user + created time
  - credential + created time
  - success/failure summary windows

### Recommended request-history data model

The summary table and detail table should be split, similar to Verbex.

Recommended `request_history` columns:

- `id`
- `created_utc`
- `method`
- `route_template`
- `route_path`
- `query_string`
- `status_code`
- `duration_ms`
- `request_size_bytes`
- `response_size_bytes`
- `content_type`
- `is_success`
- `tenant_id`
- `user_id`
- `credential_id`
- `principal_display`
- `client_ip`
- `correlation_id`

Recommended `request_history_detail` columns:

- `request_history_id`
- `path_params_json`
- `query_params_json`
- `request_headers_json`
- `response_headers_json`
- `request_body_text`
- `response_body_text`
- `request_body_truncated`
- `response_body_truncated`

## Request Capture Design

Armada currently logs requests in `ArmadaServer` post-routing, but it does not persist request history. The capture service should hook into the same request lifecycle.

- [x] Add a capture context at request start
- [x] Record request method, route, raw path, query, timestamps, and auth scope
- [x] Safely buffer request body only when content type and size policy allow it
- [x] Capture response status, headers, and response body preview without destabilizing route handlers
- [x] Persist request history after the response finishes
- [x] Avoid recording Armada's own health checks if they create noise
- [x] Keep login/auth routes captured, with redaction and truncation applied instead of excluding them

### Redaction and privacy requirements

- [x] Always redact `Authorization`, cookies, session tokens, API keys, and similar secrets
- [x] Redact credential values in both headers and bodies when recognizable
- [x] Truncate large bodies with explicit truncation markers
- [x] Do not persist binary bodies as raw text
- [x] Add a route exclusion list for noisy or sensitive endpoints
- [x] Add settings to disable request-history capture entirely

### Settings surface

Recommended settings additions in `ArmadaSettings`:

- [x] `RequestHistoryEnabled`
- [x] `RequestHistoryRetentionDays`
- [x] `RequestHistoryMaxBodyBytes`
- [x] `RequestHistoryExcludeRoutes`
- [x] `RequestHistoryCaptureRequestHeaders`
- [x] `RequestHistoryCaptureResponseHeaders`

## REST Contract

Armada should expose its own request-history API in Armada style. Do not mirror Verbex path names blindly where Armada already has a route naming pattern.

### Recommended routes

- [x] `GET /api/v1/request-history`
  - paginated list with filters
- [x] `GET /api/v1/request-history/summary`
  - aggregate cards plus bucketed chart data
- [x] `GET /api/v1/request-history/{id}`
  - entry summary plus detail payloads
- [x] `DELETE /api/v1/request-history/{id}`
  - delete one entry
- [x] `POST /api/v1/request-history/delete/multiple`
  - delete a set of selected entries
- [x] `POST /api/v1/request-history/delete/by-filter`
  - optional follow-on route for bulk delete of current filtered scope

### Filter contract

Recommended first-pass filters:

- [x] `method`
- [x] `statusCode`
- [x] `route`
- [x] `fromUtc`
- [x] `toUtc`
- [x] `tenantId`
- [x] `userId`
- [x] `credentialId`
- [x] `principal`
- [x] `successOnly`

## Dashboard Integration

### Routes and nav

- [x] Add routes in `src/Armada.Dashboard/src/App.tsx`
- [x] Add nav entries in `src/Armada.Dashboard/src/components/Layout.tsx`
- [x] Add `Requests` and `API Explorer` to the `System` section
- [x] Move `Tenants`, `Users`, and `Credentials` under a `Security` section
- [x] Keep the nav flat, consistent with the current Armada direction

### API client and types

- [x] Add client methods in `src/Armada.Dashboard/src/api/client.ts`
- [x] Add TypeScript models in `src/Armada.Dashboard/src/types/models.ts`
- [x] Add helpers for OpenAPI operation normalization, request replay serialization, and request-history filter query building

### New React pages

- [x] `src/Armada.Dashboard/src/pages/ApiExplorer.tsx`
- [x] `src/Armada.Dashboard/src/pages/RequestHistory.tsx`

### Recommended React component slices

- [ ] `src/Armada.Dashboard/src/components/api-explorer/ApiOperationList.tsx`
- [ ] `src/Armada.Dashboard/src/components/api-explorer/ApiRequestBuilder.tsx`
- [ ] `src/Armada.Dashboard/src/components/api-explorer/ApiResponsePanel.tsx`
- [ ] `src/Armada.Dashboard/src/components/api-explorer/ApiCodeSnippetTabs.tsx`
- [ ] `src/Armada.Dashboard/src/components/api-explorer/ApiRecentRequests.tsx`
- [ ] `src/Armada.Dashboard/src/components/request-history/RequestHistorySummaryCards.tsx`
- [ ] `src/Armada.Dashboard/src/components/request-history/RequestHistoryChart.tsx`
- [ ] `src/Armada.Dashboard/src/components/request-history/RequestHistoryFilters.tsx`
- [ ] `src/Armada.Dashboard/src/components/request-history/RequestHistoryTable.tsx`
- [ ] `src/Armada.Dashboard/src/components/request-history/RequestHistoryDetailModal.tsx`

### Styling work

- [x] Add Armada-native style blocks in `src/Armada.Dashboard/src/App.css`
- [x] Reuse existing shared components where possible:
  - `RefreshButton`
  - `ActionMenu`
  - `Pagination`
  - `ConfirmDialog`
  - `ErrorModal`
  - `JsonViewer`
  - `StatusBadge`
- [x] Reuse `MissionHistoryChart` interaction patterns for range tabs, chart labels, and tooltips

## Cross-Feature Integration

The value improves if these pages are not isolated tools.

- [x] Add `Replay in API Explorer` from Request History detail
- [x] Add `View recent server requests` or `Open request history` affordance from API Explorer
- [ ] Add optional links from `Server` or `Doctor` to `API Explorer`, raw `/openapi.json`, and `/swagger`
- [ ] Consider follow-on links from failed `Events` or `Signals` into matching request-history entries when correlation becomes available

## Suggested Rollout Phases

### Phase 1: Foundation and Schema

- [x] Define request-history models and query DTOs
- [x] Add database interfaces and driver wiring
- [x] Add schema creation and indexes for all supported databases
- [x] Add Armada settings for capture enablement, retention, and truncation

Acceptance criteria:

- [x] Armada boots with the new schema on supported databases
- [x] Request-history tables and indexes are created automatically
- [x] Settings can disable capture cleanly

### Phase 2: Capture and REST APIs

- [x] Implement request lifecycle capture in `ArmadaServer`
- [x] Persist summary and detail records
- [x] Add request-history REST routes
- [x] Add summary aggregation and bucketed activity API
- [x] Add redaction and truncation

Acceptance criteria:

- [x] Typical REST requests appear in stored history
- [x] Auth headers and sensitive payload fields are redacted
- [x] Large or binary bodies do not destabilize capture
- [x] Summary and detail APIs return useful data

### Phase 3: Request History UI

- [x] Build `RequestHistory` page shell
- [x] Build summary cards and activity chart
- [x] Build filter grid and paginated table
- [x] Build detail modal and delete flows

Acceptance criteria:

- [x] A user can filter, inspect, and delete history entries from the UI
- [x] The chart is readable and consistent with Armada's visual style
- [x] Empty, loading, and error states are polished

### Phase 4: API Explorer UI

- [x] Build OpenAPI loader and operation normalization
- [x] Build searchable operation catalog
- [x] Build parameter/body editor
- [x] Build response/code views
- [x] Add request execution, abort, and local recent history

Acceptance criteria:

- [x] A user can browse Armada routes, execute them, and inspect results without leaving the dashboard
- [x] Explorer request forms are generated accurately enough to be useful against Armada's real API
- [x] Explorer styling feels like Armada, not like embedded Swagger

### Phase 5: Replay and Polish

- [x] Add replay handoff from Request History into API Explorer
- [x] Add links to raw OpenAPI and Swagger
- [ ] Improve OpenAPI metadata where the explorer experience is weak
- [x] Tune copy, badges, empty states, and responsiveness

Acceptance criteria:

- [x] A stored request can be replayed into the explorer with editable fields
- [x] The explorer and history pages feel like a cohesive toolset

### Phase 6: Tests and Docs

- [x] Add backend unit tests for redaction, truncation, filters, summary buckets, and deletes
- [x] Add route tests for request-history endpoints
- [x] Add dashboard tests for explorer rendering, request-history filters, and replay flow
- [x] Add README and operator docs

Acceptance criteria:

- [x] New backend behavior is covered by automated tests
- [x] Dashboard pages have smoke coverage for key flows
- [x] Docs explain both features and their security/retention behavior

## Concrete File Targets

These are the primary Armada files and directories expected to change.

### Server and core

- [x] `src/Armada.Server/ArmadaServer.cs`
- [x] `src/Armada.Server/Routes/RequestHistoryRoutes.cs`
- [x] `src/Armada.Core/Database/DatabaseDriver.cs`
- [x] `src/Armada.Core/Database/Interfaces/IRequestHistoryMethods.cs`
- [x] `src/Armada.Core/Services/RequestHistoryCaptureService.cs`
- [x] `src/Armada.Core/Settings/ArmadaSettings.cs`
- [x] new request-history model files under `src/Armada.Core/Models`
- [x] database driver files under `src/Armada.Core/Database/*`

### Dashboard

- [x] `src/Armada.Dashboard/src/App.tsx`
- [x] `src/Armada.Dashboard/src/components/Layout.tsx`
- [x] `src/Armada.Dashboard/src/api/client.ts`
- [x] `src/Armada.Dashboard/src/types/models.ts`
- [x] `src/Armada.Dashboard/src/pages/ApiExplorer.tsx`
- [x] `src/Armada.Dashboard/src/pages/RequestHistory.tsx`
- [ ] new component files under:
  - `src/Armada.Dashboard/src/components/api-explorer`
  - `src/Armada.Dashboard/src/components/request-history`
- [x] `src/Armada.Dashboard/src/App.css`

### Tests

- [x] `test/Armada.Test.Unit`
- [x] `test/Armada.Test.Automated`
- [x] dashboard test files under `src/Armada.Dashboard`

### Docs

- [x] `README.md`
- [x] `docs/TESTING.md`
- [ ] new operator or API docs as needed

## Risks and Design Constraints

- [ ] Response-body capture may be invasive if implemented too naively; avoid destabilizing streaming or large payload routes
- [ ] Request-history growth can become expensive without retention and indexes
- [ ] Sensitive data leakage is the primary safety risk; redaction is not optional
- [ ] Explorer usefulness depends on OpenAPI metadata quality; route annotations may need cleanup first
- [ ] Multi-tenant visibility rules must be correct before non-admin rollout

## Definition of Done

This project should only be called complete when all of the following are true:

- [x] Armada has a first-class `API Explorer` page with live execution, response inspection, and code snippets
- [x] Armada has a first-class `Request History` page with stored history, filtering, summary cards, charting, detail inspection, and delete flows
- [x] `Request History` can replay a captured request into `API Explorer`
- [x] Styling feels native to Armada and visually cohesive with the current dashboard
- [x] Sensitive data is redacted and large bodies are handled safely
- [x] Tests and docs cover the new capability end to end
