---
type: "Reference"
title: "Stub runtimes used by tests (no real agent CLIs)"
description: "Tests swap runtimes via AgentRuntimeFactory.Override: StubCaptainRuntime (scripted in-process captain that commits a file and exits 0), StubAgentProcesses (long-running sleep/ping processes), TestAgentRuntime (runs dotnet --version)."
timestamp: "2026-10-05T05:37:50.5843640Z"
created: "2026-10-05T05:37:50.2830970Z"
slug: "rt-stub-runtimes-for-tests"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-runtime-factory"]
version: "1"
salience: "0.5"
---
Location: src/Test.Shared/Infrastructure.
- StubCaptainRuntime (extends BaseAgentRuntime, Name "StubCaptain", SupportsPlanningSessions=true): installed for ClaudeCode through AgentRuntimeFactory.Override. A thread-scoped turn (Ask Armada, captain chat) runs StubCaptainBehavior.OnTurn with the turn's MCP session token; a mission launch (MCP port + log file, no session token) writes one file in the dock, commits it, and exits 0 so the real completion and landing pipeline runs; any other prompt (planning, refinement, vessel context, categorization) answers with StubCaptainBehavior.OnPrompt. Synthetic PIDs from 2,100,000,000; minimum mission lifetime 500 ms.
- StubAgentProcesses: makes every CLI runtime of a started server launch a small long-running OS process (sleep on Linux/macOS, ping on Windows) so dispatch works identically on machines without Claude/Codex; missions stay InProgress until stopped. Lists CliRuntimes; records StartedProcessIds.
- TestAgentRuntime: minimal BaseAgentRuntime running `dotnet --version` (CommandOverride/ArgsOverride) for launch, invalid-command, and output-streaming tests.
Related helpers: StubCaptainPromptRunner, StubAskTurnRunner, StallTestFixture, ScriptedHarborTransport. Reason: CI runners have no agent CLIs; tests must never depend on them.
