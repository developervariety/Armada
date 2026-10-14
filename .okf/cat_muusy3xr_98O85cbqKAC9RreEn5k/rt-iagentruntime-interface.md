---
type: "Reference"
title: "IAgentRuntime: the runtime adapter seam and its events"
description: "Every captain runtime implements Armada.Runtimes.Interfaces.IAgentRuntime: StartAsync/StopAsync/IsRunningAsync plus events OnOutputReceived, OnStdoutReceived, OnProcessStarted, OnProcessExited, OnProviderError."
timestamp: "2026-10-05T05:33:48.4051830Z"
created: "2026-10-05T05:33:47.9951780Z"
slug: "rt-iagentruntime-interface"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-runtime-factory", "rt-base-cli-launch-conventions", "arch-agent-lifecycle-handler"]
version: "1"
salience: "0.5"
---
Interface src/Armada.Runtimes/Interfaces/IAgentRuntime.cs: Name, SupportsResume, SupportsPlanningSessions; events OnOutputReceived(pid, line) (stdout+stderr), OnStdoutReceived(pid, line) (stdout only; chat/planning use this so CLI stderr banners stay out of replies), OnProcessStarted(pid), OnProcessExited(pid, exitCode?), OnProviderError(pid, RuntimeProviderError); Task<int> StartAsync(workingDirectory, prompt, environment, logFilePath, finalMessageFilePath, model, captain, isolateLaunch, mcpPort, showThinking, token) returning a PID; StopAsync; IsRunningAsync. IInteractiveAgentRuntime adds StartInteractiveAsync/SendAsync.

Implementations: BaseAgentRuntime (CLI process base) -> ClaudeCodeRuntime, CodexRuntime, GeminiRuntime, CursorRuntime, MuxRuntime, OpenCodeRuntime; ApiAgentRuntime (in-process loop, synthetic PIDs raised via base helpers); RemoteAgentRuntime (process on a Harbor). Process location is abstracted by IHostProcessExecutor: LocalHostProcessExecutor or RemoteHostProcessExecutor (Harbor). AgentLifecycleHandler.HandleLaunchAgentAsync wires the events (output -> progress/heartbeat, stdout -> [ARMADA:STATUS], provider error -> exit classification, exit -> completion). See [[rt-runtime-factory]].
