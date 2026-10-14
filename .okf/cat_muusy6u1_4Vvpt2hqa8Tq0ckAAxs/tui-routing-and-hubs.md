---
type: "Reference"
title: "TUI routing: Router, RouteTable (every dashboard route), hubs with tabs in the query"
description: "RouteTable mirrors App.tsx routes and redirects plus TUI-only /approvals and /setup; Router keeps back/forward (100 entries); hubs keep the active tab in ?tab= so deep links and history work."
timestamp: "2026-10-05T05:38:13.1748800Z"
created: "2026-10-05T05:38:12.6489750Z"
slug: "tui-routing-and-hubs"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-parity-manifest", "tui-key-map", "dashboard-routes-and-hubs"]
version: "1"
salience: "0.5"
---
Routing/RouteTable.cs lists every route of src/Armada.Dashboard/src/App.tsx including redirects (for example /admin/users redirects to /server?tab=users), plus TUI-only extensions /approvals and /setup; unknown paths go to NotFound. Routing/Router.cs resolves paths (following up to 5 redirect hops), keeps back and forward stacks (MaxHistory default 100, clamp 1..1000), Navigate pushes and clears forward, SwitchTab replaces the entry so tab flips do not flood history, ReplaceQuietly mirrors screen state (filters, sort, page) into the query without rebuilding, Clear on sign-out. Use on the UI loop thread only. Deep links keep queries, e.g. /vessels/health?overall=Fail, /delivery?tab=deployments&status=PendingApproval. Screens/ScreenFactory.cs maps screen names to builders (unbuilt routes would get PlaceholderScreen; every route is implemented as of 2026-10-05). Screens/HubScreen.cs renders hubs (Missions, Delivery, Vessels, Captains, Configuration, Activity, Settings/Server, Dispatch, Fleet Actions): a TabStrip over the active tab screen, [ / ] and Alt+1..9 switch tabs, role-gated tabs hidden, TuiContext.KeepHubTabFocus keeps focus on the strip after Left/Right switches. Routing/NavCatalog.cs mirrors navConfig.tsx exactly. Navigation: Alt+Left/Alt+Right (Backspace outside text fields) back/forward; g-prefixed go-to keys; Ctrl+K palette opens ids directly (msn_, vsl_, vyg_, cpt_...).
