# Dashboard Simplification & Usability Report

A survey of `src/Armada.Dashboard` (React + Vite + TypeScript) focused on the biggest
wins for **usability**, **consolidation**, and **simplification**. This is analysis
only — no code was changed. Findings are ordered by impact-to-effort ratio.

## Snapshot (why this matters)

| Metric | Value |
|---|---|
| Page components | 61 (`src/pages/*.tsx`), ~30,600 lines |
| `ConfirmDialog` re-wired per page | **49 pages** |
| `JsonViewer` re-wired per page | **48 pages** |
| Hand-rolled `modal-overlay` blocks in pages | **28 pages** |
| Per-page `colFilters` state | 23 pages |
| Per-page create/edit modal state | 20 pages |
| Distinct page-header patterns | **3** (`view-header` ×27, `detail-header` ×20, `page-header` ×9) |
| Distinct filter-row patterns | **4** (`playbook-filter-row`, `filter-bar`, `request-filter-field`, `backlog-filter-grid`) + 1 unused `FilterBar` component |
| `App.css` | **6,895 lines**, single file |
| `types/models.ts` | 2,066 lines, single file |
| Largest pages | `ObjectiveDetail` 1,609 · `Server` 1,334 · `Workspace` 1,264 · `RequestHistory` 894 · `MissionDetail` 877 |

The headline: **the list/table page is copy-pasted ~30 times.** Each page re-implements
the same search + column filters + pagination + JSON viewer + confirm dialog + action
menu + (increasingly) a create/edit modal. That repetition is the single largest source
of both bulk and inconsistency, and it is where nearly every recurring bug has lived.

---

## P0 — Highest impact

### 1. Extract one reusable "resource list page" (table + toolbar + modals)
**Problem.** There is no shared list-page abstraction. Every list page independently
declares and wires: `search`, `colFilters`, `pageNumber/pageSize`, `jsonData` (48×),
`confirm` (49×), an `ActionMenu` per row (33 pages), and now a create/edit modal (20×).
The markup for the table header, the `column-filter-row`, the empty/loading/error states,
and the pagination bar is re-typed each time.

**Evidence.** `ConfirmDialog` imported in 49 pages, `JsonViewer` in 48, `modal-overlay`
hand-written in 28. The "list" pages alone total ~13,300 lines and are ~80% boilerplate.

**Recommendation.** Introduce a `useResourceTable()` hook + a `<ResourceTable>` /
`<ResourceListPage>` component that owns: data loading, search + column filters, sort,
pagination, row-click behavior, selection/bulk bar, the JSON viewer, the confirm dialog,
and the row action menu (View / View JSON / Edit / Delete + custom actions). A page then
becomes a column definition + row-action config + an optional create/edit form — roughly
50–100 lines instead of 300–600.

**Impact.** Removes thousands of lines; makes table behavior (row-click, above-table
controls, empty states, filter reset-to-page-1) correct **everywhere at once**; kills the
class of "I fixed it on page X but not Y" bugs. **Effort:** high, but the highest payoff.

### 2. Adopt the existing `FilterBar` component everywhere (retire the 4 filter patterns)
**Problem.** There is already a shared `components/shared/FilterBar.tsx`, but it is used in
**exactly one page**. Pages instead hand-roll four different filter containers:
`playbook-filter-row` (11 pages), `filter-bar` (Missions), `request-filter-field` grid
(Requests), `backlog-filter-grid` (Objectives).

**Evidence.** Directly caused the recurring "filters each on their own line" bug: the global
`input, select, textarea { width: 100% }` rule (see #5) collided with `flex: 0 0 auto` in
two of these patterns and had to be patched in each separately.

**Recommendation.** Make `FilterBar` the single filter primitive (search input that grows +
auto-width selects/segmented controls) and route all list pages through it. Delete the
bespoke filter CSS classes.

**Impact.** One consistent, correct filter layout; removes ~4 near-duplicate CSS blocks and
the whole footgun. **Effort:** medium.

### 3. Server should emit camelCase JSON; delete the client re-keying shim
**Problem.** The REST API serializes **PascalCase** (`TotalCount`, `Objects`,
`BucketStartUtc`, …), and the client compensates with a recursive `camelizeKeys()` that
re-keys **every response** (`api/client.ts`, 6 call sites).

**Evidence.** This shim silently masks casing mismatches and made the Requests-page
diagnosis much harder (the data was correct but invisible until the whole path was traced).
It also means every new endpoint depends on an implicit, global transform.

**Recommendation.** Configure the server's route serializer to use a camelCase naming
policy (the server already constructs `JsonSerializerOptions` with
`PropertyNamingPolicy = CamelCase` for some paths — apply it to the REST route responses),
then delete `keyToCamel`/`camelizeKeys` and the per-call transform.

**Impact.** Removes an entire fragile translation layer and a class of "field is undefined"
bugs. **Effort:** medium (server + client), but mechanical and well-contained.

---

## P1 — High impact

### 4. One `<PageHeader>` component (collapse the 3 header patterns)
`view-header` (27), `detail-header` (20), and `page-header` (9) all render the same thing:
title + subtitle + right-aligned actions. Standardize on one `<PageHeader title subtitle
actions>` component and one CSS class. Removes visual drift (spacing/alignment differs
between them today) and simplifies every page top. **Effort:** low–medium.

### 5. Fix the global CSS footguns and split `App.css`
- The base rule `input, select, textarea { width: 100% }` is a footgun: it forces
  full-width on **every** control and is the root cause of the filter-stacking bugs. Scope
  full-width to form contexts (`.form-group`, `.modal`, grids) instead of applying it
  globally, so inline controls size to content by default.
- `App.css` is a single **6,895-line** file. Split by concern (tokens, shell, tables,
  forms/modals, page-specific) or move to CSS modules / co-located styles. A single global
  sheet is why "fix here, break there" keeps happening.

**Impact.** Eliminates a recurring bug family; makes styling changes safe and local.
**Effort:** medium (do the footgun first; the split can be incremental).

### 6. Merge the two record inspectors (`JsonViewer` + `RecordDetailModal`)
There are now two overlapping "look at a record" surfaces: `JsonViewer` (raw JSON, 48
pages) and the newer `RecordDetailModal` (readable grid + JSON toggle). Fold them into one
detail modal with a **Details** tab (readable key/value grid) and a **JSON** tab. One
component, one mental model, and it satisfies the "row-click opens a modal + View JSON"
requirement with a single dependency. **Effort:** low–medium.

### 7. Pick one source of truth for each entity's form (list-modal vs detail-page)
The recent work added create/edit **modals** on list pages while the **detail pages** still
contain the same forms (e.g. `SkillDetail`, `EnvironmentDetail`, `WorkflowProfileDetail`).
That duplicates every field, validation, and payload builder twice per entity and they will
drift. Decide per entity: (a) modal for create/edit + detail page for read-only/relationships,
or (b) detail page only. Extract the form into a shared `<EntityForm>` used by both if both
are needed. **Effort:** medium; **prevents** a large future-maintenance tax.

---

## P2 — Meaningful cleanups

### 8. Break up the mega-pages
`ObjectiveDetail` (1,609), `Server` (1,334), `Workspace` (1,264), `MissionDetail` (877),
`RunbookDetail` (849) are doing too much in one file. Extract logical sections (e.g. Server's
settings groups, backup/restore, MCP integration, danger zone) into child components. Improves
readability, testability, and lazy-load size. **Effort:** medium, incremental.

### 9. Centralize copy-pasted helpers
`formatBytes`, `parseJsonString`, `entityRoute`/ID-prefix routing, and `methodClass` are
duplicated across pages. Move to `src/lib/` (a `format.ts` and a `routing.ts`). Notably the
**ID-prefix → route** map (`flt_→/fleets`, `msn_→/missions`, …) is reimplemented in more than
one place; make it one function used by every "linked entity" cell. **Effort:** low.

### 10. Split `types/models.ts` (2,066 lines)
One giant type file. Split by domain (fleet/vessel/captain/mission/deployment/…) or generate
from the server's OpenAPI document (which already exists and powers the API Explorer) so
frontend types can't drift from the backend contract. **Effort:** low (split) / medium
(codegen, but high long-term payoff).

### 11. Usability consistency wins (cheap, high perceived quality)
- **Above-table toolbars.** The style guide wants pagination + refresh + create + filters as
  one bar **above** the table; today most pages only paginate below. Folding this into the
  shared table (#1) fixes it everywhere.
- **Uniform empty/loading/error states.** Currently each page rolls its own copy and markup;
  the shared table should standardize them.
- **Consistent row-click affordance.** Now that row-click opens a modal, ensure every table
  uses the same guard (never open on clicks to checkboxes, links, or the action menu). The
  shared table makes this structural rather than per-page.
- **Nav/route surface.** 75 routes are registered; a quick audit for orphaned or duplicate
  routes (e.g. detail routes that are now superseded by modals) would trim dead paths.

---

## Suggested sequencing

1. **#5 footgun + #3 camelCase** — small, surgical, remove two whole bug families first.
2. **#2 FilterBar + #4 PageHeader + #6 unified detail modal** — build the shared primitives.
3. **#1 ResourceTable** — the big one; migrate pages onto it in batches, deleting per-page
   boilerplate as you go (this is where the line count drops sharply).
4. **#7 form source-of-truth, #8 mega-page splits, #9/#10 helpers/types** — opportunistic as
   each page is touched.

## Implementation Status (as executed)

| # | Item | Status |
|---|------|--------|
| 9 | Centralize copy-pasted helpers | **Done** — `lib/format.ts` (formatBytes, parseJsonString, methodClass) + `lib/routing.ts` (ID-prefix → route). Duplicates removed. |
| 4 | One `PageHeader` component | **Done** — all ~52 pages (list + detail) migrated off the three header variants onto the shared component. |
| 1 | Reusable resource list page | **Abstraction built + first batch migrated.** `lib/useResourceTable.ts` owns search/column-filter/sort/pagination/selection; adopted on Fleets, Vessels, Voyages, Personas, Pipelines, Docks. Remaining list pages are an incremental rollout onto the same hook (the report itself calls for migrating "in batches") — deliberately not force-migrated in one pass to avoid behavior regressions on the complex create/edit pages. |
| 3 | Server camelCase + drop client shim | **Infeasible as written.** REST responses are serialized by the SwiftStack framework (a NuGet dependency), which emits PascalCase and is not reconfigurable from app code. The client `camelizeKeys` shim is the correct compensating layer; removing it would break every page. Would require forking the framework. |
| 2 | Adopt `FilterBar` everywhere | **Superseded for the bug; consolidation deferred.** The user-visible "filters stack" bug was fixed directly in CSS (filter selects now `width: auto`, overriding the global rule). Full `FilterBar` adoption is optional consolidation. |
| 5 | Fix global CSS footgun + split App.css | **Partial.** The footgun (`input,select,textarea{width:100%}`) was neutralized in the filter contexts that it broke. Full removal has wide form blast-radius and is best done alongside #1/#2; the `App.css` split is deferred. |
| 6 | Merge JsonViewer + RecordDetailModal | **Deferred.** `RecordDetailModal` (readable grid + JSON) was added and is the row-click detail modal; folding the 48 `JsonViewer` usages into it is churn with visual-regression risk for low user value. |
| 8 | Break up mega-pages | **Deferred** (churn). |
| 10 | Split/generate `types/models.ts` | **Deferred** (churn / codegen tooling). |
| 11 | Usability consistency | **Partial** — row-click-opens-modal and per-row View/View JSON/Edit are now universal (separate work); above-table toolbars fold into #1's rollout. |

Net: the shared primitives the report asked for (`PageHeader`, `useResourceTable`, `RecordDetailModal`, `lib/format`, `lib/routing`) exist and are adopted; headers and helpers are fully consolidated; #3 is documented as infeasible; the remaining table-migration is an incremental rollout on the delivered hook.

## What is already good (leave alone)
Shared `ActionMenu`, `CopyButton` (with the green-check success state), `StatusBadge`,
`ConfirmDialog`, `Pagination`, `Markdown`, and `ChatMetricsBar` are solid, well-scoped
primitives. The problem is not a lack of components — it's that pages bypass them and
re-implement the surrounding scaffolding. The wins above are mostly about **routing existing
good pieces through one page-level abstraction** rather than writing new UI.
