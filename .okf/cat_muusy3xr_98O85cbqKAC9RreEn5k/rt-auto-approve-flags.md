---
type: "Reference"
title: "Auto-approve: each runtime's \"do not ask\" flag and how to turn it off"
description: "By default captains run unattended: Claude --dangerously-skip-permissions, Codex --sandbox workspace-write (Windows --dangerously-bypass-approvals-and-sandbox), Gemini --approval-mode yolo, Cursor --force, Mux --yolo, OpenCode --auto; autoApprove=false or Vessel.AutoApprove switches to safer modes."
timestamp: "2026-10-05T05:52:14.8822180Z"
created: "2026-10-05T05:37:49.9659240Z"
slug: "rt-auto-approve-flags"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-claude-code-runtime", "rt-ask-thread-mcp-token", "captain-auto-approve-flags", "ask-captain-auto-approve-off"]
version: "2"
salience: "0.5"
---
Runtime view (security view: [[captain-auto-approve-flags]], [[ask-captain-auto-approve-off]]). Captains run in a git worktree with nobody to answer prompts, so each CLI gets its auto-approve switch by default (docs/CAPTAINS.md "Running agents safely"). Per-captain toggle: "Auto-approve agent tool use" in the dashboard or autoApprove on create_captain/update_captain (stored in RuntimeOptionsJson, read via CaptainRuntimeOptions.GetAutoApprove). Vessel.AutoApprove (nullable, migration v76) overrides the captain for missions on that vessel (CaptainRuntimeOptions.WithEffectiveAutoApprove in AgentLifecycleHandler.HandleLaunchAgentAsync).

Without auto-approve: Claude Code uses --permission-mode acceptEdits plus --allowedTools mcp__armada (shell commands need allow rules in the project's Claude settings); Codex keeps --sandbox workspace-write; Mux uses its per-captain approval policy. Consequence: a captain can run anything the Admiral's (or Harbor's) user can; the worktree is not a sandbox. Ask Armada approval gating covers Armada MCP tools only, not the agent's own shell/file tools. Review audit.command events. Settings Ask.CaptainAutoApprove (default false) governs captains in Ask turns.
