---
type: "Reference"
title: "Upgrade test: run-upgrade-test.sh and Upgrade.FromBaseline"
description: "scripts/common/run-upgrade-test.sh seeds an old Admiral (default v0.9.0 / commit e456b008) and verifies the current build migrates it; --providers all adds Docker DBs; run before every RC."
timestamp: "2026-10-05T05:28:37.7391190Z"
created: "2026-10-05T05:28:37.1647690Z"
slug: "upgrade-test-script"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["upgrade-migrations-and-backups", "db-parity-script", "release-checklist"]
version: "1"
salience: "0.5"
---
Usage: `scripts/common/run-upgrade-test.sh [--from-ref <git ref>] [--providers sqlite,postgresql,mysql,sqlserver|all] [--framework net10.0] [--keep]` (Windows: scripts/windows/run-upgrade-test.bat runs it through Git Bash). Steps: git archive the baseline src into a temp dir, add scripts/common/upgrade-baseline-host (a small host project) and build it; build Test.Automated from this checkout; for each provider start the old Admiral against a throwaway DB with private HOME/USERPROFILE/ARMADA_DATA_DIR (never ~/.armada), seed through REST (fleets, vessels, captain, a mission in every status, voyages, merge queue entries, edited personas/pipelines, prompt template overrides, signals, a backlog objective, a second tenant with a credential), stop it, then run the Upgrade.FromBaseline suite against the current build (checks the pre-migration backup, migration, every seeded record, credentials, edits survived, built-in upgrades applied).

Default baseline: the v0.9.0 tag when present, else the release(v0.9.0) commit e456b0080e5a834d5469cd615d73b3d542b00431 (schema v44 on SQLite); 574a8a1a (published v0.9.0 images, schema v69) is also tested. Default providers: sqlite. ARMADA_UPGRADE_DB_PORT_BASE pins container host ports. Requires git, dotnet, and docker for server providers.
