---
type: "Reference"
title: "Committed dist/ and deploy-dashboard scripts"
description: "src/Armada.Dashboard/dist is committed so installs without Node work; deploy-dashboard.sh builds (npm install/build, retry with npm ci) or falls back to dist/, then copies to ~/.armada/dashboard; rebuild and commit dist/ with every source change."
timestamp: "2026-10-05T05:41:41.5287370Z"
created: "2026-10-05T05:41:40.6200020Z"
slug: "dashboard-committed-dist"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["dashboard-build-and-test-commands", "dashboard-served-by-admiral", "source-install-scripts"]
version: "1"
salience: "0.5"
---
CLAUDE.md: dist/ is committed on purpose because deploy scripts fall back to it when Node is not installed; rebuild and commit it whenever dashboard source changes; CI's dashboard job fails on drift. scripts/common/deploy-dashboard.sh (wrappers per OS; Windows deploy-dashboard.bat): honors --insecure; if node exists, npm install when node_modules is missing, npm run build, and on failure (stale or foreign node_modules) rm -rf node_modules, npm ci, rebuild; requires dist/index.html; without Node it deploys the prebuilt dist/ (errors if missing); then replaces ~/.armada/dashboard with dist/ contents (note: hardcoded ${HOME}/.armada/dashboard, not ARMADA_DATA_DIR). Called by install, update, publish-server, and run-local (skip with ARMADA_SKIP_DASHBOARD=1). Commit the rebuilt dist in the same commit as the source change, including deletions of old hashed assets.
