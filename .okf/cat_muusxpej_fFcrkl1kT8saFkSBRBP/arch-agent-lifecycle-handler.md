---
type: "Project"
title: "AgentLifecycleHandler: launching, observing, and stopping captain processes"
description: "src/Armada.Server/AgentLifecycleHandler.cs implements Admiral.OnLaunchAgent/OnStopAgent: picks local vs Harbor executor, builds the prompt, starts the runtime, maps PID -> captain/mission, runs a liveness loop, parses output signals, and forwards exits to AdmiralService.HandleProcessExitAsync."
timestamp: "2026-10-05T05:41:14.7539330Z"
created: "2026-10-05T05:41:14.2869660Z"
slug: "arch-agent-lifecycle-handler"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["rt-iagentruntime-interface", "rt-remote-harbor-runtime", "rt-armada-protocol-lines", "rt-stall-detection", "rt-runtime-exit-info"]
version: "1"
salience: "0.5"
---
Wired in ArmadaServer.StartAsync: Admiral.OnLaunchAgent = HandleLaunchAgentAsync, OnStopAgent = HandleStopAgentAsync, OnIsProcessExitHandled = IsProcessExitHandled; MissionService.OnGetMissionOutput = GetAndClearMissionOutput; SetHarborConnections, SetEndpointResolver, SetWebSocketHub.

Launch: ResolveLaunchExecutorAsync (Harbor routing with dock affinity, else LocalHostProcessExecutor); executor.CreateRuntime(captain.Runtime); subscribe OnProcessStarted (PendingLaunches -> PID map), OnOutputReceived (HandleAgentOutput: accumulate stdout for handoff, ProgressParser signals incl. papercuts; HandleAgentHeartbeat: LastHeartbeatUtc), OnStdoutReceived (HandleAgentStdout: [ARMADA:STATUS]), OnProviderError (stored for exit classification), OnProcessExited. Prompt = MissionPromptBuilder.BuildLaunchPromptAsync + commit-trailer instructions (MessageTemplateService). Log paths under LogDirectory (missions/, final-messages/, captains/<id>.current). Effective auto-approve = vessel override or captain setting. StartAsync with model, isolateLaunch=IsolateCaptainLaunch, mcpPort=McpPort. Then StartProcessLivenessHeartbeat (updates LastProcessAliveUtc only), event captain.launched, WebSocket captain/mission broadcasts.
Exit: HandleAgentProcessExited -> RuntimeFailureClassifier.Decide(exitCode, providerError) -> AdmiralService.HandleProcessExitAsync (dedup so the health check does not race it). Stop: ResolveStopExecutor routes to the Harbor that owns the PID. Also ValidateModelAsync / Mux / ApiEndpoint captain validation.
