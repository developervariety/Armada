---
type: "Reference"
title: "Restart, rebuild, and rollback the Admiral from the dashboard (A/B slots)"
description: "POST /api/v1/server/restart relaunches via ARMADA_RESTART_WAIT_PID; rebuild publishes into ~/.armada/bin/slots/<date>_<sha> with a current pointer file, auto-backup, optional Harbor-supervised rollback; experimental for 1.0."
timestamp: "2026-10-05T05:31:56.8322480Z"
created: "2026-10-05T05:31:56.3419350Z"
slug: "server-rebuild-and-rollback"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["run-on-startup-scripts", "route-authorization-registry", "backup-and-restore"]
version: "1"
salience: "0.5"
---
docs/SERVER_REBUILD.md (Phase 1 and 2 implemented; Harbor cutover not verified live; API experimental, outside the compatibility promise).
- Restart (same bits): StatusRoutes LaunchReplacementProcess starts a detached child from the current slot exe with ARMADA_RESTART_WAIT_PID=<own pid>, then stops; the child's Program.WaitForPredecessorExitAsync waits up to 30 s for the old PID before binding.
- Rebuild: POST /api/v1/server/rebuild returns a rebuild id; resolves selfVesselId to the Armada source vessel, makes a throwaway detached worktree at the chosen ref (default main HEAD), auto-backs up the DB, dotnet publish into %USERPROFILE%\.armada\bin\slots\<yyyy-MM-dd>_<short-sha>\ while the old server keeps running, rebuilds the dashboard into the shared <dataDir>/dashboard (best effort, non-fatal), flips the plain-text bin\current pointer, and relaunches. Status streams to <dataDir>/rebuild-status.json, polled via GET /api/v1/server/rebuild/status. Failed server publish leaves current untouched and does not restart.
- Rollback: POST /api/v1/server/rollback; slots retained per rebuildSlotRetentionCount (default 3), pruned only after a successful cutover. rebuildSupervisorHarborId delegates the health-gated cutover to an on-box Harbor.
Target deployment is native single-box Windows; Docker redeploy is out of scope. All four routes are AdminOnly (F-01: they used to be unauthenticated RCE).
