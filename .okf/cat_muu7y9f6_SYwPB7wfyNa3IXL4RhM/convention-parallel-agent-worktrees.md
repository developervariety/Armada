---
type: "Project"
title: "How parallel agent work is organized (worktrees, rules, merge)"
description: "Each agent gets a scratchpad worktree (never ~/Code/Armada-<name>) on work/<name> from main, follows a shared rules brief, never pushes; the coordinator merges one branch at a time with a full test run."
timestamp: "2026-10-14T20:56:44.8712560Z"
created: "2026-10-05T05:27:27.9969680Z"
slug: "convention-parallel-agent-worktrees"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["pref-worktrees-outside-code", "lesson-parallel-agent-merge-pitfalls", "pref-commit-and-push-to-main", "pref-control-scope-and-converge"]
version: "2"
salience: "0.5"
---
Process for large work (first used 2026-10-04/05, worktree location corrected 2026-10-08): per workstream, create a worktree in the coordinator's session scratchpad, for example `git worktree add -b work/<name> <scratchpad>/worktrees/<name> main`. Never put worktrees or other checkouts under ~/Code ([[pref-worktrees-outside-code]]); the old `../Armada-<name>` layout is retired. Give each agent explicit file-scope boundaries (production vs tests, which projects) to avoid overlapping edits. A shared rules brief requires: never touch ~/.armada or ports 7890/7891/21000-21099, stop processes by PID only, CLAUDE.md C# style, ASCII only, no version changes, additive API surface changes with regeneration, CHANGELOG bullets under Unreleased, plan checkbox and progress-log row updates, regression tests that fail on old code, full suite before finishing, report fixed/deferred items. Agents commit on their branch but never push or merge. The coordinator merges with --no-ff --no-commit, resolves conflicts (additive docs keep both sides; CRLF churn with an LF-normalized 3-way merge; parity.json by max status), builds, runs the full suite, reads the totals, then commits and pushes, and removes the worktree and branch right away. Ask long-running agents to cherry-pick urgent main fixes rather than waiting for merge.
