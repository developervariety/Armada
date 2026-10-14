---
type: "Project"
title: "O-08 (Medium, open): fleet action template variables substituted into shell unescaped"
description: "vessel.name, vessel.defaultBranch, vessel.workingDirectory, vessel.buildCommand, health.summary go into /bin/sh -s or PowerShell command text without shell quoting. Owner: Fleet Actions."
timestamp: "2026-10-05T05:36:32.1187500Z"
created: "2026-10-05T05:36:31.8426220Z"
slug: "open-o08-fleet-action-template-escaping"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["audit-command-events", "security-review-living-document"]
version: "1"
salience: "0.5"
---
Fleet action Command runs (POST /api/v1/fleet-actions/run and /{id}/run, MCP run_fleet_action; TenantAdmin; executed with /bin/sh -s or PowerShell locally, /bin/sh -c or PowerShell on a Harbor; audited via audit.command, F-16) substitute template variables (vessel.name, vessel.defaultBranch, vessel.workingDirectory, vessel.buildCommand, health.summary) into the shell text without escaping, so a crafted vessel name or health summary can inject commands. Planned fix: shell-quote substitutions per target shell (owner: Fleet Actions). Status 2026-10-04: open.
