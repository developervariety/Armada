---
type: "Reference"
title: "Runtime detection, versions, and model validation"
description: "RuntimeDetectionService probes PATH in order claude, codex, gemini, cursor-agent, mux by starting `<exe> --version` (no version parsing, OpenCode not probed); DefaultRuntime null = auto-detect; captain models are validated by a real launch before save."
timestamp: "2026-10-05T05:37:50.9060470Z"
created: "2026-10-05T05:37:50.6245700Z"
slug: "rt-runtime-detection"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-opencode-runtime", "rt-mux-runtime"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/RuntimeDetectionService.cs: DetectDefaultRuntime returns the first available of claude -> ClaudeCode, codex -> Codex, gemini -> Gemini, cursor-agent -> Cursor, mux -> Mux; DetectAllRuntimes lists them; IsCommandAvailable starts `<exe> --version` and treats "the process started" as installed (ignores output and exit code). Used by `armada doctor` (install hints: npm install -g @anthropic-ai/claude-code, @openai/codex, @google/gemini-cli; Cursor docs; Mux on PATH) and default runtime selection (settings DefaultRuntime, null = auto-detect). OpenCode is not probed; `armada mcp install` finds it via ~/.config/opencode or opencode on PATH.

No minimum CLI version is enforced anywhere: "supported" means the CLI still accepts Armada's flags; a renamed flag fails the launch and shows in the mission log, so pin CLI versions on captain hosts. Model validation: AgentLifecycleHandler.ValidateModelAsync launches the runtime with the requested model in a temp dir (armada-model-validation-<guid>; Codex gets git init first) before REST/MCP captain create/update saves; Mux captains are validated with `mux probe --require-tools`; ApiEndpoint captains validate their endpoint.
