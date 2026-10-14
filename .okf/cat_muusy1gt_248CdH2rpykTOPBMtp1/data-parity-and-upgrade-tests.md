---
type: "Reference"
title: "Schema verification: provider parity + upgrade-from-baseline test"
description: "Two guards for schema changes: the Database suite incl. MigrationHygiene on all four providers (procedure in [[db-parity-script]]) and scripts/common/run-upgrade-test.sh, which seeds a real v0.9.0 Admiral and checks the current build migrates it (Upgrade.FromBaseline)."
timestamp: "2026-10-05T05:47:52.9814330Z"
created: "2026-10-05T05:47:19.4262680Z"
slug: "data-parity-and-upgrade-tests"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["db-parity-script", "data-migration-rules", "data-seeding-and-builtin-upgrades"]
version: "2"
salience: "0.5"
---
What a schema change must pass: (1) the Touchstone "Database" suite on SQLite, PostgreSQL, MySQL, SQL Server (run-db-parity-tests.sh; Docker images postgres:17-alpine, mysql:8.4, mssql 2022), which includes Database.MigrationHygiene (re-apply every migration on a populated DB: no schema or data change; no new destructive statements). Command details live in the build-and-test memory [[db-parity-script]].
(2) scripts/common/run-upgrade-test.sh [--from-ref <ref>] [--providers sqlite|postgresql|mysql|sqlserver|all] [--keep] (Windows wrapper runs it via Git Bash): git archive the baseline (default tag v0.9.0, else release commit e456b008 = schema v44 on SQLite; also validated from 574a8a1a, the published v0.9.0 images = schema v69), build scripts/common/upgrade-baseline-host, start the old Admiral against a throwaway DB with private HOME/USERPROFILE/ARMADA_DATA_DIR, seed over REST (fleets, vessels, a captain, a mission in every status, voyages, merge entries, edited personas/pipelines, prompt overrides, signals, a backlog objective, a second tenant with its own credential), stop, then the current build's Upgrade.FromBaseline suite (Test.Shared UpgradeFromBaselineSuite) checks the pre-migration backup, migration, every record, the tenant credential, that edits survived, and that built-in upgrades were applied. Run before every release candidate; ARMADA_UPGRADE_DB_PORT_BASE pins container ports.
