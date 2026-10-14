---
type: "Project"
title: "Pushing to main cancels the in-progress CI run"
description: "ci.yml uses concurrency; a new push cancels the running ci workflow, so commit locally and push after the run you are waiting on finishes."
timestamp: "2026-10-05T05:25:44.4028620Z"
created: "2026-10-05T05:25:44.1297410Z"
slug: "lesson-push-cancels-ci-run"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["pref-commit-and-push-to-main"]
version: "1"
salience: "0.5"
---
Many ci runs on 2026-10-04/05 show "cancelled" because a newer push superseded them. When a decision depends on a CI result (for example running build-all only after green CI), commit further work locally and push only after that run completes. Check status with `gh run list -R jchristn/Armada` and `gh run view <id>`; job logs via `gh api --allow-escape-sequences repos/jchristn/Armada/actions/jobs/<jobId>/logs` (gh run view --log fails while the run is in progress). Workflows: ci (dashboard, compose, test matrix), install-verify, security.
