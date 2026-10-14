---
type: "Reference"
title: "Data directory ~/.armada and ARMADA_DATA_DIR"
description: "Everything (settings.json, armada.db, logs, docks, repos, dashboard, bin, backups) lives in ~/.armada unless ARMADA_DATA_DIR relocates it; read once at startup by Constants.DefaultDataDirectory."
timestamp: "2026-10-05T05:29:19.1076070Z"
created: "2026-10-05T05:29:18.6619630Z"
slug: "data-directory-and-env"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["settings-json-loading", "logs-layout", "service-registration-flags", "test-temp-profile-sandbox"]
version: "1"
salience: "0.5"
---
Armada.Core.Constants.DefaultDataDirectory = ARMADA_DATA_DIR when set (Constants.DataDirectoryEnvVar), otherwise <UserProfile>/.armada. It is a static readonly resolved once per process, so set the variable before the process starts. Layout under the data dir: settings.json (Admiral config), armada.db (+ -wal, -shm; SQLite default), logs/ (admiral.log, captains/, missions/, ...), docks/ (git worktrees for captains), repos/ (vessel clones), dashboard/ (deployed React build, served automatically), bin/ (published server from publish-server scripts; A/B slots for rebuild), backups/ (backup zips, pre-migration and pre-restore copies), rebuild-status.json, crash.log (unhandled exceptions), tui.json and tui-credentials.json (TUI). Directory settings dataDirectory, logDirectory, docksDirectory, reposDirectory, databasePath can override pieces; relative SQLite paths resolve against the data dir.

Service installs: a Linux system unit without --service-user runs as root and uses /root/.armada; the Windows service (LocalSystem) uses %ProgramData%\Armada when ARMADA_DATA_DIR is unset. Docker sets ARMADA_DATA_DIR=/app/data. Harbor has its own ~/.armada-harbor/settings.json.
