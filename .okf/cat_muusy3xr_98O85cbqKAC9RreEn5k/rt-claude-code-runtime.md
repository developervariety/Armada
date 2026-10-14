---
type: "Reference"
title: "Claude Code runtime: launch flags, env, streaming, resume"
description: "`claude --print --verbose [--output-format stream-json --include-partial-messages] [--model m] --dangerously-skip-permissions`; without auto-approve uses --permission-mode acceptEdits --allowedTools mcp__armada; only runtime with SupportsResume."
timestamp: "2026-10-05T05:34:11.7529620Z"
created: "2026-10-05T05:34:11.1312370Z"
slug: "rt-claude-code-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-launch-isolation", "rt-ask-thread-mcp-token", "rt-reasoning-effort", "rt-auto-approve-flags"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/ClaudeCodeRuntime.cs (executable "claude", ExecutablePath settable). Arguments: --print --verbose; --output-format stream-json --include-partial-messages only when StreamJsonOutput (Ask and interactive planning turns: real token deltas, final message and usage from the result event; missions keep plain --print); --model <model>; if SkipPermissions and the captain's effective auto-approve (CaptainRuntimeOptions.GetAutoApprove, vessel AutoApprove override wins) -> --dangerously-skip-permissions, else --permission-mode acceptEdits plus --allowedTools mcp__armada (Armada tools stay usable because Armada authorizes each call). Prompt on stdin.

Environment: CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT=1; removes CLAUDECODE and CLAUDE_CODE_ENTRYPOINT so captains start even when the Admiral was launched from inside a Claude Code session; MAX_THINKING_TOKENS from ReasoningEffort (Off 0, Minimal 2048, Low 4096, Medium 8192, High 16384).
Provider errors parsed by RuntimeProviderErrorParser.TryParseClaudeTextLine / TryParseClaudeStreamJsonLine. Isolated launches add --setting-sources project,local --strict-mcp-config --mcp-config <dir>/armada-mcp.json; Ask turns always use strict per-launch config with X-Token. SupportsResume=true (only runtime). Tool cards from tool_use/tool_result blocks. Instructions file in the dock: CLAUDE.md. Install hint: npm install -g @anthropic-ai/claude-code.
