# Scoped voyage mission summaries

`GET /api/v1/voyages/{id}/mission-summary` returns status counts and a page of
distinct vessel IDs. The database groups all visible missions before paging
vessels. It selects no mission description, diff, agent output or playbook data.
All four providers use the same parameterized query with provider paging syntax.
No schema migration is required.

The route first checks voyage visibility. Global administrators get global
mission scope, tenant administrators get their tenant, and other users get their
own missions in that tenant. Query parameters cannot override this scope.
Unauthorized voyage IDs return 404. Unauthenticated requests return 401.

`pageNumber` starts at 1. `pageSize` defaults to 100 and must be between 1 and
100. Invalid bounds or nonnumeric values return 400. `statusCounts` covers all
visible missions regardless of page. `vessels.totalRecords` counts distinct
non-NULL vessel IDs; `vessels.objects` contains only the requested page. Empty
and beyond-end pages retain their totals. Pages use the provider's identifier
collation and can change between requests if missions change concurrently.

The new provider case failed as unsupported on all four providers before the
query was added. It then passed on the same databases, with ordinary totals
62 SQLite, 62 PostgreSQL, 63 MySQL and 62 SQL Server. It checks two vessel pages
plus an empty third page, duplicate associations, status totals, separate users
and tenants, empty voyages and omission of a large description.

Four added REST cases cover all-page totals and associations, payload omission,
page validation, missing voyages, absent/invalid credentials, separate users in
one tenant, another tenant, tenant/global administrators and forged scope query
parameters. The first run exposed an empty paging value treated as absent and a
fixture that attempted to purge an open voyage. Parameter presence is now checked
explicitly; cleanup cancels its owned voyage before purge and retains failures.
All 136 focused voyage and cross-tenant cases then passed.

Final combined checks passed 4,042 Unit, 954 automated API and 183 runtime cases
with no skips, in 276 seconds. All four final provider runs passed, including
invalid scope and page rejection. The solution build emitted 106 warnings and
zero errors; this incremental count is not a clean-build warning census. All
274 historical migration declarations and ten source-guard controls pass.
Dashboard source is unchanged. No image was built or deployed.

The dashboard still derives associations from recent missions. This backend
contract is ready for its later adaptation after backend acceptance. Existing
mission summary endpoints and fork voyage progress status meanings are retained.
Git-anchor snapshots, admission reasons and other service outcome DTOs remain
separate backend work.
