---
type: "Reference"
title: "Stub agent runtimes: StubAgentProcesses and StubCaptainRuntime"
description: "Tests never start a real agent CLI: StubAgentProcesses launches sleep (ping on Windows) for every CLI runtime; StubCaptainRuntime answers Ask/missions/planning through MCP."
timestamp: "2026-10-05T05:27:00.8851280Z"
created: "2026-10-05T05:27:00.2626550Z"
slug: "stub-agent-runtimes-in-tests"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["decision-test-runtime-stubs", "e2e-server-fixture", "install-verify-scripts", "lesson-script-editor-toasts"]
version: "1"
salience: "0.5"
---
StubAgentProcesses (src/Test.Shared/Infrastructure/StubAgentProcesses.cs): InstallOn(server) replaces every CLI runtime (ClaudeCode, Codex, Gemini, Cursor, Mux, OpenCode; listed in CliRuntimes) with a launcher that starts a long-running OS process the test owns (`sleep` on Linux/macOS, `ping` on Windows, 600 s lifetime). Dispatch behaves the same whether or not Claude Code is installed, no model is called, and the mission stays InProgress until StopAll(). ApiEndpoint and Custom runtimes need no stub.

StubCaptainRuntime (installed through AgentRuntimeFactory.Override and ArmadaServer.RuntimeFactory; BaseAgentRuntime has protected raise helpers): answers Ask turns by calling MCP with the turn's session token, commits a file for missions (so landing runs for real), and replies to planning, refinement, context, chat, and categorization prompts. Used by Tui.EndToEnd.Flows and Client.Contract.* suites. Related helpers: StubGitService, StubHostCommandExecutor, RecordingCommandRunner, RecordingNotificationCommandRunner (desktop notifications never call osascript in tests), ScriptedHarborTransport. Install-verify uses a different stub: scripts/common/install-verify/stub_inference.py for an ApiEndpoint captain.
