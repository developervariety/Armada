---
type: "Reference"
title: "Typed runtime stream events (Armada.Core/Protocol)"
description: "Structured runtime output is deserialized into typed classes (ClaudeStreamLine, CodexStreamEvent, MuxProtocolEvent, OpenCodeStreamEvent) via RuntimeStreamJson.TryDeserializeLine; a line counts only if its type/eventType is in the class's KnownTypes set."
timestamp: "2026-10-05T05:35:39.4036030Z"
created: "2026-10-05T05:35:38.9841140Z"
slug: "rt-typed-stream-events"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-runtime-exit-info", "term-token-usage"]
version: "1"
salience: "0.5"
---
Folder src/Armada.Core/Protocol. RuntimeStreamJson.TryDeserializeLine<T> accepts a line only if it is a single JSON object ({...}); malformed JSON returns false, never throws.

Event classes and discriminators:
- Claude Code stream-json: ClaudeStreamLine (KnownTypes: system, assistant, user, result, stream_event, tool_use) with ClaudeStreamMessage, ClaudeStreamContentBlock, ClaudeStreamDelta, ClaudeStreamUsage, ClaudeToolResultContent (+ converters); result carries final text and usage.
- Codex exec --json: CodexStreamEvent (thread.started, turn.started, turn.completed, turn.failed, item.started, item.updated, item.completed, error) with CodexStreamItem, CodexUsage.
- Mux JSONL: MuxProtocolEvent keyed by eventType (run_started, assistant_text, assistant_thinking, tool_call_proposed, tool_call_approved, tool_call_completed, error, heartbeat, context_status, context_compacted, run_completed, task_plan_updated) with MuxToolCall/MuxToolResult.
- OpenCode --format json: OpenCodeStreamEvent (text, reasoning, tool_use, step_start, step_finish, error) with OpenCodePart, OpenCodeToolState/Metadata.
Rule (FRAGILITY_REMEDIATION): output is classified by these typed shapes, never by keyword or substring matching; a JSON line with an unknown discriminator is ordinary text. These drive chat streaming, tool cards, thinking display, token usage, and provider-error detection.
