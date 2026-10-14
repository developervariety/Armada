---
type: "Project"
title: "run-local.sh rebuilds dashboard dist/ and dirties the committed files"
description: "deploy-dashboard during run-local.sh regenerates hashed dist assets in the main checkout; rebuild cleanly and commit dist intentionally, never mix it into unrelated commits."
timestamp: "2026-10-05T05:26:09.9351400Z"
created: "2026-10-05T05:26:09.7398650Z"
slug: "lesson-dashboard-dist-local-rebuild"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
version: "1"
salience: "0.5"
---
src/Armada.Dashboard/dist/ is committed on purpose (deploy scripts fall back to it without Node). When the owner runs scripts/macos/run-local.sh, deploy-dashboard rebuilds it, deleting and adding hashed asset files in the working tree. Before committing in the main checkout, check `git status` for dist changes: either restore them (git checkout -- src/Armada.Dashboard/dist) or rebuild deliberately (npm ci && npm run build) and commit dist in its own commit when dashboard source changed. Merge conflicts in dist are resolved by rebuilding, never by hand. node_modules is never committed.
