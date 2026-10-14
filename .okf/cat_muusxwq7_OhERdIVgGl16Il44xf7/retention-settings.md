---
type: "Reference"
title: "Data retention settings and what grows unbounded"
description: "dataRetentionDays 30 (SQLite only), requestHistoryRetentionDays 30, retention.askThreadArchiveAfterDays 90, askThreadDeleteAfterDays 0, jobRetentionDays 30, importBatchRetentionDays 90, fleetActions.runRetentionDays 30; 0 keeps forever."
timestamp: "2026-10-05T05:31:34.1335390Z"
created: "2026-10-05T05:31:33.8055430Z"
slug: "retention-settings"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["important-settings-keys", "logs-layout"]
version: "1"
salience: "0.5"
---
Retention runs in the background on the health-check loop's slow cadence (every 100 cycles, about hourly with the default heartbeat), works on every provider, and applies setting changes immediately. 0 disables a rule.
- retention section (settings.json, Server page, PUT /api/v1/settings; RetentionSettings): askThreadArchiveAfterDays 90 (0-3650; pinned threads never archived), askThreadDeleteAfterDays 0 (never; deletes messages, tool calls, proposals, tracked work), jobRetentionDays 30 (finished jobs; keeps the newest finished job per kind and name per tenant because schedules measure from it), importBatchRetentionDays 90 (finished vessel import batches; imported vessels untouched).
- dataRetentionDays 30: completed voyages and missions, old signals and events (currently SQLite only).
- requestHistoryRetentionDays 30, requestHistoryMaxBodyBytes 32 KB, requestHistoryEnabled true (false turns capture off), requestHistoryExcludeRoutes.
- planningSessionRetentionDays 0 (kept), planningSessionInactivityTimeoutMinutes.
- fleetActions.runRetentionDays 30 (1-3650); rebuildSlotRetentionCount 3; database.migrationBackupRetentionCount 5; captain logs rotate by maxLogFileSizeBytes 10 MB and maxLogFileCount 5.
Vessel health findings replace previous ones per evaluation (no history). On busy installs check the DB size and ~/.armada/logs monthly.
