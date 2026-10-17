# Storage Management Plan

## Objective and release

Give system administrators a trustworthy, read-only inventory of Armada storage in the existing unreleased v1.1.0. Preserve all current version numbers and image tags. Deliver the implementation, committed dashboard distribution, documentation, test evidence, and rebuilt images together on main.

Storage utilization is not one number. File lengths, database allocation, shared Git objects, and volume capacity measure different things. The interface must label their scopes and avoid suggesting that observed bytes account for every byte on a host.

## Requirements and repository conventions

Apply `~/Code/Agents/requirements/CODE_STYLE.md`, `BACKEND_ARCHITECTURE.md`, `BACKEND_TEST_ARCHITECTURE.md`, `AUTHENTICATION.md`, `FRONTEND_ARCHITECTURE.md`, `DASHBOARD_STYLE_AND_USABILITY.md`, `I18N.md`, `TELEMETRY_REQUIREMENTS.md`, `REPOSITORY_REQUIREMENTS.md`, and `VERSIONING.md`. Use existing Armada integration points rather than replacing unrelated architecture. Keep Watson hosting, explicit authorization declarations, strongly typed contracts, provider-specific SQL, cancellation, XML documentation, and one class per file. Do not add dependencies for filesystem scanning or chart rendering.

Use the existing Server page and administration routes as the design reference: shared PageHeader, DataTable, pagination, copy controls, theme tokens, AuthContext, LocaleContext, and API client. Storage introduces no new permission tier. Only authenticated global administrators may read it or request collection. Tenant administrators and ordinary users receive 403; unauthenticated requests receive 401. Never return connection strings or file contents.

## Scope and measurement contract

| Source | Measurement | Limitations and attribution |
| --- | --- | --- |
| Admiral managed filesystem | Configured logs, docks, repository roots, data-directory remainder, backups, retained builds | Recursive file lengths, not allocated blocks. Separate managed roots into exclusive categories. Configured paths can live outside the data directory. |
| Logs | Server, missions, captains, planning sessions, diffs, instructions, final messages, docks and Harbor jobs | File count, byte count, oldest/newest modification time, largest files. Modification time is not semantic record age. |
| Repository and dock directories | Per-directory size and associated vessel/dock when known | Shared Git objects are attributed to their containing repository, not copied into each worktree estimate. Symlinks and reparse points are skipped. Hard links and copy-on-write extents can still differ from physical disk usage. |
| SQLite | Main file, WAL, SHM, database page allocation and free pages; table counts and table/index pages when dbstat is available | Physical sidecars belong in filesystem totals only. Database allocation is displayed separately. No checkpoint, vacuum or write operation. |
| PostgreSQL | Database size, table/index size, estimated rows | System catalogs scoped to the configured database/schema. Estimates depend on statistics freshness. |
| MySQL | Data/index allocation and estimated table rows | InnoDB statistics and allocation are estimates. No host-volume inference. |
| SQL Server | Database file allocation, table/index allocation and catalog row counts | Catalog permissions may restrict metrics. Label counts as estimates. |
| Harbor | Managed local logs, docks and bare repositories; host volumes | Collect on Harbor itself, asynchronously. Send cached reports through the authenticated link. Older Harbors remain compatible and show unavailable coverage. |
| Host volumes | Total and available bytes for volumes containing measured locations | Capacity includes unrelated data. Container mounts describe the container-visible filesystem. Do not add capacity to Armada bytes. |
| Trends | Bounded recent samples, category and table changes, host totals | Dashboard history is process-local and resets after restart. Prometheus owns durable metric retention; do not add a second metrics database or serialize domain snapshots into database blobs. |

Unknown, missing, unsupported, inaccessible, timed-out, partial, stale and complete must remain distinguishable. A failed collector must not replace its last successful report with an apparent zero. The response records sample timestamps, warnings and collection state. Fleet totals are explicitly observed totals and may include stale hosts; the dashboard shows host coverage alongside them.

## Collection design

Implement typed storage models in Core, a bounded filesystem collector, provider-specific statistics methods behind `IStorageMethods`, and an Admiral inventory service. Attach storage methods to each database driver, using the driver's actual connection configuration. Default background collection runs every five minutes, with a configurable per-stage time budget, maximum examined files, history capacity and largest-file limit. Numeric settings require validated bounds.

Inventory discovery uses configured roots plus known vessel/dock paths on their owning host. Normalize paths using the operating system's case rules. Sort roots by specificity and exclude child roots from ancestor traversal. Never follow symlinks or reparse points, including configured roots and intermediate path components. File races and access failures become partial-source warnings. Cap traversal and retained detail so a large checkout cannot exhaust memory. Limit concurrent collections to one, coalesce refresh requests, publish complete snapshots atomically, and cancel and await background workers before database disposal.

Read catalog statistics by default. Add an explicit asynchronous exact-count operation with query timeouts and cancellation; never execute `COUNT(*)` merely because a browser opens or polls the page. Discover table identifiers from trusted provider catalogs and quote them with provider-specific escaping. Count failures are per-table and preserve other metrics. Database failures are independent of filesystem failures.

Harbor collection must run outside heartbeat creation and socket processing. Extend the heartbeat with an optional typed cached report; omitted reports mean unsupported or not yet measured. Bind reports to the authenticated Harbor identity on the Admiral, bound accepted payload detail, and retain last-known measurements on disconnect. Do not accept a Harbor-supplied host identity as authority or expose this data through ordinary Harbor detail APIs. Explicitly show that a fresh Harbor binary is needed to report storage.

## API and route inventory

| Surface | Job and role | Backend and controls | Empty/error behavior |
| --- | --- | --- | --- |
| `/storage` | Global administrator inspects utilization | Cached inventory, source/table/host/volume/history tables, local filter, sortable columns, paging, copy paths, refresh and collect controls | First sample pending, partial sources, offline Harbor, failed API with retry |
| `GET /api/v1/storage` | Global administrator reads cached inventory | Typed snapshot and bounded history, retention context, collection status | Never starts scans. Initial response explains pending collection. |
| `POST /api/v1/storage/refresh` | Global administrator requests collection | Optional typed `exactCounts` boolean, returns accepted state immediately | Coalesces with existing work; does not block HTTP on scans. |

Register authorization and OpenAPI metadata for both endpoints. Document methods, bodies, response shapes, status codes, limitations, and examples in REST_API.md and the existing Postman collection. No MCP operation is necessary for this dashboard feature; MCP_API.md remains unchanged unless a tool is added. The API Explorer consumes the registered OpenAPI descriptions.

## Dashboard behavior

Use compact KPI cards for observed filesystem bytes, database allocation, log bytes and host coverage. Label database allocation separately so it is never added twice. Show measurement time and current collection activity near refresh controls. Keep previous results visible during refresh and when a later request fails.

Provide shared tables for locations, database tables, volumes, largest files and recent history. Support client filtering and sorting over the explicitly bounded cached snapshot, with pagination and a clear local-filter label. Display host and category, path, bytes, file count, age, entity attribution, state and warnings. Offer copy controls for paths, no arbitrary file browsing. Use locale-aware byte/count/date formatting and translation keys through the established catalog. All controls need descriptive titles and accessible labels. No deletion, pruning, vacuum, repository cleanup or server restart is part of the page.

Retention context includes data, request history, planning sessions, Ask threads, jobs, imports, CLI permission requests, fleet action runs and retained builds. Present existing settings as context, not guaranteed reclaimable bytes. An old log or idle worktree is not automatically safe to remove. Cleanup policy and confirmation-based destructive controls are a separate future feature.

## Implementation sequence and acceptance gates

1. Define settings and DTOs, including units, null semantics, bounded detail and timestamp fields. Add defaults without changing existing version metadata.
2. Implement and test filesystem accounting: nested roots, roots outside data, excluded links, missing paths, bounded scans, largest files, timestamps, cancellation and volume association.
3. Implement all four database adapters. Test a common contract against seeded tables, metadata/count modes, allocation semantics and cancellation. Verify SQLite WAL/SHM accounting and unavailable dbstat behavior.
4. Add background inventory lifecycle, single-flight refresh, bounded recent history, stale-host coverage and retention context. Instrument collection counters, duration and category bytes with bounded metric dimensions and spans, using the existing telemetry host.
5. Wire optional Harbor reports through link lifecycle and collectors; test old/new heartbeat compatibility, trusted identity binding and disconnect behavior.
6. Register global-admin-only REST endpoints and write authentication/authorization tests covering anonymous, user, tenant admin and global admin. Confirm no detail leaks in denied responses.
7. Add dashboard route/navigation, shared-client calls and typed contracts. Test denied access, loading/error/empty/populated/partial states, refresh and exact-count intent, local filtering, paging and locale formatting.
8. Update README, REST reference, Postman, v1.1.0 changelog and generated API surface. Rebuild committed dashboard dist. Run the .NET build and relevant Touchstone suites, provider matrix, dashboard production build and tests. Record any unrelated baseline failures honestly.
9. Perform browser visual checks at desktop, tablet and mobile widths in light/dark themes. Verify tables scroll and controls remain accessible. Use an isolated test instance rather than modifying user data.
10. Build affected server/dashboard images with the existing v1.1.0 tags and any already established alias tags. Inspect build outputs and run isolated image smoke tests. Push updated release images if registry credentials are available. Preserve compose versions and user volumes. Rebuilding images does not authorize stopping the user's server.
11. Review the diff for secrets, generated artifacts, unrelated files and version changes. Commit only task files, push main without force, and report the commit, image tags/digests, test evidence and review entry point.

## Validation and delivery record

Implementation status and exact commands/results will be appended here as work completes. Existing unrelated untracked files (`.DS_Store`, `assets/.DS_Store`, `docs/HYBRID_DEVELOPMENT_RESULTS.md`, `src/nupkg/`) must remain outside the commit. Do not overwrite or remove them.


## Implemented integration and validation

The storage page is available at `/dashboard/storage` and Settings > Storage. Both REST endpoints have typed OpenAPI bodies/responses and explicit global-administrator declarations. All four database drivers expose the same read-only statistics contract. The implementation does not require a database migration or persist snapshots into application tables.

Repository parity gates require dashboard routes, hub tabs and shared client exports to remain usable in the terminal and mobile clients. The delivery therefore also includes a read-only TUI Storage screen and native Storage tab/route, both using the same API. They preserve the explicit distinction between reading cached data and requesting collection. Their manifests and generated mobile routes are regenerated and pass their release gates. Two pre-existing native gaps discovered while regenerating the mobile manifest are documented explicitly: opening a desktop terminal and editing the Harbor launch requirement remain desktop/dashboard operations. Storage does not alter either policy.

English storage message keys have a preloaded fallback in the shared catalog runtime. Labels are supplied for the existing eight beta locales; explanatory messages use the established English fallback when a beta translation is absent. Counts, byte units and dates use the selected locale. The repository's existing beta locale policy is preserved rather than claiming a native-language review that did not occur.

Grafana has a provisioned Armada Storage dashboard for category bytes, database allocation, collection outcomes, stage durations and collection duration. Its source is mirrored under assets/grafana. Durable retention remains Prometheus's job. Source-level history contains only recent process-local samples and resets on restart.

### Completed checks

| Check | Result |
| --- | --- |
| Solution build, net8.0 and net10.0 | Passed with zero warnings and zero errors |
| Database storage contract, SQLite/PostgreSQL/MySQL/SQL Server | Passed against all four actual providers |
| Storage services and REST authorization | Passed: overlaps, links, missing roots, traversal bounds, cancellation, cache/history lifecycle, Harbor protocol compatibility and authenticated identity binding, global-administrator access |
| Client route coverage, API contract and authorization declarations | Passed after adding typed .NET storage methods and reconciling the regenerated baseline's already existing synthetic routes |
| Focused final backend regression | 64 passed; no failures |
| TUI storage and parity | 12 passed; no failures |
| Dashboard production build and complete Vitest suite | Passed, 869 tests |
| Native TypeScript and ESLint, including shared dashboard code | Passed |
| Native complete Jest suite | 1,223 passed; focused new storage tests add two passes |
| Parity generation | TUI, mobile manifest and mobile route checks passed, with no planned entries |
| Real browser | Exact counts, all storage sections, keyboard tabs, copy-path controls, light/dark themes, 1440px desktop, 768px tablet and 390px mobile; no JavaScript errors or page-wide horizontal overflow |
| Published server/dashboard images | Real health, cached inventory and asynchronous collection requests through the dashboard reverse proxy; exact-count rendering and the same browser checks passed |

The full initial backend run executed 4,646 cases: 4,625 passed, 12 failed and 9 were skipped. Four failures were contract/parity checks encountered during implementation; the final focused regression and parity runs pass after their required integrations were added. The other eight are pre-existing terminal-rendering failures, reproduced unchanged on original commit `eea24bd9` in a separate clean worktree. A UTF-8 terminal environment does not resolve them. No storage code exists in that baseline checkout.

The baseline failures are `Tui.Display.ascii_icon_mode`, `Tui.FocusBorder.tab_moves_the_pane_border`, `Tui.FocusHints.pending_strip`, `Tui.FocusSweep.ask_conversation_list`, `Tui.FocusSweep.ask_transcript`, `Tui.FocusSweep.ask_composer`, `Tui.FocusSweep.tab_bar_and_content`, and `Tui.ThemeI18n.cjk_shell`. They concern ASCII border glyphs, an existing pending-approval strip wrap, and CJK sidebar rendering. They are recorded here rather than hidden, skipped, or changed as part of storage management.

### Review procedure

Sign in as a global administrator and open System > Storage. Confirm that no scan starts merely from opening or refreshing the view. Select Collect now, wait for completion, and inspect the locations and source timestamps. Request exact counts separately, then inspect Database count accuracy. Review Hosts for any unavailable/offline Harbor and update a Harbor binary to exercise remote reports. Compare volumes with the relevant host/container mounts; capacity includes unrelated data. Browse History after two collections, switch between category/host/table changes, and confirm that restarting an isolated Admiral clears this recent history. Verify that a tenant administrator cannot access either storage REST endpoint or any native inventory data.

All existing v1.1.0 version metadata and compose tags are retained. The server, dashboard and proxy images are rebuilt for linux/amd64 and linux/arm64 and published under both v1.1.0 and latest. No existing user server or workload is restarted; image smoke tests use isolated containers and temporary data. The final Git commit and registry digests are reported with the delivery.

### Reproduce validation

Run the following from the repository root. Database-backed tests use throwaway test data; for the provider matrix, configure the test harness environment to point at a disposable PostgreSQL, MySQL or SQL Server instance and rerun `Database.Storage`.

```sh
dotnet build src/Armada.sln --no-restore
ARMADA_TEST_SUITES=Services.Storage,Database.Storage,E2E.Storage,Tui.Storage,Tui.Parity,Client.RouteSurface,Client.Coverage dotnet run --project src/Test.Automated -f net10.0
npm run build --prefix src/Armada.Dashboard
npm run test:run --prefix src/Armada.Dashboard
npm run test:ci --prefix src/Armada.Mobile -- --runInBand
npm run lint --prefix src/Armada.Mobile
python3 scripts/tui/generate-parity-manifest.py --check
python3 scripts/mobile/generate-parity-manifest.py --check --forbid-planned
python3 scripts/mobile/generate-route-screens.py --check
```

The final release-focused backend run contains 23 passing checks, including the new terminal storage screen. The final proxy health probe succeeds with a non-default configured proxy password. Both the Admiral and Proxy retain their existing secure startup requirements in containers: use the supplied compose configuration and provide the required initial/shared passwords.

### Published image indexes

Both tags resolve to the same multi-architecture index for each image. Each index contains linux/amd64 and linux/arm64 images.

| Repository | Tags | Index digest |
| --- | --- | --- |
| `jchristn77/armada-server` | `v1.1.0`, `latest` | `sha256:feda3d838a1055d549c4b11beac410dd52346f19d93f7b801f9f35a594024788` |
| `jchristn77/armada-dashboard` | `v1.1.0`, `latest` | `sha256:19ed8755efd108bfcc9e55daaab8c591ce38d71179b5aab95f5b26015b27938c` |
| `jchristn77/armada-proxy` | `v1.1.0`, `latest` | `sha256:9b5462a7e77f48d737722008159727d283fcd04639775259868a4bf75b1cdde9` |
