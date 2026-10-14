---
type: "Reference"
title: "Captain reasoning effort translation per runtime"
description: "Captain.ReasoningEffort (Off/Minimal/Low/Medium/High) maps via ReasoningEffortTranslator to Claude MAX_THINKING_TOKENS (0/2048/4096/8192/16384), Codex model_reasoning_effort, Mux --effort, OpenCode --variant; Gemini, Cursor, ApiEndpoint ignore it."
timestamp: "2026-10-05T05:37:49.9154380Z"
created: "2026-10-05T05:37:49.7157500Z"
slug: "rt-reasoning-effort"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-claude-code-runtime", "rt-mux-runtime", "rt-opencode-runtime", "rt-codex-runtime"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/ReasoningEffortTranslator.cs; enum ReasoningEffortEnum Off, Minimal, Low, Medium, High.

| Setting | Claude Code env MAX_THINKING_TOKENS | Codex -c model_reasoning_effort | Mux --effort | OpenCode --variant |
| Off | 0 | not passed | off | not passed |
| Minimal | 2048 | minimal | minimal | minimal |
| Low | 4096 | low | low | minimal |
| Medium | 8192 | medium | medium | high |
| High | 16384 | high | high | high |
Gemini, Cursor, and ApiEndpoint ignore the setting. Thinking DISPLAY is separate: only Mux (--show-thinking, assistant_thinking) and OpenCode (--thinking, reasoning events) have native thinking channels; for other runtimes Armada asks the model to wrap reasoning in one <thinking>...</thinking> block and lifts it out (the model describing its reasoning, not a provider trace).
