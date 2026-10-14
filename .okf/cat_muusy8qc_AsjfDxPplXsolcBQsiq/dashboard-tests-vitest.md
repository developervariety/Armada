---
type: "Reference"
title: "Dashboard tests: Vitest + Testing Library smoke and interaction tests"
description: "About 50 *.test.ts(x) files beside the code (pages, shared components, lib hooks, socket, i18n); run npm run test:run; tests read backend enums via ?raw to assert full mapping."
timestamp: "2026-10-05T05:41:34.4560390Z"
created: "2026-10-05T05:41:34.1595000Z"
slug: "dashboard-tests-vitest"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["dashboard-build-and-test-commands", "test-assertions-structured-rule-7"]
version: "1"
salience: "0.5"
---
Vitest 4 with jsdom, globals, threads pool, setup src/test/setup.ts (imports @testing-library/jest-dom/vitest). Tests live next to the code as *.test.ts / *.test.tsx: pages (ApiExplorer, AskArmada, CheckRuns, History, FleetActionRunDetail, ...), shared components (ActionMenu, BackgroundActivityIndicator, BoolIcon, CaptainPicker, LanguageSelector, PlaybookSelector, Tabs), lib (armadaSocket, askConversation, askQuickActions, captainApproval, captains, dialogA11y, fleetActionLabels, fleetOpsCatalog, useResourceTable, useTablePrefs), context (WebSocketContext), i18n (plural), api (vesselHealthClient), navConfig. Some tests import backend sources with ?raw (vite fs.allow includes ../Armada.Core and the i18n folder) to assert the dashboard maps every enum value or detail code. Commands: npm run test:run (CI), npm test (watch). Fragility R5: assert on parsed messages and matched calls, not on rendered text fragments where a structured form exists.
