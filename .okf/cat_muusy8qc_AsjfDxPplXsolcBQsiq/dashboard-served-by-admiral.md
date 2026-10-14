---
type: "Reference"
title: "How the Admiral serves the dashboard at /dashboard"
description: "ArmadaServer.InitializeDashboard picks dashboardPath (relative to the data dir), else <dataDir>/dashboard with index.html, else dashboard/ next to the executable, else the embedded legacy wwwroot dashboard."
timestamp: "2026-10-05T05:41:53.7732990Z"
created: "2026-10-05T05:41:52.9909510Z"
slug: "dashboard-served-by-admiral"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["dashboard-committed-dist", "isolated-server-for-dashboard-testing", "troubleshooting-common"]
version: "1"
salience: "0.5"
---
src/Armada.Server/ArmadaServer.cs InitializeDashboard: 1) settings.dashboardPath if set (relative paths resolve against DataDirectory) and the directory exists, else warn "configured DashboardPath not found" and continue; 2) <DataDirectory>/dashboard containing index.html (where deploy-dashboard.sh copies the build); 3) a dashboard/ folder next to the server executable with index.html (packaged single-file servers; AppContext.BaseDirectory when Assembly.Location is empty); 4) fallback to the embedded legacy dashboard in src/Armada.Server/wwwroot (not the React dashboard; this was a W5.5 bug for packaged servers). Dashboard.StaticFileHandler.SetExternalPath serves the chosen folder with path containment (PathContainment). The React build uses base /dashboard/, so it is reachable at http://localhost:7890/dashboard; /dashboard/i18n/armada.json serves the catalog. For testing a worktree's build, point dashboardPath at <worktree>/src/Armada.Dashboard/dist on an isolated server. CORS allows Content-Type, X-Api-Key, X-Token, Authorization.
