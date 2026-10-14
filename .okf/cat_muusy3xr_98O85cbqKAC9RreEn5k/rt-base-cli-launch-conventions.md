---
type: "Reference"
title: "CLI runtime launch conventions (BaseAgentRuntime)"
description: "CLI captains get the prompt on stdin (except Mux), env MSBUILDDISABLENODEREUSE=1 and DOTNET_CLI_TELEMETRY_OPTOUT=1, a per-mission log file, a final-message file, graceful stop (close stdin, 10s) then kill tree."
timestamp: "2026-10-05T05:34:03.1862110Z"
created: "2026-10-05T05:34:02.7768580Z"
slug: "rt-base-cli-launch-conventions"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-iagentruntime-interface", "rt-launch-isolation", "rt-runtime-exit-info"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/BaseAgentRuntime.cs (abstract): subclasses supply GetCommand(), BuildArguments(...), RuntimeType, optional UsePromptStdin, ApplyEnvironment, TryParseProviderError (only for the CLI's own machine-readable error channel, never keyword matching).

Conventions: the prompt is written to stdin for every CLI runtime except Mux (positional last argument) because Windows npm .cmd wrappers truncate multi-line arguments at the first newline. All launches set MSBUILDDISABLENODEREUSE=1 and DOTNET_CLI_TELEMETRY_OPTOUT=1 (captains build .NET often; orphaned MSBuild nodes outlive missions). Output: AgentLifecycleHandler passes logFilePath = <LogDirectory>/missions/<missionId>.log, finalMessageFilePath = <LogDirectory>/final-messages/<missionId>.txt, and writes <LogDirectory>/captains/<captainId>.current pointing at the log. Stop: close stdin, wait the graceful window (default 10000 ms, configurable for tests), then kill the process tree; stdout/stderr drain waits up to 5000 ms after exit. Isolation/thread-scoped MCP plans are appended via an overridable hook (Mux inserts before its prompt). A negative exit code means interruption (cancellation maps to -1).
