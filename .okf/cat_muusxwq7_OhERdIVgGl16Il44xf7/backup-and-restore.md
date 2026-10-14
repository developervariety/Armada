---
type: "Reference"
title: "Backup and restore (SQLite): GET /api/v1/backup, POST /api/v1/restore"
description: "Backup ZIP = online-backup armada.db + settings.json + manifest.json, saved to backups/armada-backup-<ts>.zip; restore writes pre-restore-<ts>.zip first; restart afterward; AdminOnly; treat ZIPs as secrets."
timestamp: "2026-10-05T05:31:26.7886310Z"
created: "2026-10-05T05:31:26.3501030Z"
slug: "backup-and-restore"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["upgrade-migrations-and-backups", "open-o12-backup-host-paths", "proxy-route-policy"]
version: "1"
salience: "0.5"
---
SQLite only. GET /api/v1/backup (dashboard Server page Backup button, MCP tool backup, WebSocket backup command) writes {DataDirectory}/backups/armada-backup-<timestamp>.zip and streams it: a consistent snapshot of armada.db taken with the SQLite online backup API (includes WAL writes; safe while running), the loaded settings.json, and manifest.json (schema version, Armada version, record counts). Example: curl -H "Authorization: Bearer <token>" http://127.0.0.1:7890/api/v1/backup -o armada-backup.zip.

POST /api/v1/restore with the ZIP as an application/zip body: rejects anything without armada.db containing schema_migrations; writes a safety backup to backups/pre-restore-<timestamp>.zip; copies the backup into the live DB via the online backup API and replaces settings.json if present. Restart the Admiral afterward (in-memory state is cached until then). Covered by E2E.BackupRestoreDrill.

Both are AdminOnly; the proxy relay blocks restore but allows backup download. The ZIP includes settings.json, which may contain gitHubToken and DB credentials: store backups as secrets. Server providers get 400 from the endpoint; use pg_dump/mysqldump/BACKUP DATABASE. MCP and WebSocket backup/restore accept arbitrary host paths (security O-12, accepted). Docker: stopping the stack and copying docker/armada/db and armada.json is a full SQLite backup.
