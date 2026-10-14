---
type: "Project"
title: "O-12 (Low, accepted): MCP and WebSocket backup/restore take arbitrary host paths"
description: "backup/restore via MCP and WebSocket (AdminOnly) read and write any host path; accepted because global admins are host operators."
timestamp: "2026-10-05T05:36:55.1876550Z"
created: "2026-10-05T05:36:50.9447500Z"
slug: "open-o12-backup-host-paths"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["backup-and-restore", "mcp-tool-authorization-registry"]
version: "1"
salience: "0.5"
---
The MCP tools backup and restore and the WebSocket backup/restore commands accept arbitrary host paths (backup overwrites outputPath; restore replaces the database and settings from any zip on the host). Before F-03 any MCP caller could use them; now they are AdminOnly and need a credential even on loopback. Remaining exposure accepted: global admins are trusted host operators. Backups include settings.json secrets. REST GET /api/v1/backup and POST /api/v1/restore use the data dir's backups folder instead. Status 2026-10-04: accepted, documented.
