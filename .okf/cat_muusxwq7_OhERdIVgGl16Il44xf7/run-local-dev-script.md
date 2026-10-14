---
type: "Reference"
title: "Foreground dev run: run-local.sh and dotnet run"
description: "scripts/<os>/run-local.sh [net10.0] builds server and Harbor, deploys the dashboard, starts the server in background, waits for health, runs Harbor in foreground; plain server: dotnet run --project src/Armada.Server."
timestamp: "2026-10-05T05:45:38.0038650Z"
created: "2026-10-05T05:29:57.5147650Z"
slug: "run-local-dev-script"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["isolated-server-for-dashboard-testing", "dashboard-committed-dist", "pref-protect-owner-environment"]
version: "2"
salience: "0.5"
---
scripts/common/run-local.sh (wrappers scripts/linux/run-local.sh, scripts/macos/run-local.sh, scripts/windows/run-local.bat): usage `run-local.sh [-f|--framework <fw>|<fw>]` (default net10.0, used to build and run). Builds Armada.Server and Armada.Harbor, runs deploy-dashboard.sh (skip with ARMADA_SKIP_DASHBOARD=1), starts `dotnet run --project src/Armada.Server --no-build` in the background, polls ${ARMADA_BASE_URL:-http://localhost:7890}/api/v1/status/health for 30 s, then runs Harbor in the foreground; exiting Harbor or Ctrl+C stops the server. Warning: it uses the real ~/.armada and port 7890, so it is for the owner's own dev session, not for agents' throwaway tests (use ARMADA_DATA_DIR and other ports for those).

Server-only foreground: `dotnet build src/Armada.sln` then `dotnet run --project src/Armada.Server` (prints the ASCII banner, "Admiral running on port N", "MCP server on port N", dashboard URL). First run creates the SQLite DB, applies migrations, seeds defaults.
