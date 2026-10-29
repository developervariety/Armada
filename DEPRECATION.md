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
  `Armada.Core`'s `ArmadaApiClient`) may call a listed or removed route, or list jobs without paging.

## Active deprecations

Nothing is deprecated right now. The whole-list APIs that were deprecated here were retired; see [Removed](#removed).

| ID | Surface | Item | Replacement | Deprecated in | Removed in | First-party clients |
|---|---|---|---|---|---|---|

## Bounded, not retired

Armada exposes no API that returns a whole stored collection in one response. Every collection is read through a
paged route or tool (`pageNumber`, `pageSize` 1 to 1000 or a lower route cap, `TotalRecords`), and every list route that
had no `POST .../enumerate` form gained one. The guard suite `E2E.CollectionSurface` fails when a new REST route or MCP
tool returns an unpaged list without an entry in its allowlist. The allowlisted items below return a list that is not a
stored collection:

| Surface | Item | Bound |
|---|---|---|
| REST, MCP | `GET /api/v1/ask/quick-actions`, `list_ask_quick_actions` | A fixed catalog defined in code (a handful of entries) |
| REST | `GET /api/v1/runtimes/mux/endpoints` | The endpoints in one Mux configuration file, read from the `mux` CLI |
| REST, MCP, WebSocket | Captain and mission log reads (`.../log`, `get_captain_log`, `get_mission_log`) | `lines` 1 to 5000 per read; page with `offset` |
| REST | `GET /api/v1/workspace/vessels/{vesselId}/search` | `maxResults` 1 to 1000 (default 200) |

### Lists inside a single read (not retired)

These are the children of one entity returned with its single-resource read, not collections of every row of a type.
They stay, because the reads are single-resource lookups; each large one has a paged alternative.

| Surface | Item | Paged alternative |
|---|---|---|
| REST, MCP, WebSocket | Missions in `GET /api/v1/voyages/{id}`, `voyage_status` with `includeMissions`, `get_voyage` | `POST /api/v1/missions/enumerate` with `voyageId`, `enumerate` missions with `voyageId` |
| REST, MCP, WebSocket | `Voyages` in `GET /api/v1/status`, `status` (one progress row per active voyage) | `POST /api/v1/voyages/enumerate` with `status` |
| REST, MCP | Targets in `GET /api/v1/fleet-action-runs/{id}`, `fleet_action_run_status` | `POST /api/v1/fleet-action-runs/{id}/targets/enumerate`, `enumerate` fleet_action_run_target |
| REST, MCP | Items in `GET /api/v1/vessels/import/batches/{id}`; candidates of `POST /api/v1/vessels/import/discover`, `discover_vessels` | None (one import batch or one directory scan) |
| REST, MCP | `GET /api/v1/captains/{id}/tools`, `get_captain_tools`; directory and change lists of the workspace routes | None (one captain's tool inventory; one directory or working tree) |

## Removed

Each of these returned an entire collection in one response. They are no longer registered: a request for a removed
REST route is answered with HTTP 410 Gone and a message naming the replacement, and a removed MCP tool is not listed by
`tools/list`. Each replacement returns one page (`pageNumber`, `pageSize` 1 to 1000) with `TotalRecords`. The list is
`src/Armada.Core/ApiSurface/RetiredSurface.cs`; `Docs.Deprecation` keeps this table and that file in step.

| ID | Surface | Item | Replacement | Removed in |
|---|---|---|---|---|
| D-001 | REST | `GET /api/v1/harbors` | `POST /api/v1/harbors/enumerate` | Unreleased |
| D-002 | REST | `GET /api/v1/model-endpoints` | `POST /api/v1/model-endpoints/enumerate` | Unreleased |
| D-003 | REST | `GET /api/v1/planning-sessions` | `POST /api/v1/planning-sessions/enumerate` | Unreleased |
| D-004 | REST | `GET /api/v1/objectives/{id}/refinement-sessions` | `POST /api/v1/objectives/{id}/refinement-sessions/enumerate` | Unreleased |
| D-005 | REST | `GET /api/v1/backlog/{id}/refinement-sessions` | `POST /api/v1/backlog/{id}/refinement-sessions/enumerate` | Unreleased |
| D-006 | REST | `GET /api/v1/inbox` | `POST /api/v1/inbox/enumerate` | Unreleased |
| D-007 | REST | `GET /api/v1/cli-permissions/rules` | `POST /api/v1/cli-permissions/rules/enumerate` | Unreleased |
| D-008 | REST | `GET /api/v1/push/devices` | `POST /api/v1/push/devices/enumerate` | Unreleased |
| D-009 | REST | `GET /api/v1/releases/{id}/github/pull-requests` | `POST /api/v1/releases/{id}/github/pull-requests/enumerate` | Unreleased |
| D-010 | REST | `GET /api/v1/vessels/{id}/branches` | `POST /api/v1/vessels/{id}/branches/enumerate` | Unreleased |
| D-011 | REST | `GET /api/v1/signals/recipient/{captainId}` | `POST /api/v1/signals/enumerate (toCaptainId, unreadOnly)` | Unreleased |
| D-012 | MCP | `inbox` | `enumerate with entityType inbox` | Unreleased |
| D-013 | MCP | `list_cli_permission_rules` | `enumerate with entityType cli_permission_rules` | Unreleased |
| D-014 | Behavior | `GET /api/v1/jobs` with no paging or filter parameters (returned every job) | `GET /api/v1/jobs` now always returns one page (first page of 100 without parameters); `POST /api/v1/jobs/enumerate` | Unreleased |
| D-015 | REST | `GET /api/v1/cli-permissions/requests` | `POST /api/v1/cli-permissions/requests/enumerate` | Unreleased |
| D-016 | REST | `GET /api/v1/signals/recent` | `POST /api/v1/signals/enumerate` | Unreleased |
| D-017 | MCP | `list_cli_permission_requests` | `enumerate with entityType cli_permission_requests` | Unreleased |
| D-018 | Behavior | WebSocket `list_cli_permission_requests` returned up to 100 rows as a bare list | It returns one page (`query.pageNumber`, `query.pageSize`) as an enumeration result | Unreleased |

Paged `POST .../enumerate` forms added alongside existing paged `GET` lists (the `GET` forms stay, because they already
page): `/api/v1/tenants/enumerate`, `/api/v1/users/enumerate`, `/api/v1/credentials/enumerate`,
`/api/v1/jobs/enumerate`, `/api/v1/token-usage/enumerate`, `/api/v1/request-history/enumerate`,
`/api/v1/stage-outcomes/enumerate`, `/api/v1/failure-cause-rules/enumerate`, `/api/v1/model-probe-runs/enumerate`,
`/api/v1/battery/packs/enumerate`, `/api/v1/harbors/{id}/jobs/enumerate`, and
`/api/v1/missions/{id}/handoff-artifacts/enumerate`.

## Adding a deprecation

1. Add the replacement first, and move every first-party client to it (the dashboard and mobile `api/client.ts`,
   `Armada.Client` and the TUI, the `armada` CLI, `Armada.Core`'s `ArmadaApiClient`, and any prompt or instruction file
   that tells an agent to use the old item).
2. List the old item and its replacement in `DeprecatedSurface.cs` (`_Routes`, `_Tools`, or `_WebSocketCommands`).
3. Add a row to [Active deprecations](#active-deprecations) with the next ID, and a `Deprecated` entry to the
   CHANGELOG's Unreleased section naming the item, its replacement, and the release that removes it.
4. Mark it in the reference doc (REST_API.md, MCP_API.md, or WEBSOCKET_API.md) with a deprecation note.

## Removal checklist

When a deprecated item is removed:

1. Delete the route or tool, its handler, client methods, and tests.
2. Move it from `DeprecatedSurface.cs` to `RetiredSurface.cs` (REST routes then answer 410 Gone with the replacement),
   and move its row from [Active deprecations](#active-deprecations) to [Removed](#removed).
3. Regenerate the surface file (`scripts/common/generate-api-surface.sh`) and list the removal under a `Removed` heading
   in the CHANGELOG.
