---
type: "Feedback"
title: "Never create worktrees or sibling checkouts under ~/Code; use the session scratchpad"
description: "Only ~/Code/Armada may exist for this repo under ~/Code; worktrees and build checkouts go in the scratchpad and are removed once merged."
timestamp: "2026-10-14T20:56:40.9545830Z"
created: "2026-10-14T20:56:37.7247910Z"
slug: "pref-worktrees-outside-code"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["convention-parallel-agent-worktrees", "pref-protect-owner-environment"]
version: "1"
salience: "0.5"
---
Never create git worktrees, image-build checkouts, device-build checkouts, or any other sibling directory under ~/Code (for example ~/Code/Armada-<name>). Put them in the agent session's scratchpad, for example <scratchpad>/worktrees/<name>, and remove each one (worktree and branch) as soon as it is merged or no longer needed.

Why: on 2026-10-08 the owner found ~/Code/Armada-device, ~/Code/Armada-harbor-charts and ~/Code/Armada-images next to ~/Code/Armada and said "THERE SHOULD ONLY BE ~/Code/Armada."

How to apply: when briefing subagents, give them a scratchpad worktree path, never ../Armada-<name>. This replaces the ../Armada-<name> step in the earlier parallel-work process ([[convention-parallel-agent-worktrees]]).
