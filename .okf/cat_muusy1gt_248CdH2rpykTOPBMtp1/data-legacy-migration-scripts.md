---
type: "Reference"
title: "The migrations/ folder: legacy settings and SQL handoff scripts"
description: "Repo-root migrations/ holds historical, optional scripts (settings v0.1->v0.2 conversion with jq, per-version SQL for v0.7->v0.8 and v0.8->v0.9 per provider); the Admiral applies schema migrations itself at startup, so these are not needed for normal upgrades."
timestamp: "2026-10-05T05:47:41.4367550Z"
created: "2026-10-05T05:47:40.9432730Z"
slug: "data-legacy-migration-scripts"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-schema-migration-numbering", "upgrade-migrations-and-backups"]
version: "1"
salience: "0.5"
---
Contents of /migrations: migrate_v0.1.0_to_v0.2.0.(sh|bat) (converts settings.json databasePath -> database object, backs up to settings.json.v0.1.0.bak, needs jq), migrate_v0.2.0_to_v0.3.0, v0.3.0_to_v0.4.0, v0.4.0_to_v0.5.0, v0.6.0_to_v0.7.0 (no-op), v0.7.0_to_v0.8.0 and v0.8.0_to_v0.9.0 with _sqlite.sql/_postgresql.sql/_mysql.sql/_sqlserver.sql variants, plus migrate_add_agent_output, migrate_add_captain_system_instructions, migrate_add_model_context. They are handoff/manual scripts for operators who want to apply DDL themselves; the canonical path is the automatic startup migration list in each driver ([[data-schema-migration-numbering]]). Supported upgrade path for 1.0: any 0.9.x directly; older releases should go to 0.9.x first. Historical version mentions in these files are intentionally left unchanged when the product version is bumped.
