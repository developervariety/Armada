---
type: "Feedback"
title: "Tests and agents must never touch the owner's ~/.armada or ports 7890/7891"
description: "The owner runs a live Admiral from ~/.armada on 7890/7891 (run-local.sh); tests, agents, and throwaway servers use temp data dirs and other ports."
timestamp: "2026-10-05T05:23:44.6606630Z"
created: "2026-10-05T05:23:44.4138820Z"
slug: "pref-protect-owner-environment"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["lesson-test-overwrote-user-settings", "lesson-mcp-host-binding"]
version: "1"
salience: "0.5"
---
The owner develops against a live local Admiral (scripts/macos/run-local.sh: builds server and Harbor, deploys the dashboard, starts the server on 7890/7891 with data in ~/.armada, then Harbor). Never let tests or agents write to ~/.armada, read or probe his real Claude Code/Codex configs, or bind 7890/7891. Throwaway servers: set ARMADA_DATA_DIR to a temp dir and use high ports (e.g. 33000-38000); stop them by PID, never pkill patterns. Never kill or restart his server without asking; when a settings change requires a restart, tell him. After a test overwrote his settings.json (2026-10-04), TestTemp now sandboxes the profile for every test run.
