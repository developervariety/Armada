# Bounded Enumeration Contract

Armada has no "get all" API. No REST route or MCP tool returns, embeds, or echoes a collection that is not bounded by
a server-enforced limit. This file is the contract every route and tool follows and the checklist that records how
each audited surface meets it. The shared code is `src/Armada.Core/Services/CollectionLimits.cs`,
`EnumerationQuery`, and `EnumerationResult<T>`.

## Paging

- A stored collection (rows in a database table, or any set that grows with operational data) is read one page at a
  time with `EnumerationQuery` and returned as `EnumerationResult<T>`: `PageNumber`, `PageSize`, `TotalPages`,
  `TotalRecords`, `Objects`.
- `PageSize` defaults to 10 (`CollectionLimits.DefaultPageSize`), minimum 1, maximum 1000. A value outside the range is
  normalized to the nearest bound (existing convention); the result's `PageSize` shows the size used. `PageNumber`
  below 1 is normalized to 1. A page past the end returns an empty `Objects` with accurate totals.
- A route may set a lower maximum; it documents it and normalizes the same way.
- The page is cut in the data source: SQL `LIMIT/OFFSET` (or the provider equivalent) with a `COUNT` over the same
  filter, in every database provider (SQLite, PostgreSQL, MySQL, SQL Server). Filtering and authorization scope apply
  before counting, so `TotalRecords` is exact for the caller. Loading every row and slicing in memory is not allowed
  for stored rows.
- Ordering is deterministic: the requested `Order` (CreatedDescending by default) with the id as the tie-break, so
  pages never overlap or skip rows when timestamps are equal.
- REST: `POST .../enumerate` takes the query in the body (querystring values override it); a paged `GET` list takes it
  in the querystring and has a `POST .../enumerate` twin. A malformed body is 400. MCP: the tool takes `pageNumber`
  and `pageSize` and returns the same page shape.

## Collections that are not stored rows

| Kind | Rule | Limit |
|---|---|---|
| Children that grow with operational data (missions of a voyage, vessels of a fleet, transcript messages, tracked work, dependencies, resources, results) | Not embedded in the parent read. The parent carries a count where useful; the children have their own paged enumeration. | Page bounds above |
| Ids or keys in one request (bulk delete, purge, reorder, cleanup keys) | Rejected when over the limit. A result list derived from the request (for example `Skipped`) is bounded by it. | `MaxRequestItems` = 1000 |
| Configuration lists and dictionaries stored on one entity (patterns, tags, headers, stages, priorities, inputs, parameters) | Every write path (REST, MCP, WebSocket, service) rejects a list over the limit. | `MaxConfigItems` = 256 |
| Link id lists stored on one entity (objective, release, deployment, incident links) | A write or link that would exceed the limit is rejected. | `MaxLinkedIds` = 1000 |
| Summary buckets (token usage, request history, mission history) | A window and bucket size that would produce more buckets is rejected. | `MaxSummaryBuckets` = 1000 |
| Mutation results | Scalars, ids, and counts. Per-item detail is bounded by the request limit or read through an enumeration. | As above |
| External sources (GitHub, git, the file system, Harbor) | Requested from the source in bounded pages and exposed through `EnumerationQuery` paging; never cut short without saying so. | Page bounds above |

Exceeding a limit is never a silent truncation. The request fails with `CollectionLimitExceededException`: REST 400
with `Data.Code` `CollectionLimitExceeded` (`CollectionLimitErrorDetail`: `Field`, `Limit`, `Actual`), MCP
`ErrorCode` `InvalidArgument` with `Code` `CollectionLimitExceeded`.

Every collection property of a model a route or tool returns declares its limit with `[CollectionBound(limit)]`
(`src/Armada.Core/Services/CollectionBoundAttribute.cs`, using the `CollectionLimits` constants), and every write path
calls `CollectionBounds.Validate(model)`, which enforces the declared limits on the model and every Armada model it
contains. The `E2E.ResponseShape` guard fails for a collection property reachable from a response that declares no
bound, and for any array in a live response that is neither a page's `Objects` nor a declared bounded property.

Collections fixed in code (for example the Ask quick-action catalog, the doctor checks, or the six model probe results)
are bounded by construction and listed in the checklist with the code that bounds them.

## Checklist

The checklist below maps every surface from the inventory audit (voyage `vyg_mvsudvxf_OPT5n2zeNlp`, mission
`msn_mvsudvxs_1SwRANimKxU`, commit `634622de4`: 272 of 610 REST routes and MCP tools unbounded) to the migration that
closes it and the evidence. It is maintained with the code: a change that adds or reshapes a collection updates it.

<!-- CHECKLIST -->
