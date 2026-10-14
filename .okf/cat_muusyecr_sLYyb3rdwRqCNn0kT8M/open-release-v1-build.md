---
type: "Project"
title: "Release 1.0.0: build-all v1.0.0 pending a green CI run"
description: "Version is 1.0.0 in code; owner approved running build-all v1.0.0 (Docker Hub push) only after CI is green on the pushed commits."
timestamp: "2026-10-05T05:26:24.6850340Z"
created: "2026-10-05T05:26:24.4324750Z"
slug: "open-release-v1-build"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["decision-version-1-0-0", "pref-confirm-outward-actions", "lesson-push-cancels-ci-run"]
version: "1"
salience: "0.5"
---
Status 2026-10-05: main was pushed at 433469f1 with CI fixes; CI run 37266447400 and install-verify 37266447425 were in progress. Commits after that (version 1.0.0 bump ce7357d8, TUI login logo, hub tab focus and page size controls 5721b79b, macOS timing test fixes 57c0f265, TUI plan U1-U10 note 7f67490e, Docker install safe.directory fix ae1338d6) were committed locally and need pushing, then one more CI run must be green before running `./build-all.sh v1.0.0` (pushes jchristn77/armada-server and the proxy image as v1.0.0 and latest). Previous run results: Ubuntu, compose, dashboard green; macOS and Windows failures were fixed in those commits.
