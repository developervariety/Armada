---
type: "Reference"
title: "audit.command events for every shell command run on a user's behalf"
description: "Workspace exec, fleet action commands, check runs (incl. deploy/verify), Harbor probes, and merge queue tests write an audit.command event before starting; only global admins can delete audit.* events."
timestamp: "2026-10-05T05:35:00.1796320Z"
created: "2026-10-05T05:34:59.9348060Z"
slug: "audit-command-events"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["open-o16-accounting-gaps", "captain-auto-approve-flags", "secrets-redaction-and-request-history"]
version: "1"
salience: "0.5"
---
F-16. Before Armada runs a shell command or process for a user it writes an audit.command event with source WorkspaceExec, FleetAction, CheckRun, HarborProbe, or MergeQueueTest. Payload: source, command (secret-shaped values redacted, max 4,000 characters), working directory, host (Admiral or Harbor id), tenant, user, vessel, owning record. Only a global admin can delete audit.* events (REST and MCP delete_event/delete_events, which are also tenant and owner scoped, F-19). Review them with MCP enumerate entityType events or the dashboard Events page. Not covered: commands a captain runs inside its own CLI session (visible in the mission log), authentication successes/failures, authorization denials, and actor ids on other operational events (O-16). New code that launches a command for a user must emit the audit event first.
