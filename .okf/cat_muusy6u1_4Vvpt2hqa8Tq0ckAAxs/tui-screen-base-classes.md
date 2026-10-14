---
type: "Reference"
title: "TUI screen base classes: ScreenBase, Ops*, Kit GridScreen/StackScreen, Entity*"
description: "ScreenBase for all; Operations use OpsScreen/OpsListScreen/OpsDetailScreen; Activity/System use Kit GridScreen/StackScreen; Delivery/Configuration use EntityListScreen/EntityDetailScreen; pick the family of the hub you work in."
timestamp: "2026-10-05T05:38:23.0337100Z"
created: "2026-10-05T05:38:22.5425250Z"
slug: "tui-screen-base-classes"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-widgets", "tui-modal-closed-workaround", "tui-project-structure"]
version: "1"
salience: "0.5"
---
- Screens/ScreenBase.cs: every main-region screen; carries the route and TuiContext, contributes screen-scoped commands (Actions menu, palette, help overlay), status bar hints, optional refresh action, default auto-refresh interval (15 s; 0 off), OnActivated/OnDeactivating hooks. Not thread-safe.
- Screens/Operations (W3; Home, Needs You, Planning, Dispatch, Backlog, Fleet Actions, Missions, Voyages, Merge Queue, Jobs): OpsScreen (background API calls whose results post back to the UI loop and are dropped once the screen is gone, error dialog and toasts, socket subscriptions ending with the screen, JSON/diff/log/Markdown viewers, confirmations, RemoveClosed modal workaround), OpsListScreen (heading, toolbar of screen and bulk actions, filter row, KPI/banner/chart above, ArmadaGrid with server or local paging, local text filters debounced 250 ms, server select filters, admin user-scope filter, row actions), OpsDetailScreen, OpsFormDialog, OpsHandoff (prefilled hand-offs between screens).
- Screens/Kit (W7 Activity and System): GridScreen (ScreenHeader + KpiBar + FilterStrip + ArmadaGrid, saved table prefs, View JSON j, row menu, New/Delete with role gating and bulk delete), StackScreen, FormModal, PathPrompt, ToggleField, MultilineField, MultiSeriesChart, RecordDetailView, UserScopeField, DataExport, ScreenOps.
- Screens/Entities (W5/W6 Delivery and Configuration): EntityListScreen, EntityDetailScreen (record name, status badges, action buttons, panels as tabs), EntityForm, ScopeRules (lib/scoping.ts rules), NavigationPrefill/PrefillSlots.
Build screens (W4) live in Screens/Build. Conventions: English strings go through the localizer; background work posts to the UI loop via IUiDispatcher.
