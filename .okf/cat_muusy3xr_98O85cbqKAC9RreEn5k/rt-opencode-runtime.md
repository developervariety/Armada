---
type: "Reference"
title: "OpenCode runtime: opencode run --format json --auto"
description: "`opencode run --format json [--model m] [--variant v] [--thinking] --auto --dir <wd>`; no launch isolation support; not probed by armada doctor; not in `armada captain add --runtime` (create via dashboard/REST/MCP)."
timestamp: "2026-10-05T05:34:49.7013610Z"
created: "2026-10-05T05:34:49.2809440Z"
slug: "rt-opencode-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-typed-stream-events", "rt-ask-thread-mcp-token", "rt-runtime-detection"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/OpenCodeRuntime.cs with OpenCodeCommandBuilder (executable "opencode"). Command: opencode run --format json [--model <model>] [--variant <variant>] [--thinking] --auto --dir <workingDirectory>; --auto is the auto-approve switch.

Reasoning effort maps to --variant (Minimal/Low -> minimal, Medium/High -> high, Off -> none). Streams typed OpenCodeStreamEvent JSON (text, reasoning, tool_use, step_start, step_finish, error) giving native thinking and tool cards. No entry in CaptainLaunchIsolationPlanner, so IsolateCaptainLaunch has no effect; it always reads ~/.config/opencode/opencode.json(c) where `armada mcp install` writes a remote entry under "mcp". Ask turns: OPENCODE_CONFIG_CONTENT merged over the user's config with a fresh remote entry (header {env:ARMADA_MCP_TOKEN}); existing armada aliases disabled with "enabled": false (named armada or armada_ask when the user already has armada). Missing from RuntimeDetectionService probes (armada doctor) and from the `captain add --runtime` CLI shortcut; fully supported otherwise, including planning (inherits base CLI runtime).
