---
type: "Reference"
title: "Dashboard routes and hub pages (App.tsx)"
description: "App.tsx declares about 85 routes (pages lazy-loaded) including redirects such as /admin/users to /server?tab=users; hubs (CaptainsHub, DeliveryHub, DispatchHub, MissionsHub, ServerHub, VesselsHub, Configuration, Activity, FleetActions) keep the tab in ?tab=."
timestamp: "2026-10-05T05:41:05.8164090Z"
created: "2026-10-05T05:41:05.4676490Z"
slug: "dashboard-routes-and-hubs"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["dashboard-nav-groups", "tui-parity-manifest", "tui-parity-generator-and-suite"]
version: "1"
salience: "0.5"
---
src/Armada.Dashboard/src/App.tsx: React Router routes under ProtectedRoute and Layout, each page imported with React.lazy (Vite splits one chunk per page, hence the hashed dist/assets/<Page>-<hash>.js files). Hub pages render Tabs (components/shared/Tabs.tsx) and keep the active tab in the query (?tab=) so deep links work: CaptainsHub (/captains), DeliveryHub (/delivery), DispatchHub (/dispatch), MissionsHub (/missions), ServerHub (/server), VesselsHub (/vessels), Configuration (/configuration), Activity (/activity, by source), FleetActions (/fleet-actions). Older paths redirect to hub tabs (for example /admin/credentials to /server?tab=credentials). Detail routes use :id (/missions/:id, /vessels/:id, /deployments/:id, /requests/:id ...). The generator scripts/tui/generate-parity-manifest.py parses App.tsx and the hub pages (HUB_PAGES map) to keep the TUI in parity, so adding a route or tab requires regenerating parity.json and implementing it in the TUI.
