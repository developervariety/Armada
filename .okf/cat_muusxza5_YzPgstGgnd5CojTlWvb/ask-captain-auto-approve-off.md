---
type: "Reference"
title: "Ask turns run captains with auto-approve forced off (ask.captainAutoApprove false)"
description: "Any authenticated user can start an Ask turn, so turns and narrations launch the CLI without its bypass flag (Claude Code: --permission-mode acceptEdits --allowedTools mcp__armada) unless ask.captainAutoApprove is true."
timestamp: "2026-10-05T05:34:24.2667370Z"
created: "2026-10-05T05:34:23.8772460Z"
slug: "ask-captain-auto-approve-off"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["captain-auto-approve-flags", "ask-thread-token-precedence", "open-o02-ask-chat-planning-residual"]
version: "1"
salience: "0.5"
---
AskSettings.CaptainAutoApprove default false (F-27, closing O-02 for Ask). Ask thread turns and narrations launch the captain with auto-approve off regardless of the captain's own setting. For Claude Code that is `--permission-mode acceptEdits --allowedTools mcp__armada`: file edits allowed inside the turn's temporary working directory, Armada MCP tools allowed (Armada authorizes each call), shell and edits outside are refused in print mode rather than prompted, so a turn cannot hang. Other runtimes use their documented auto-approve-off flags (Codex --sandbox workspace-write, Gemini --approval-mode auto_edit, Mux --approval-policy deny, no --force for Cursor, no --auto for OpenCode). Leave captainAutoApprove off unless every account that can use Ask is trusted with a shell on the Admiral host: Ask's proposal gate covers Armada MCP tools, not the CLI's own shell. The TUI and dashboard expose a per-thread auto-approve toggle (Ctrl+Y in the TUI) that asks first and shows a banner. Residual: acceptEdits still edits files in the temp dir (O-02); direct captain chat and planning (TenantAdmin) use the captain's own setting.
