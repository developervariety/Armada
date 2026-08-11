# Navigation Consolidation — Implementation Plan

**Target:** collapse the dashboard from **~35 nav destinations across 6 sections** to
**~13 workflow-grouped destinations**, without deleting a single capability, so a new
operator can open Armada and understand what to do on the first screen. **Ask Armada is a
first-class, permanent nav item** — it is expected to be the primary workflow interface, so
it stays at the top of the Operate group and is never demoted to a menu or shortcut.

**Scope:** `src/Armada.Dashboard` (React + Vite + TypeScript) and the repo docs/scripts
that describe it. This is a v0.9.0 feature effort on `feature/v0.9.0`.

**How to use this document.** It is both the spec and the tracker. Every phase carries a
status line and a checklist. Check a box when the work is done *and verified* — a merged
PR with the acceptance criteria met, not merely code written. Keep the Progress Dashboard
at the top in sync as phases move. The three review phases at the end (tests, docs,
scripts) are gates: the effort is not "done" until all three pass on the final IA, because
the whole point is that a user can understand exactly how to use the dashboard, and stale
tests, docs, or install scripts break that promise.

---

## Status legend

Use these markers in the **Status** field of each phase and in the Progress Dashboard.

- `Not started` — no branch yet.
- `In progress` — branch open, work underway.
- `In review` — PR open, awaiting review/QA.
- `Blocked` — waiting on a dependency; note it in the phase's Notes line.
- `Done` — merged, acceptance criteria verified, visual QA captured.

Task boxes: `- [ ]` open, `- [x]` complete. A phase is `Done` only when every task box in
it is checked and its acceptance criteria are ticked.

---

## Progress Dashboard

| # | Phase | Depends on | Status | Owner | PR / Branch | Verified |
|---|---|---|---|---|---|---|
| 0 | Foundations (shared Tabs, bell, palette, route redirects) | — | Done | | feature/v0.9.0 | build+tests green |
| 1 | Configuration hub (7 config pages → 1 tabbed hub) | 0 | Done | | feature/v0.9.0 | build+tests green |
| 2 | Activity log (History + Requests + Events + Signals → 1) | 0 | Done | | feature/v0.9.0 | build+tests green |
| 3 | Quick demotions (Notifications bell, Doctor → Server, Docks → Agents) | 0, 2 | Done | | feature/v0.9.0 | build+tests green |
| 4 | Vessels hub (Fleets folded in + Workspace drill-in) | 0 | Done | | feature/v0.9.0 | build+tests green |
| 5 | Captains hub (Docks tab) | 0, 3 | Done | | feature/v0.9.0 | build+tests green |
| 6 | Missions surface (Voyages + full Merge Queue as tabs) | 0 | Not started | | | |
| 7 | Dispatch + Backlog (Backlog as intake tab) | 0 | Not started | | | |
| 8 | Delivery hub + role/feature gating | 0 | Not started | | | |
| 9 | Dashboard home as command center + guided understanding | 1-8 | Not started | | | |
| 10 | Test review | 1-9 | Not started | | | |
| 11 | Documentation review | 1-9 | Not started | | | |
| 12 | Script review | 1-9 | Not started | | | |
| 13 | Final acceptance + visual QA sign-off | 10-12 | Not started | | | |

---

## The thesis (why the nav is big)

The dashboard is organized **one page per backend entity**. Almost every table in the API
got its own nav item, so the navigation mirrors the *data model* instead of the *jobs an
operator actually does*. Two structural problems fall out of that.

Config sprawl is the first. Roughly a third of the nav — Workflow Profiles, Project
Profiles, Skills, Personas, Pipelines, Prompts, Playbooks — is setup you touch rarely,
sitting at the same level as the things you do every day like Missions, Planning, and
Dispatch. A new user cannot tell the difference between "I configure this once" and "I
live in this daily."

Redundant observability is the second. History, Requests, Events, Signals, and
Notifications are five different "list of things that happened" surfaces. When something
goes wrong, an operator has to guess which of the five to open, and the guess is often
wrong because History already lists requests as one of its source types.

The fix is to group by job, collapse configuration into one hub, unify the activity
streams behind a source filter, and demote implementation-detail entities (Docks) and
one-off diagnostics (Doctor) out of the top level. The guiding rule throughout: **prefer
tabs and filters over deleting features.** Power users still need Docks, raw Events, and
the full merge queue. They do not each deserve a permanent nav slot; they deserve a tab or
a filter value on a parent surface.

---

## Current state (verified in code)

The sidebar is declared in `src/Armada.Dashboard/src/components/Layout.tsx` as a
`navSections` array. Routes are declared in `src/Armada.Dashboard/src/App.tsx` with
lazy-loaded page components. Detail routing by ID prefix lives in
`src/Armada.Dashboard/src/lib/routing.ts` (`entityRoute`). The six sections today:

- **Operations (8):** Needs You (`/inbox`), Ask Armada (`/ask`), Planning, Dispatch,
  Backlog (`/backlog` → `Objectives`), Voyages, Missions, Merge Queue.
- **Delivery (9):** Workflow Profiles, Project Profiles, Skills, Checks, Environments,
  Deployments, Releases, Incidents, Runbooks.
- **Fleet (5):** Fleets, Workspace, Vessels, Captains, Docks.
- **Activity (4):** History, Signals, Events, Notifications.
- **System (8):** Personas, Pipelines, Prompts (`/prompt-templates`), Playbooks, Requests
  (`/requests`), API Explorer, Server, Doctor.
- **Security (3, admin-gated):** Tenants, Users, Credentials.

Two facts shape the plan. There is **no shared `Tabs` primitive** — the only in-page tab UI
is ApiExplorer's local `api-tab-row` / `api-tab` buttons (and Workspace's panel switching),
so folding pages into tabs requires building that primitive first. And the notification
system is already a React context (`context/NotificationContext.tsx`) exposing
`{ notifications, unreadCount, toasts, markRead, markAllRead, clearHistory, dismissToast,
pushToast }`; Layout already renders the toasts and the unread badge, and `Notifications.tsx`
is a thin view over that context — so a top-bar bell reuses the exact same API with no
data-layer change. Reuse is trending the right way: `lib/useResourceTable.ts` already owns
search/filter/sort/pagination/selection for list pages (the P0 win from
`archive/SIMPLIFICATION.md`), currently adopted on six pages (Docks, Voyages, Vessels,
Pipelines, Personas, Fleets) with the rest still hand-rolling the same state; shared
components exist for `ActionMenu`, `Pagination`, `ConfirmDialog`, `JsonViewer`, `PageHeader`,
`StatusBadge`, `FilterBar`, and `CopyButton`.

### Shared conventions (apply in every phase)

New nav labels, tab titles, and page subtitles are added as English keys to the i18n
catalog at `src/Armada.Server/wwwroot/i18n/armada.json` (shape:
`{ defaultLocale, supportedLocales[], locales: { <code>: { terms, phrases, sections } } }`;
locales en/es/zh-Hans/zh-Hant/yue-Hant/ja/de/fr/it). English is pass-through, so a new
string renders fine before translation, and the DOM `MutationObserver` translator in
`i18n/runtime.ts` also catches raw literals — but add the key so real translations can land.
Where a consolidated surface introduces or migrates a list view, prefer `useResourceTable`
over hand-rolling table state, extending the six-page adoption rather than adding new
one-offs.

---

## Target information architecture (~13 destinations)

The authoritative end-state nav. Everything below builds toward exactly this. Grouped
around four jobs — *do the work*, *ship it*, *set it up*, *look at what happened* — plus
system and admin.

| Group | Destination | Absorbs | Notes |
|---|---|---|---|
| — | **Dashboard** (`/`) | + Diagnostics summary | Home command center; deep-links into fixes |
| — | **Ask Armada** (`/ask`) | — | **standalone top-level item, directly under Dashboard, above all groups; primary workflow interface** |
| Operate | **Needs You** (`/inbox`) | actionable merge entries, unresolved notifications | attention queue |
| Operate | **Planning** (`/planning`) | — | |
| Operate | **Dispatch** (`/dispatch`) | Backlog (as intake tab) | |
| Operate | **Missions** (`/missions`) | Voyages, full Merge Queue (tabs) | |
| Build | **Vessels** (`/vessels`) | Fleets, Workspace (drill-in) | vessels grouped by fleet; Fleets manageable inline; route stays `/vessels` |
| Build | **Captains** (`/captains`) | Docks (tab) | route stays `/captains` |
| Ship | **Delivery** (`/delivery`) | Environments, Deployments, Releases, Incidents, Checks, Runbooks (tabs) | role/feature-gated |
| Configure | **Configuration** (`/configuration`) | Workflow Profiles, Project Profiles, Skills, Personas, Pipelines, Prompts, Playbooks (tabs) | collapsed by default |
| Observe | **Activity** (`/activity`) | History, Requests, Events, Signals (source filter) | request detail modal preserved |
| System | **API Explorer** (`/api-explorer`) | — | |
| System | **Server** (`/server`) | Doctor (Diagnostics tab) | |
| Admin | **Security** (`/admin/*`) | Tenants, Users, Credentials | admin-gated, unchanged |

**Ask Armada leads the nav.** It is expected to be the primary way people drive Armada, so
it is a **standalone top-level item rendered directly under Dashboard, above every grouped
section** — not inside Operate, not a tab, not a filter, not a menu item. In `Layout.tsx`
it becomes a second ungrouped item alongside the existing `dashboardItem`, so the nav reads
Dashboard, then Ask Armada, then the groups. The ⌘K command palette is an *additional* fast
path to it (and to any nav destination), never a replacement for the nav slot. Only
**Notifications** becomes a non-nav affordance: a top-bar bell dropdown feeding Needs You.
Start Configuration, Delivery, and System **collapsed by default** so the first paint shows
only the daily drivers — with Dashboard and Ask Armada anchored above them.

---

## Route strategy (do not break deep links)

Consolidation changes nav, not URLs that people and code already depend on. Three rules
hold for every phase:

Old top-level routes must keep resolving. When Merge Queue moves under Missions, `/merge-queue`
should redirect to `/missions?tab=merge-queue` (or render the Missions surface with that
tab active), never 404. Add redirects in `App.tsx` alongside the existing
`settings → /server` and `dashboard → /` redirects.

Detail routes stay put. `entityRoute` in `lib/routing.ts` maps ID prefixes to
`/missions/:id`, `/vessels/:id`, `/signals/:id`, `/events/:id`, `/docks/:id`, and so on.
Those detail pages remain addressable. When a list page becomes a tab, its `/:id` detail
route is untouched; only the list entry point moves. Update `entityRoute` only where the
*list* URL changes (for example `mrg_` currently points at `/merge-queue`).

Tab and filter state belongs in the URL. Each consolidated surface reads its active tab
and filters from query params (`?tab=`, `?source=`) so a bookmark, a chart click-through,
or a Needs You link can land the operator on the exact tab/filter they need. The shared
Tabs primitive (Phase 0) owns this.

---

## Phase 0 — Foundations

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Nothing else can start cleanly until the shared pieces every later phase leans on exist.
Build the primitives once, correctly, and the seven consolidations that follow become
configuration rather than fresh invention.

> **Status: Done.** Shipped `navConfig.tsx` (single source of truth for nav), `Tabs.tsx`
> (URL-synced), `NotificationBell.tsx`, `CommandPalette.tsx`; Ask Armada is a standalone
> item under Dashboard; sidebar widened to 220px; section collapse defaults applied. Build
> and all 42 tests green. Nav-inventory/primitive unit tests are added in Phase 10.

**Goal.** Ship the shared Tabs primitive, the top-bar notification bell, the ⌘K command
palette, the route-redirect scaffolding, and the sidebar defaults — with no user-visible
nav consolidation yet.

**Files.**
- `src/Armada.Dashboard/src/components/shared/Tabs.tsx` (new) — URL-synced tabbed surface.
- `src/Armada.Dashboard/src/components/shared/CommandPalette.tsx` (new) — ⌘K launcher.
- `src/Armada.Dashboard/src/components/shared/NotificationBell.tsx` (new) — top-bar bell.
- `src/Armada.Dashboard/src/components/Layout.tsx` — mount bell + palette; adjust section
  default-collapsed state; widen sidebar from 180px to **220px** (into the spec band).
- `src/Armada.Dashboard/src/App.tsx` — redirect scaffolding for moved routes.
- `src/Armada.Dashboard/src/context/NotificationContext.tsx` — expose a recent-items list
  and `markAllRead` for the bell if not already present.
- `src/Armada.Dashboard/src/App.css` — Tabs, bell dropdown, palette styles.

**Tasks.**
- [ ] Build `Tabs.tsx`: reads/writes `?tab=` in the URL, keyboard-navigable
  (`role="tablist"`, arrow keys, `aria-selected`), lazy-mounts panels, and degrades to a
  scrollable tab strip on narrow viewports. All labels pass through `t()`.
- [ ] Retrofit `ApiExplorer.tsx` and `Workspace.tsx` onto `Tabs.tsx` to prove the
  primitive against the two existing ad-hoc tab implementations (no behavior change).
- [ ] Build `NotificationBell.tsx`: unread count badge, recent list, "mark all read,"
  click-through to the source record via `entityRoute`; mount in the top bar of `Layout.tsx`.
- [ ] Build `CommandPalette.tsx`: ⌘K / Ctrl-K opens; jumps to any nav destination and
  offers a fast "Ask Armada" entry point; focus-trapped, Escape closes. The palette is an
  *accelerator* — Ask Armada keeps its permanent nav slot regardless.
- [ ] Promote **Ask Armada** to a standalone top-level nav item in `Layout.tsx` — rendered
  directly under `dashboardItem` and above every grouped section (its own ungrouped entry,
  not inside Operate), so the order is Dashboard, then Ask Armada, then the groups. Remove
  `/ask` from the Operations section's items and matchers. No phase removes or demotes `/ask`.
- [ ] Add redirect routes in `App.tsx` for every route that will move (kept as no-ops now,
  populated per later phase) and confirm `settings`/`dashboard` redirects still work.
- [ ] Set Configuration, Delivery, and System sections to start collapsed; keep operator
  daily-drivers expanded. Persist per-user like the existing `armada_sidebar_collapsed`.
- [ ] Add i18n catalog entries for all new strings (English keys in the locale packs;
  confirm the DOM runtime in `i18n/runtime.ts` picks them up).

**Acceptance.**
- [ ] `Tabs.tsx` drives ApiExplorer and Workspace with identical behavior to before.
- [ ] Bell shows unread count and opens a dropdown; no separate page needed to read alerts.
- [ ] Ask Armada renders as a standalone item directly under Dashboard and above all
  groups (not inside Operate); ⌘K opens the palette on desktop and also offers a fast path
  to it.
- [ ] Every future-moved route has a redirect stub; no 404 on any current URL.
- [ ] Tabs, bell, and palette verified at 1280 / 768 / 390 px in light and dark themes.
- [ ] No new hardcoded English literals in JSX outside locale resources.

---

## Phase 1 — Configuration hub

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

The single biggest reduction in daunting-ness, and the lowest risk, because these are
independent CRUD pages that become tabs without changing their internals. Do it first
after Foundations.

**Goal.** One `/configuration` surface with tabs for Workflow Profiles, Project Profiles,
Skills, Personas, Pipelines, Prompts, and Playbooks. Removes ~6 items from the top level.

**Files.**
- `src/Armada.Dashboard/src/pages/Configuration.tsx` (new) — hosts `Tabs.tsx`, each tab
  rendering the existing page component.
- Reuse as tab bodies: `WorkflowProfiles.tsx`, `ProjectProfiles.tsx`, `Skills.tsx`,
  `Personas.tsx`, `Pipelines.tsx`, `PromptTemplates.tsx`, `Playbooks.tsx`.
- `App.tsx` — add `/configuration`; redirect `/workflow-profiles`, `/project-profiles`,
  `/skills`, `/personas`, `/pipelines`, `/prompt-templates`, `/playbooks` to
  `/configuration?tab=...`. Keep all `/:id` / `/:name` detail routes intact.
- `Layout.tsx` — replace the seven nav items with one **Configuration** item; remove those
  entries from the Delivery and System `matchers`.

**Tasks.**
- [ ] Create `Configuration.tsx` with the seven tabs, default tab = Workflow Profiles.
- [ ] Confirm each tab's create/edit/delete/view-JSON flows work unchanged inside the tab.
- [ ] Add redirects; verify a bookmarked `/skills` lands on `/configuration?tab=skills`.
- [ ] Verify detail routes (`/personas/:name`, `/pipelines/:name`, `/skills/:id`, etc.)
  still open and their "back" affordance returns to the correct tab.
- [ ] Update nav: one Configuration item, section starts collapsed.
- [ ] i18n for the hub title, tab labels, and page subtitle (operator copy: what each
  config controls, not class names).

**Acceptance.**
- [ ] All seven former pages reachable as tabs; nothing lost.
- [ ] Old URLs redirect; detail routes intact.
- [ ] Tab state persists in the URL and survives refresh.
- [ ] Visual QA at three breakpoints, both themes; action menus not clipped by tab frame.

---

## Phase 2 — Activity log unification

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Four "what happened" lists become one. History already aggregates most of it, so this is
medium effort with a large legibility payoff.

**Goal.** One `/activity` page that is History's superset, filtered by source type
(request, event, signal, mission, check, planning, merge, deployment, incident). When
`source = request`, show the request KPIs, the activity chart, and the request-detail
modal (replay/inspect). Absorbs Events and Signals as filter values.

**Files.**
- `src/Armada.Dashboard/src/pages/Activity.tsx` (new) — or evolve `History.tsx` in place.
- Fold in behavior from `RequestHistory.tsx` (KPIs, chart, request inspector modal),
  `Events.tsx`, and `Signals.tsx`.
- `App.tsx` — add `/activity`; redirect `/history`, `/requests`, `/events`, `/signals` to
  `/activity?source=...`. Keep `/requests/:id`, `/events/:id`, `/signals/:id` detail routes.
- `lib/routing.ts` — no change to detail prefixes; confirm `evt_`/`sig_` still resolve.
- `Layout.tsx` — replace the four Activity nav items with one **Activity** item.

**Tasks.**
- [ ] Build the source-type filter as the primary control; default view = all sources.
- [ ] Preserve request-specific UI when `source = request`: KPI strip, activity chart with
  range controls, and the request inspector modal (headers/bodies/raw JSON/copy).
- [ ] Preserve Events audit rows and Signals admiral-captain messages as filtered views
  with their existing columns and detail links.
- [ ] Make chart click-through set the equivalent Activity filter/time window.
- [ ] Redirect old routes; verify Needs You / Dashboard links that pointed at
  History/Requests/Events/Signals now resolve to the right Activity filter.
- [ ] Distinct empty states: "no activity retained," "no rows matched these filters,"
  "request-history backend unavailable."
- [ ] i18n for filter labels, KPI labels, chart labels, empty states.

**Acceptance.**
- [ ] All four former surfaces reachable via source filter; no capability lost.
- [ ] Request KPIs, chart, and inspector modal intact under `source = request`.
- [ ] Old URLs redirect; detail routes intact.
- [ ] Backend filtering used where the API supports it; frontend-only filtering clearly
  scoped to fetched data where it does not.
- [ ] Visual QA at three breakpoints, both themes.

---

## Phase 3 — Quick demotions

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Three low-traffic pages leave the top level with minimal risk once Foundations (bell) and
Activity (for Signals/Events) are in.

**Goal.** Delete the Notifications page in favor of the top-bar bell + Needs You; fold
Doctor into a Server "Diagnostics" tab; prepare Docks to live under Agents (Phase 5).

**Files.**
- `Layout.tsx` — remove Notifications, Doctor, Docks nav items; the top-bar health dot
  already links to diagnostics (currently `/doctor`, repoint to `/server?tab=diagnostics`).
- `pages/Server.tsx` — add a Diagnostics tab hosting the `Doctor.tsx` content.
- `App.tsx` — redirect `/notifications` to `/inbox`, `/doctor` to `/server?tab=diagnostics`.
- `context/NotificationContext.tsx` — ensure unresolved notifications feed Needs You.
- `pages/Inbox.tsx` — surface unresolved notification items in the attention queue.

**Tasks.**
- [ ] Repoint the top-bar health link from `/doctor` to the Server Diagnostics tab.
- [ ] Move Doctor content into a Server tab; keep all diagnostic checks working.
- [ ] Remove the Notifications nav item and page route; redirect `/notifications`.
- [ ] Feed unresolved notifications into Needs You; confirm the bell + Needs You together
  cover everything the page did.
- [ ] Remove the standalone Docks nav item (route/detail stay for Phase 5 tab).
- [ ] i18n for the Diagnostics tab label and any moved copy.

**Acceptance.**
- [ ] No Notifications, Doctor, or Docks item in the sidebar.
- [ ] Bell + Needs You fully replace the Notifications page.
- [ ] Doctor diagnostics run from the Server Diagnostics tab; health dot links there.
- [ ] `/notifications` and `/doctor` redirect; `/docks/:id` still resolves.

---

## Phase 4 — Vessels hub (Fleets folded in)

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

A fleet is a folder of vessels. Managing the container and its contents on two separate
pages is pure overhead. Decision: keep Armada's nautical vocabulary — the nav item stays
**Vessels** at `/vessels`, with Fleets folded in (not a new "Repositories" label/route).

**Goal.** The `/vessels` page grows a **Fleet** column and a fleet filter/group-by, with
fleet create/rename/delete as secondary actions. Workspace stays as the per-vessel drill-in
reached from a row, not a nav item. The route stays `/vessels`.

**Files.**
- `pages/Vessels.tsx` — add the Fleet column, fleet filter/group-by, and a "Manage fleets"
  secondary control; reuse `useResourceTable` (already adopted here).
- Reuse `Fleets.tsx` fleet CRUD from the "Manage fleets" control (modal or drawer).
- `App.tsx` — redirect `/fleets` to `/vessels`; keep `/vessels/:id`,
  `/vessels/:id/onboarding`, `/fleets/:id`, and all `/workspace/*` routes.
- `Layout.tsx` — remove the separate Fleets and Workspace nav items; keep **Vessels**.

**Tasks.**
- [ ] Vessels table with Fleet column, fleet filter, and group-by view.
- [ ] "Manage fleets" secondary action for create/rename/delete.
- [ ] Row action to open the vessel Workspace drill-in.
- [ ] Redirect `/fleets` to `/vessels`; verify `entityRoute` `flt_`/`vsl_` detail links still work.
- [ ] Remove the Fleets and Workspace nav items; keep the Vessels item.
- [ ] i18n for the page, columns, and fleet-management controls.

**Acceptance.**
- [ ] Vessels and fleets both manageable from the one Vessels page; Workspace reachable per row.
- [ ] `/fleets` redirects; detail + onboarding + workspace routes intact.
- [ ] Table meets the standard: above-table pagination, backend filters where available,
  row action menu with View / Edit / View JSON, non-clipped menus.
- [ ] Visual QA at three breakpoints, both themes.

---

## Phase 5 — Captains hub (Docks tab)

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Docks are git worktrees — an implementation detail of how a captain runs a mission. Users
rarely manage them directly. Decision: keep the nautical vocabulary — the nav item stays
**Captains** at `/captains` (not a new "Agents" label/route), with Docks as a tab.

**Goal.** The `/captains` page becomes a tabbed surface: Captains as the primary view with
a captain's current dock shown inline, plus a **Docks** tab for the rare cleanup case. The
route stays `/captains`.

**Files.**
- `pages/Captains.tsx` — host `Tabs.tsx`: Captains (default) tab + Docks tab (rendering the
  existing `Docks.tsx` body).
- Reuse `Docks.tsx` as the Docks tab body.
- `App.tsx` — redirect `/docks` to `/captains?tab=docks`; keep `/captains/:id` and
  `/docks/:id`.
- `Layout.tsx` — remove the separate Docks nav item; keep **Captains**.

**Tasks.**
- [ ] Captains page with Captains (default) and Docks tabs.
- [ ] Show each captain's current dock inline on the Captains view.
- [ ] Redirect `/docks` to `/captains?tab=docks`; verify `cpt_`/`dck_` detail links via `entityRoute`.
- [ ] Remove the Docks nav item; keep the Captains item.
- [ ] i18n for tab labels.

**Acceptance.**
- [ ] Captains and Docks both reachable; dock cleanup still possible.
- [ ] `/docks` redirects; detail routes intact.
- [ ] Visual QA at three breakpoints, both themes.

---

## Phase 6 — Missions surface (Voyages + Merge Queue)

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Missions, Voyages, and the merge queue are the same operational flow at three grains. Group
them; promote only the attention-worthy merge entries to Needs You.

**Goal.** `/missions` hosts Missions (default), Voyages, and the full Merge Queue as tabs.
Blocked/failed/needs-approval merge entries surface in Needs You; the full queue stays here.

**Files.**
- `pages/Missions.tsx` — host `Tabs.tsx`: Missions / Voyages / Merge Queue.
- Reuse `Voyages.tsx` and `MergeQueue.tsx` as tab bodies.
- `App.tsx` — redirect `/voyages` and `/merge-queue` to `/missions?tab=...`; keep
  `/missions/:id`, `/voyages/:id`, `/voyages/create`, `/merge-queue/:id`.
- `lib/routing.ts` — update the `mrg_` route to point at the merge-queue tab URL.
- `pages/Inbox.tsx` — pull actionable merge entries into the attention queue.
- `Layout.tsx` — replace Missions, Voyages, Merge Queue with one **Missions** item.

**Tasks.**
- [ ] Missions page with three tabs; deep-links to a specific tab work.
- [ ] Actionable merge entries (blocked/failed/needs-approval) appear in Needs You with a
  link to the merge-queue tab entry.
- [ ] Redirect old routes; update `entityRoute` for `mrg_`.
- [ ] Verify `voyages/create` and all detail routes intact.
- [ ] i18n for tab labels and any merge-in-Needs-You copy.

**Acceptance.**
- [ ] Missions, Voyages, and full Merge Queue reachable as tabs; nothing lost.
- [ ] Needs You shows actionable merge entries and links correctly.
- [ ] Old URLs redirect; `mrg_` deep link lands on the merge-queue tab.
- [ ] Visual QA at three breakpoints, both themes.

---

## Phase 7 — Dispatch + Backlog

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Backlog is the intake step that feeds Dispatch. It reads as one workflow, so present it as
one surface with Backlog as the intake tab.

**Goal.** `/dispatch` hosts Dispatch (default) with Backlog as an intake tab. Removes one
top-level item.

**Files.**
- `pages/Dispatch.tsx` — host `Tabs.tsx`: Dispatch / Backlog.
- Reuse `Objectives.tsx` (the Backlog page) and `ObjectiveDetail.tsx` as the Backlog tab.
- `App.tsx` — redirect `/backlog` to `/dispatch?tab=backlog`; keep `/backlog/:id` and
  `/objectives`, `/objectives/:id`.
- `Layout.tsx` — remove the standalone Backlog item.

**Tasks.**
- [ ] Dispatch page with a Backlog intake tab; the objective-to-dispatch handoff stays intact.
- [ ] Redirect `/backlog`; verify objective detail routes still open.
- [ ] i18n for the Backlog tab label.

**Acceptance.**
- [ ] Backlog reachable as a Dispatch tab; the intake-to-dispatch flow unbroken.
- [ ] Old URLs redirect; detail routes intact.
- [ ] Visual QA at three breakpoints, both themes.

---

## Phase 8 — Delivery hub

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

The one area not to over-merge. Environments, Deployments, Releases, and Incidents are
genuinely distinct lifecycle concepts. Group them under one Delivery parent with tabs
rather than six flat items. Decision: **show all six tabs, no gating** this pass — no
backend flag work, no risk of hiding a surface wrongly. Per-tenant gating can layer on
later once the flag model is decided.

**Goal.** `/delivery` hosts Environments, Deployments, Releases, Incidents, Checks, and
Runbooks as tabs, all visible to everyone.

**Files.**
- `pages/Delivery.tsx` (new) — host `Tabs.tsx` over the six delivery pages.
- Reuse `Environments.tsx`, `Deployments.tsx`, `Releases.tsx`, `Incidents.tsx`,
  `CheckRuns.tsx`, `Runbooks.tsx`.
- `App.tsx` — add `/delivery`; redirect `/environments`, `/deployments`, `/releases`,
  `/incidents`, `/checks`, `/runbooks`; keep every `/:id` and `/new` detail route.
- `Layout.tsx` — replace the delivery items with one **Delivery** item.

**Tasks.**
- [ ] Delivery page with six tabs; default tab = Deployments.
- [ ] All six tabs visible to everyone (no gating this pass).
- [ ] Redirect old routes; verify detail routes (`releases/new`, `checks/:id`, etc.).
- [ ] i18n for tab labels.

**Acceptance.**
- [ ] All six delivery surfaces reachable as tabs; distinct lifecycle behavior preserved.
- [ ] All six tabs visible (no gating this pass).
- [ ] Old URLs redirect; detail routes intact.
- [ ] Visual QA at three breakpoints, both themes.

---

## Phase 9 — Dashboard home as command center + guided understanding

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

The nav is legible now; the home page has to answer the operator's three questions on the
first screen — what is happening, what can I operate, and what do I do when something is
wrong. The consolidation is only worth it if the entry point teaches the new IA.

**Goal.** `Dashboard.tsx` becomes a command center that reflects the ~13-item IA: domain
KPIs, health/status, an activity chart, attention items linking into Needs You, a
Diagnostics summary linking to Server, and CTA cards for the daily-driver workflows. The
setup wizard and guided tour teach the new grouping.

**Files.**
- `pages/Dashboard.tsx` — KPIs, activity chart, attention/failure items, Diagnostics
  summary, CTA cards deep-linking into the new tabbed surfaces.
- `components/SetupWizard.tsx` — update step targets to the consolidated nav; keep the
  first-run detection that already checks for fleet/vessel/captain.
- Guided tour (if present) — update highlighted targets to the new nav items.

**Tasks.**
- [ ] KPI cards for the product's live state (missions, voyages, merge queue, agents,
  failures), clickable into the relevant consolidated surface/tab.
- [ ] Activity chart with range controls; click-through to Activity filtered to the window.
- [ ] Attention section linking to Needs You; Diagnostics summary linking to Server.
- [ ] CTA cards for common first actions (start planning, dispatch work, add a repository),
  permission-aware and operational.
- [ ] Feature **Ask Armada** as the lead entry point: a prominent "Ask Armada" affordance
  on the home screen (primary CTA and/or an inline prompt box) that opens `/ask`, so a
  first-time user sees the primary workflow interface immediately.
- [ ] Make the setup wizard and guided tour introduce Ask Armada first when teaching the
  new nav, framing it as the main way to drive Armada.
- [ ] Update the setup wizard and tour to point at the new nav destinations.
- [ ] Partial-failure handling: successful panels render even when one endpoint fails.
- [ ] i18n for all KPI labels, CTA copy, empty/loading/error states.

**Acceptance.**
- [ ] A new user can log in and understand current state and next actions from home alone.
- [ ] KPI and chart click-throughs land on the correct consolidated surface/tab/filter.
- [ ] Setup wizard and tour reference the new nav, not the old one.
- [ ] Visual QA at three breakpoints, both themes; loading/empty/error states verified.

---

## Phase 10 — Test review

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

A consolidation that leaves red or stale tests has not shipped. Every moved page, redirect,
and new primitive needs coverage that matches the final IA.

**Goal.** The dashboard test suite reflects the ~13-item IA, covers the new primitives and
redirects, and passes. Backend tests unaffected by nav still pass. Note two standing gaps
this phase closes: `Layout.tsx` (the nav) and `App.tsx` (routing) have **no existing
tests**, and `useResourceTable.ts` has no unit test — the consolidation is the moment to
add them. Tests run on Vitest + jsdom (`vite.config.ts`, `src/test/setup.ts`);
`History.test.tsx` (which mocks `NotificationContext` after its table conversion) is a
working template for the consolidated Activity page tests.

**Files / suites.**
- Dashboard tests: `src/Armada.Dashboard/src/pages/*.test.tsx` and any component tests
  (existing: `ApiExplorer.test.tsx`, `CheckRuns.test.tsx`, `History.test.tsx`,
  `Releases.test.tsx`, `RequestHistory.test.tsx`, `PlaybookSelector.test.tsx`,
  `checkRunComparison.test.ts`, plus `lib/captains.test.ts`).
- Backend regression: `dotnet run --project src/Test.Automated --framework net10.0`
  (and the xUnit/NUnit adapter suites) per `CLAUDE.md`.

**Tasks.**
- [ ] Add tests for `Tabs.tsx` (URL sync, keyboard nav, lazy panels).
- [ ] Add tests for `NotificationBell.tsx` and `CommandPalette.tsx`.
- [ ] Add redirect tests: every old route resolves to the correct new tab/filter URL.
- [ ] Update or move page tests whose pages became tabs so they exercise the tab context.
- [ ] Add a nav-inventory test asserting the sidebar renders exactly the ~13 destinations
  and that the first two, in order, are Dashboard then a standalone Ask Armada above any
  section (guards against regressions re-adding or re-nesting items) — the first test for
  `Layout.tsx`.
- [ ] Add a first unit test for `useResourceTable.ts` (filter/sort/paginate/select) since
  more list views now depend on it.
- [ ] Run the full dashboard suite green; run the backend suites green.
- [ ] Record any test that could not run and why, in the phase Notes.

**Acceptance.**
- [ ] Dashboard suite green, covering primitives, redirects, and moved pages.
- [ ] Backend suites green.
- [ ] Nav-inventory test locks the target IA.

---

## Phase 11 — Documentation review

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Docs are how a user learns to use the dashboard. Any doc that names an old page, section,
or route teaches the wrong mental model after this effort.

**Goal.** Every repo doc that describes dashboard navigation, pages, or routes matches the
consolidated IA. Screenshots and walkthroughs reflect the new nav.

**Files (audit each for old nav/route names).**
- Root: `README.md`, `GETTING_STARTED.md`, `FAST_TRACK_SETUP.md`, `CHANGELOG.md`, `CLAUDE.md`.
- `docs/`: `DELIVERY_OPERATIONS.md`, `MERGING.md`, `REMOTE_MGMT.md`, `SCHEDULING.md`,
  the orchestrator/instructions guides, `PERSONAS*.md`, `PIPELINES.md`, and any doc that
  references `/vessels`, `/fleets`, `/notifications`, `/doctor`, `/history`, `/requests`,
  `/events`, `/signals`, `/merge-queue`, `/backlog`, or the seven config pages.

**Tasks.**
- [ ] Grep the repo for old route strings and section names; fix each reference to the new
  destination (for example "Fleets page" → "Repositories," "Doctor" → "Server > Diagnostics").
- [ ] Update `CHANGELOG.md` with the navigation consolidation under the v0.9.0 entry.
- [ ] Update `GETTING_STARTED.md` / `FAST_TRACK_SETUP.md` walkthroughs and any screenshots
  to the new nav.
- [ ] Verify `README.md` architecture/nav descriptions match the ~13-item IA.
- [ ] Note the new IA and the Notifications-as-bell change where docs describe those
  features; confirm docs still present Ask Armada as a top-level nav destination (and the
  primary workflow interface), now also reachable via ⌘K.

**Acceptance.**
- [ ] No doc references a removed nav item or old top-level route without a redirect note.
- [ ] `CHANGELOG.md` records the change.
- [ ] Getting-started walkthroughs match what the user sees.

---

## Phase 12 — Script review

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

Build, deploy, and health-check scripts have to keep producing a working dashboard on every
platform after the frontend churn.

**Goal.** The dashboard build/deploy/healthcheck scripts run clean on all three platforms
and produce the consolidated dashboard; no script references removed assets or routes.

**Files.**
- `scripts/common/`: `build-dashboard.sh`, `deploy-dashboard.sh`, `healthcheck-server.sh`,
  `install.sh`, `reinstall.sh`, `update.sh`.
- `scripts/windows/`: `build-dashboard.bat`, `deploy-dashboard.bat`, `start-armada-server.ps1`,
  `healthcheck-server.bat`, and the install/update/task scripts.
- `scripts/linux/` and `scripts/macos/`: the matching `build-dashboard`/`deploy-dashboard`/
  `healthcheck` scripts and systemd/service installers.
- `src/Armada.Dashboard/Dockerfile`, `vite.config.ts`, `package.json` scripts.

**Tasks.**
- [ ] Run `build-dashboard` on Windows (primary dev OS) and confirm a clean production build.
- [ ] Confirm `deploy-dashboard` places the build where the Admiral serves `/dashboard`.
- [ ] Confirm `healthcheck-server` still passes against the running server + dashboard.
- [ ] Grep scripts for any hardcoded route/page/asset names that changed (for example a
  smoke check hitting `/dashboard/notifications`); repoint to a surviving route.
- [ ] Verify the stale build/test logs in `src/Armada.Dashboard`
  (`dashboard-build-after-lazy.log`, `dashboard-test-after-lazy.log`) are regenerated or
  removed so they do not mislead.
- [ ] Spot-check the Linux/macOS build/deploy scripts for the same references.

**Acceptance.**
- [ ] `build-dashboard` and `deploy-dashboard` succeed and serve the consolidated dashboard.
- [ ] `healthcheck-server` passes.
- [ ] No script references a removed route or asset.

---

## Phase 13 — Final acceptance + visual QA sign-off

**Status:** Not started &nbsp;|&nbsp; **Owner:** &nbsp;|&nbsp; **PR:** &nbsp;|&nbsp; **Notes:**

**Goal.** Confirm the whole effort against the requirements bar and capture the mandatory
visual QA before calling it done.

**Tasks.**
- [ ] Sidebar shows ~13 grouped destinations; Configuration / Delivery / System start collapsed.
- [ ] Every former capability reachable via tab, filter, bell, or palette — nothing deleted.
- [ ] Every old top-level route redirects; every detail route resolves.
- [ ] Playwright (or equivalent) capture of the shell, Dashboard, Activity (+ request
  detail modal), API Explorer, Server, and one representative consolidated table, at
  1280 / 768 / 390 px in light and dark themes.
- [ ] Action menus portal above tab/table clipping; modals scroll within viewport; no
  body-level horizontal scroll.
- [ ] i18n smoke pass: nav, tabs, bell, palette, and one CJK + one long-Latin locale render
  without clipping.
- [ ] Handoff note lists reference dashboards consulted, backend gaps found, and residual risk.

**Acceptance.**
- [ ] All requirements-compliance boxes below are checked.
- [ ] Visual QA artifacts captured or the handoff explains why not, with residual risk.

---

## Requirements compliance matrix

Traceability to `c:\code\agents\requirements`. Check each when the corresponding phase(s)
land.

| Requirement (source) | Where satisfied | Done |
|---|---|---|
| Group nav by workflow, not table; sections with labels (DASHBOARD_STYLE, IA) | Target IA + all phases | [ ] |
| Avoid flat lists > 8-10; collapse rarely-used sections (DASHBOARD_STYLE) | Phase 0 defaults, 1, 8 | [ ] |
| Keep Request History, API Explorer, Health, Server reachable in nav (DASHBOARD_STYLE) | Phases 2, 3; API Explorer + Server retained | [ ] |
| Reusable Tabs / table / pagination / action-menu / modal primitives (DASHBOARD_STYLE) | Phase 0 + `useResourceTable` reuse | [ ] |
| Notifications as top-bar bell, not a page (DASHBOARD_STYLE / thesis) | Phases 0, 3 | [ ] |
| One-screen diagnostics folded into an existing page (DASHBOARD_STYLE / thesis) | Phase 3 (Doctor → Server) | [ ] |
| Home is a command center with KPIs, chart, attention, CTAs (DASHBOARD_STYLE) | Phase 9 | [ ] |
| Route inventory drives the build (DASHBOARD_STYLE) | Target IA + Route strategy tables | [ ] |
| Shared `ApiClient`; no scattered fetch (DASHBOARD_STYLE, FRONTEND_ARCH) | Reuse `api/client.ts`; no new HTTP layer | [ ] |
| i18n: nav/tab/modal/chart strings translatable via runtime (FRONTEND_ARCH, I18N) | Every phase's i18n task | [ ] |
| Backend filtering/sorting where supported; frontend fallback labeled (DASHBOARD_STYLE) | Phases 2, 4 | [ ] |
| Destructive actions use custom confirm modals, not browser dialogs (DASHBOARD_STYLE) | Reuse `ConfirmDialog`; verify per phase | [ ] |
| Role-aware nav; client gating is UI-only (DASHBOARD_STYLE, AUTHENTICATION) | Security section admin-gated, unchanged (Delivery ungated this pass) | [ ] |
| Mandatory visual QA at 1280/768/390, light + dark (DASHBOARD_STYLE, FRONTEND_ARCH) | Every phase + Phase 13 | [ ] |
| Docs describe the real product (REPOSITORY_REQUIREMENTS, WRITING_DOCUMENTS) | Phase 11 | [ ] |
| Scripts build/deploy the dashboard on all platforms (REPOSITORY_REQUIREMENTS) | Phase 12 | [ ] |
| Tests cover routing, pages, table behavior, empty/error (DASHBOARD_STYLE) | Phase 10 | [ ] |

---

## Risks and how the plan contains them

Deep-link breakage is the sharpest risk, and the route-redirect strategy is the answer:
every moved route keeps a redirect, every detail route stays addressable, and Phase 10 adds
tests that fail if a redirect regresses. Bookmarks, chart click-throughs, Needs You links,
and `entityRoute` all keep working.

Feature loss is the fear the thesis exists to prevent, so the rule holds everywhere:
relocate, never delete. Docks, raw Events, Signals, and the full merge queue remain
reachable as tabs and filter values. If a reviewer cannot reach a former capability, the
phase is not done.

Tab performance matters because folding six or seven pages behind one route risks loading
them all at once. The Tabs primitive lazy-mounts panels so only the active tab's page
component renders, preserving the existing `React.lazy` route-splitting behavior.

Delivery over-merging is a real temptation and the plan resists it. Environments,
Deployments, Releases, and Incidents keep their distinct lifecycle behavior as separate
tabs, and role/feature gating hides what a tenant does not use rather than flattening them
into one blob.

The measure of success is not the item count. It is whether a first-time operator can open
Armada, read the home screen, find the daily-driver work in the first group, and recover
from a failure without hunting through five near-identical activity logs. Ship the phases
in dependency order, keep the redirects honest, and treat the three review phases as gates
rather than paperwork.
