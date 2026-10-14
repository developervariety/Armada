---
type: "Reference"
title: "Dashboard: npm ci, build, Vitest, and commit dist/"
description: "In src/Armada.Dashboard: npm ci && npm run build && npm run test:run; node_modules is per machine; rebuild and commit dist/ whenever dashboard source changes (CI checks drift)."
timestamp: "2026-10-05T05:27:51.8407390Z"
created: "2026-10-05T05:27:51.4945430Z"
slug: "dashboard-build-and-test-commands"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["dashboard-committed-dist", "dashboard-tests-vitest", "ci-workflow-ci-yml"]
version: "1"
salience: "0.5"
---
cd src/Armada.Dashboard
  npm ci            # node_modules is not committed; install per machine (a foreign/stale node_modules breaks the toolchain)
  npm run build     # tsc && vite build -> dist/
  npm run test:run  # vitest run (jsdom, threads pool, setup src/test/setup.ts)
`npm test` runs Vitest in watch mode; `npm run dev` starts Vite on port 3000.

dist/ is committed on purpose (deploy scripts fall back to it when Node is absent). CI job `dashboard` (ubuntu, Node 24) runs npm ci, build, test:run, then fails if `git status --porcelain -- dist` shows any change ("dist/ is out of date. Run npm ci && npm run build ... and commit dist/"). Build on Linux or with matching line endings so the committed files match. Vite emits hashed asset names, so a rebuild deletes old dist/assets/*.js and adds new ones; commit both the deletions and additions. security.yml also runs `npm audit --audit-level=high` (Node 22).
