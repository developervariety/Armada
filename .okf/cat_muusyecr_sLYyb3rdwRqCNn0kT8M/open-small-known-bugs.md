---
type: "Project"
title: "Known small bugs and gaps not yet fixed (2026-10-05)"
description: "Codex tool catalog fails on non-integer tool_timeout_sec; job cancel can be lost to a heartbeat (needs conditional update); commit-less WorkProduced; Codex text-mode failures classify as crashes."
timestamp: "2026-10-05T05:27:12.1172980Z"
created: "2026-10-05T05:27:11.5930370Z"
slug: "open-small-known-bugs"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
version: "1"
salience: "0.5"
---
As of 2026-10-05: (1) GET /api/v1/captains/{id}/tools for a Codex captain fails "The JSON value could not be converted to System.Nullable`1[System.Int32]. Path: $.tool_timeout_sec" when the user's Codex config has a non-integer tool_timeout_sec (make it a double or tolerant). (2) Job heartbeats and JobService.CancelAsync rewrite the whole job row, so a cancel landing between a heartbeat's read and write can be overwritten back to Running; needs a conditional update ("only if status is still X") on all four providers. (3) A captain that edits files without committing still reaches WorkProduced with an empty branch. (4) Codex in text mode has no structured error output, so its usage-limit/auth failures classify as crashes until Codex runs with --json. (5) TUI: Alt+Left moves by word inside text fields on editor pages (use Esc to go back); restarting a failed mission leaves it Pending until the next dispatch cycle; merging into the branch checked out in the vessel's working directory fails. (6) Dashboard client.ts still has dead functions behind the five route fixes made in Armada.Client.
