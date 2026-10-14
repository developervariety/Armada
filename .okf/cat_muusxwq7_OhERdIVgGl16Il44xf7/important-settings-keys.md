---
type: "Reference"
title: "Important Admiral settings keys and defaults"
description: "admiralPort 7890, mcpPort 7891, rest.hostname localhost, deploymentMode Local, allowSelfRegistration false, allowDefaultCredentialsOnNetwork false, mcp.allowUnauthenticatedLoopback true, heartbeat 10 s, stall 10 min, etc."
timestamp: "2026-10-05T05:29:37.1108080Z"
created: "2026-10-05T05:29:36.3710080Z"
slug: "important-settings-keys"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["settings-json-loading", "retention-settings", "ports-and-listeners", "default-credentials-bind-guard"]
version: "1"
salience: "0.5"
---
Top-level camelCase keys (ArmadaSettings) with defaults:
- admiralPort 7890, mcpPort 7891, webSocketEnabled true, rest.hostname "localhost" (use 0.0.0.0, * or + for all interfaces), rest.ssl false (unsupported; terminate TLS in front).
- deploymentMode Local (Split for Harbor mode), requireHarborForLaunch false, harbor.requireAuth false, harbor.linkPath /v1.0/harbor/connect, harbor.heartbeatTimeoutSeconds 45, harbor.advertisedMcpBaseUrl.
- Security: apiKey (generated, ak_...), sessionTokenEncryptionKey (auto), allowSelfRegistration false, allowDefaultCredentialsOnNetwork false, requireAuthForShutdown (deprecated, ignored), mcp.allowUnauthenticatedLoopback true, mcp.toolCallsPerSecond 100, loginRateLimit.*, ask.captainAutoApprove false.
- Captains: heartbeatIntervalSeconds 10, stallThresholdMinutes 10, maxRecoveryAttempts 3, maxMissionRuntimeMinutes 240, reviewTimeoutMinutes 1440, maxConcurrentMissions 0 (unlimited), captainQuarantineMinutes, isolateCaptainLaunch false, defaultRuntime, agents[].
- Landing: landingMode (null), branchCleanupPolicy LocalOnly, autoPush true, autoCreatePullRequests false, autoMergePullRequests false, mergeQueueTestCommand, gitHubToken (secret).
- Logs: maxLogFileSizeBytes 10 MB, maxLogFileCount 5, syslogServers [127.0.0.1:514].
- Retention: dataRetentionDays 30, requestHistoryEnabled true, requestHistoryRetentionDays 30, requestHistoryMaxBodyBytes 32 KB, planningSessionRetentionDays 0, retention.* (Ask threads, jobs, import batches), fleetActions.runRetentionDays 30.
- Rebuild: selfVesselId, rebuildSlotRetentionCount 3, rebuildSupervisorHarborId (experimental).
- dashboardPath (null: auto-detect), notifications true, terminalBell true, database.*, telemetry.*, remoteControl.*.
