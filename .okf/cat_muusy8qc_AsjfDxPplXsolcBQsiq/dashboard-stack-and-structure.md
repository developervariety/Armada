---
type: "Reference"
title: "Dashboard stack and source layout (src/Armada.Dashboard)"
description: "React 19 + react-router-dom 7 + Vite 8 + TypeScript 7, base path /dashboard/; src/{api,components,context,i18n,lib,pages,types,test}; pages lazy-loaded in App.tsx."
timestamp: "2026-10-05T05:40:50.8865870Z"
created: "2026-10-05T05:40:50.3246460Z"
slug: "dashboard-stack-and-structure"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["dashboard-nav-groups", "dashboard-api-client", "dashboard-tests-vitest", "dashboard-table-cell-sizing"]
version: "1"
salience: "0.5"
---
package.json (version 1.0.0, type module): dependencies react/react-dom 19, react-router-dom 7, react-markdown 10 + remark-gfm; dev: vite 8, @vitejs/plugin-react 6, typescript 7, vitest 4, jsdom, @testing-library/react, jest-dom, user-event. vite.config.ts: base '/dashboard/', __APP_VERSION__ from package.json, Vitest config (jsdom, globals, threads pool, setupFiles src/test/setup.ts), dev server port 3000 with fs.allow for ../Armada.Core and ../Armada.Server/wwwroot/i18n (tests read backend enums and i18n with ?raw), dev proxy /api to VITE_ARMADA_SERVER_URL or http://localhost:5000 (stale default; set VITE_ARMADA_SERVER_URL=http://localhost:7890 or your test port).
src/: App.tsx (85 Route elements, 73 lazy() page imports), main.tsx, App.css (CRLF line endings; table sizing utilities), index.css, responsive.css, a11y.css; api/client.ts (all server calls); components/ (Layout.tsx, navConfig.tsx, navIcons.tsx, LoginFlow, SetupWizard, DefaultCredentialsBanner, PasswordChangeRequired, ProtectedRoute, MissionHistoryChart, shared/ widgets, ask/, backlog/, captains/, fleetActions/, planning/, settings/, vessels/, workspace/); context/ (AuthContext, WebSocketContext, LocaleContext, NotificationContext, ThemeContext); i18n/runtime.ts; lib/ (armadaSocket, askConversation, scoping, useResourceTable, useTablePrefs, useAutoRefresh, useInboxCount, routing, format, health/...); pages/ (one per route plus hubs and admin/); types/models.ts.
