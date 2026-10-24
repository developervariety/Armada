# Deprecations

This is the managed list of everything Armada has deprecated: what it is, what replaces it, when it was deprecated,
when it is removed, and whether Armada's own clients have moved off it. The policy behind it (what a deprecation must
do, and that a covered item is removed only in a major release) is in
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md#deprecation).

The `Client.RouteSurface` and `Docs.Deprecation` test suites keep this file honest:

- every REST route and MCP tool in [Active deprecations](#active-deprecations) must be listed in
  `src/Armada.Core/ApiSurface/DeprecatedSurface.cs` with the same replacement, and nothing may be listed there that is
  missing here;
- the Admiral marks each listed route `deprecated: true` in OpenAPI with `[Deprecated]` and the replacement in its
  summary, and prefixes each listed MCP tool's description with `[Deprecated]`;
- no first-party client (the dashboard and mobile `api/client.ts`, `Armada.Client` and the TUI, the `armada` CLI,
  `Armada.Core`'s `ArmadaApiClient`) may call a listed route, or list jobs without paging.

## Active deprecations

All of these were deprecated because they return an entire collection in one response. Each replacement returns one
page (`pageNumber`, `pageSize` from 1 to 1000) with `TotalRecords`, so no response is unbounded. The deprecated items
keep working, unchanged, until they are removed.

| ID | Surface | Item | Replacement | Deprecated in | Removed in | First-party clients |
|---|---|---|---|---|---|---|
| D-001 | REST | `GET /api/v1/harbors` | `POST /api/v1/harbors/enumerate` | Unreleased | 2.0 | Migrated |
| D-002 | REST | `GET /api/v1/model-endpoints` | `POST /api/v1/model-endpoints/enumerate` | Unreleased | 2.0 | Migrated |
| D-003 | REST | `GET /api/v1/planning-sessions` | `POST /api/v1/planning-sessions/enumerate` | Unreleased | 2.0 | Migrated |
| D-004 | REST | `GET /api/v1/objectives/{id}/refinement-sessions` | `POST /api/v1/objectives/{id}/refinement-sessions/enumerate` | Unreleased | 2.0 | Migrated |
| D-005 | REST | `GET /api/v1/backlog/{id}/refinement-sessions` | `POST /api/v1/backlog/{id}/refinement-sessions/enumerate` | Unreleased | 2.0 | Migrated |
| D-006 | REST | `GET /api/v1/inbox` | `POST /api/v1/inbox/enumerate` | Unreleased | 2.0 | Migrated |
| D-007 | REST | `GET /api/v1/cli-permissions/rules` | `POST /api/v1/cli-permissions/rules/enumerate` | Unreleased | 2.0 | Migrated |
| D-008 | REST | `GET /api/v1/push/devices` | `POST /api/v1/push/devices/enumerate` | Unreleased | 2.0 | Migrated |
| D-009 | REST | `GET /api/v1/releases/{id}/github/pull-requests` | `POST /api/v1/releases/{id}/github/pull-requests/enumerate` | Unreleased | 2.0 | Migrated |
| D-010 | REST | `GET /api/v1/vessels/{id}/branches` | `POST /api/v1/vessels/{id}/branches/enumerate` | Unreleased | 2.0 | Migrated |
| D-011 | REST | `GET /api/v1/signals/recipient/{captainId}` | `POST /api/v1/signals/enumerate (toCaptainId, unreadOnly)` | Unreleased | 2.0 | Migrated (no first-party caller) |
| D-012 | MCP | `inbox` | `enumerate with entityType inbox` | Unreleased | 2.0 | Migrated |
| D-013 | MCP | `list_cli_permission_rules` | `enumerate with entityType cli_permission_rules` | Unreleased | 2.0 | Migrated |
| D-014 | Behavior | `GET /api/v1/jobs` with no paging or filter parameters (returns every job) | `GET /api/v1/jobs?pageNumber=1&pageSize=100` (the same route with paging) | Unreleased | 2.0 (the parameterless form will return the first page) | Migrated |

Notes:

- **D-010** keeps the vessel's default branch and any git error on the paged result (`BranchEnumerationResult`:
  `VesselId`, `DefaultBranch`, `Error`).
- **D-012 and D-013** stay on the Ask Armada read-only allowlist until they are removed, so an approval is never needed
  for them; `enumerate` is on it as well.
- Screens that need a whole set (pickers, id-to-name maps) read it page by page from the replacement routes
  (`fetchAllPages` in the dashboard, `ArmadaPaging.ReadAllAsync` in `Armada.Client`). Each request still returns at
  most one page.

## Bounded, not deprecated

These return a list in one response but are bounded, so they are not deprecated. Revisit an entry if its bound changes.

| Surface | Item | Bound |
|---|---|---|
| REST, MCP, WebSocket | `GET /api/v1/cli-permissions/requests`, `list_cli_permission_requests` | `limit` 1 to 1000 (default 100), newest first |
| REST | `GET /api/v1/signals/recent` | `count` 1 to 1000 (default 50); larger values are clamped |
| REST, MCP, WebSocket | Captain and mission log reads (`.../log`, `get_captain_log`, `get_mission_log`) | `lines` 1 to 5000 per read; page with `offset` |
| REST, MCP | `GET /api/v1/ask/quick-actions`, `list_ask_quick_actions` | A fixed catalog defined in code |
| REST | `GET /api/v1/runtimes/mux/endpoints` | The endpoints in one Mux configuration file |
| REST (8 routes) and history | `POST .../enumerate` for check runs, deployments, releases, environments, skills, workflow profiles, project profiles, and `/history/enumerate` | A body `PageSize` is now clamped to 1 to 1000 (it used to be passed to the database unclamped) |

### Lists inside a single read (open decision)

These are not collections of every row of a type but the children of one entity. Each has a paged alternative, and they
are not deprecated yet. Deprecating them would mean moving the fleet, voyage, and home screens to the alternatives.

| Surface | Item | Paged alternative |
|---|---|---|
| REST, MCP, WebSocket | Vessels in `GET /api/v1/fleets/{id}`, `get_fleet` | `GET /api/v1/vessels?fleetId=...`, `enumerate` vessels with `fleetId` |
| REST, MCP, WebSocket | Missions in `GET /api/v1/voyages/{id}`, `voyage_status` with `includeMissions` | `GET /api/v1/missions?voyageId=...`, `enumerate` missions with `voyageId` |
| REST, MCP, WebSocket | `Voyages` in `GET /api/v1/status`, `status` (one progress row per active voyage) | `GET /api/v1/voyages?status=InProgress` |

## Removed

Nothing has been removed yet.

## Adding a deprecation

1. Add the replacement first, and move every first-party client to it (the dashboard and mobile `api/client.ts`,
   `Armada.Client` and the TUI, the `armada` CLI, `Armada.Core`'s `ArmadaApiClient`, and any prompt or instruction file
   that tells an agent to use the old item).
2. List the old item and its replacement in `DeprecatedSurface.cs` (`_Routes`, `_Tools`, or `_WebSocketCommands`).
3. Add a row to [Active deprecations](#active-deprecations) with the next ID, and a `Deprecated` entry to the
   CHANGELOG's Unreleased section naming the item, its replacement, and the release that removes it.
4. Mark it in the reference doc (REST_API.md, MCP_API.md, or WEBSOCKET_API.md) with a deprecation note.

## Removal checklist (2.0)

When the 2.0 release is cut:

1. Delete each route and tool listed above, and its handler, client methods, and tests.
2. For D-014, make `GET /api/v1/jobs` without parameters return the first page.
3. Empty `_Routes`, `_Tools`, and `_WebSocketCommands` in `DeprecatedSurface.cs`, and move every row from
   [Active deprecations](#active-deprecations) to [Removed](#removed) with the release.
4. Write the 2.0 surface file (`scripts/common/generate-api-surface.sh`) and list the removals under Removed in the
   CHANGELOG.
