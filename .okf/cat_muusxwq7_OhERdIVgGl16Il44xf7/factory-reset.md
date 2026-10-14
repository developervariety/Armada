---
type: "Reference"
title: "Factory reset scripts (wipe ~/.armada state)"
description: "scripts/<os>/factory-reset.sh [-y] [--all] stops the Admiral and deletes the DB and runtime state under ~/.armada (keeps bin and dashboard unless --all); regenerates the API key; destructive, confirm first."
timestamp: "2026-10-05T05:31:48.2529500Z"
created: "2026-10-05T05:31:47.8916260Z"
slug: "factory-reset"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["data-directory-and-env", "pref-protect-owner-environment", "pref-confirm-outward-actions"]
version: "1"
salience: "0.5"
---
scripts/macos/factory-reset.sh, scripts/linux/factory-reset.sh, scripts/windows/factory-reset.bat: stop the Admiral (macOS launchd agent com.armada.admiral plus any stray Armada.Server process), then delete the database and runtime state under ~/.armada (armada.db and -wal/-shm, logs/, docks/, repos/, and similar) so the next start comes up empty and re-seeds the default tenant, admin@armada/password, the default bearer token, built-in templates, personas, pipelines. Flags: -y/--yes/--force skip the prompt; --all also deletes bin/ and dashboard/ (whole ~/.armada). The API key is regenerated on next start, so MCP clients and the CLI that stored the old key must be reconfigured. factory/ at the repo root holds a gold settings.json (new ArmadaSettings() with machine paths stripped) and reset.sh/reset.bat; docker/armada/factory/reset.* resets the Docker SQLite db and logs but keeps armada.json and never touches external databases. Also exposed as POST /api/v1/status/factory-reset (blocked through the proxy). This is destructive and irreversible: only run it when the owner asks, never against the owner's real ~/.armada on your own initiative.
