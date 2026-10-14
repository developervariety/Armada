---
type: "Reference"
title: "Log locations: admiral.log, captains/, missions/<id>.log, crash.log, syslog"
description: "Logs live in logDirectory (default ~/.armada/logs; /app/data/logs in Docker): admiral.log, captains/, missions/<missionId>.log, final-messages/, diffs/, instructions/, docks/; crash.log in the data dir."
timestamp: "2026-10-05T05:31:40.9439110Z"
created: "2026-10-05T05:31:40.6115060Z"
slug: "logs-layout"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["data-directory-and-env", "troubleshooting-common", "retention-settings"]
version: "1"
salience: "0.5"
---
Under logDirectory (default <dataDir>/logs):
- admiral.log: the Admiral's own log, rotated by size; the reason for a failed start is near its end.
- captains/: per-captain process output, rotated per maxLogFileSizeBytes (10 MB) and maxLogFileCount (5).
- missions/<missionId>.log: one mission's output (what get_mission_log reads).
- final-messages/, diffs/ (captured at landing), instructions/ (instruction file snapshots per launch), docks/ (dock start metadata), playbooks/, objective-refinement-sessions/.
Also: <dataDir>/crash.log gets unhandled and unobserved task exceptions (Program.cs). Logging goes through SyslogLogging to syslogServers (default 127.0.0.1:514 UDP) and, with telemetry on, Loki. Docker: docker compose logs armada-server for console output; docker/armada/logs on the host. Armada.Proxy logs to its own logDirectory (docker/proxy/logs). Request logs omit query strings (F-15). When filing an issue collect admiral.log, relevant missions/ and captains/ files, GET /api/v1/status output, and settings.json with secrets removed.
