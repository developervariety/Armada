---
type: "Reference"
title: "Harbor-delegated captains: RemoteAgentRuntime and LocalHarborJobRunner"
description: "When a Harbor is connected and eligible, AgentLifecycleHandler launches via RemoteHostProcessExecutor -> RemoteAgentRuntime (launch/stdin/kill over the link); the Harbor runs it with LocalHarborJobRunner using the normal local runtimes; falls back to local otherwise."
timestamp: "2026-10-05T05:35:06.0454880Z"
created: "2026-10-05T05:35:05.6355590Z"
slug: "rt-remote-harbor-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["arch-harbor-split-mode", "term-harbor", "rt-iagentruntime-interface"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Runtimes/RemoteAgentRuntime.cs, RemoteHostProcessExecutor.cs, LocalHarborJobRunner.cs; selection in Armada.Server/AgentLifecycleHandler.ResolveLaunchExecutorAsync.

Flow: if a HarborConnectionManager is wired and any Harbor is connected, build HarborRoutingRequest {ExistingHarborId = dock.HarborId, PreferredHarborId = vessel.PreferredHarborId, RequestedRuntime = captain.Runtime, RequiredCapabilities = vessel.RequiredCapabilities} and call SelectHarborAsync(tenant). On success, RecordHarborAffinityAsync pins mission + dock to the Harbor and a RemoteHostProcessExecutor creates a RemoteAgentRuntime that sends `launch` over the link and translates started/output/exited messages into IAgentRuntime events; liveness and stop route by job id (ResolveStopExecutor maps PID -> Harbor). Otherwise (no Harbor, nothing eligible, routing error) the captain runs locally. Settings RequireHarborForLaunch=true instead defers assignment until an eligible Harbor owned by the mission's user is connected (MissionService.CanAssignMissionAsync). Harbor-hosted runtimes do not support planning sessions; captains are not used for fleet categorization on Harbors. Exit codes < 0 from a dropped link count as interruptions (re-dispatch). Experimental in 1.0.
