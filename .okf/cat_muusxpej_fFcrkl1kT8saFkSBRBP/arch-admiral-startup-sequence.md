---
type: "Project"
title: "Admiral startup sequence (ArmadaServer.StartAsync)"
description: "Telemetry -> DB driver -> pre-migration backup -> migrate -> API key -> password hash upgrade -> default-credential policy -> services -> seeding -> fleet action runner -> auth -> handlers/hooks -> Watson REST+/ws+Harbor link -> MCP -> Ask tracker, tunnel -> session/import recovery -> health loop."
timestamp: "2026-10-05T05:38:30.4849870Z"
created: "2026-10-05T05:38:29.5427900Z"
slug: "arch-admiral-startup-sequence"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-admiral-health-check-loop", "data-pre-migration-backup", "term-admiral"]
version: "1"
salience: "0.5"
---
src/Armada.Server/ArmadaServer.cs StartAsync, in order:
1. ArmadaTelemetryHost.Start(settings.Telemetry).
2. DatabaseDriverFactory.Create; MigrationBackupService.PrepareAsync (SQLite file copy or server-provider dump warning/refusal); _Database.InitializeAsync (migrations).
3. EnsureApiKeyAsync (generate "ak_..." local API key into settings.json for the CLI); PasswordHashUpgradeService (SHA-256 -> PBKDF2); EnforceDefaultCredentialPolicyAsync (ARMADA_INITIAL_ADMIN_PASSWORD, retire bearer "default", refuse non-loopback hostname with default creds unless AllowDefaultCredentialsOnNetwork -> UnsafeListenerConfigurationException).
4. Services: GitService, DockService, CaptainService, PromptTemplateService, DefinitionOfDoneGate, MissionService, VoyageService, EscalationService, AdmiralService, MergeQueueService, JobService, VesselService, MissionRecoveryCoordinator, LandingService, MessageTemplateService, AgentRuntimeFactory, FleetCategorization/VesselImport, Workspace, RequestHistoryCapture, WorkflowProfile/ProjectProfile/VesselReadiness/Environment/CheckRun, VesselHealth (evaluator + service), Objective/Release/Deployment/Incident/Runbook, GitHubIntegration, LandingPreview, HistoricalTimeline, ModelEndpoint, Harbor (+HarborConnectionManager, HarborLinkEndpoint), RemoteTunnelManager + RemoteDashboardRelayService.
5. Seeding: prompt templates, personas/pipelines, deployment environments, fleet actions (all tenants); FleetActionRunner.StartAsync (restart recovery before any route can create a run).
6. Auth: SessionTokenService (auto-generates key), AuthenticationService, LoginRateLimiter, AuthorizationService; synthetic admin for ApiKey. LogRotation, DataExpiry, Retention.
7. MissionLandingHandler + AgentLifecycleHandler; wire AdmiralService hooks (OnLaunchAgent, OnStopAgent, OnCaptureDiff, OnMissionComplete, OnVoyageComplete, OnReconcilePullRequest), CanAssignMissionAsync (RequireHarborForLaunch).
8. Watson Webserver + OpenAPI tags; PreRouting (auth /ws upgrades, RouteAuthorizationRegistry check), PostRouting (CORS, request history); ArmadaWebSocketHub; PlanningSessionCoordinator, ObjectiveRefinementCoordinator, CaptainToolService, CaptainChatService, Ask services; RegisterRoutes, MarkExperimentalRoutes, InitializeDashboard; /ws and Harbor.LinkPath WebSockets; _App.Start.
9. McpHttpServer (0.0.0.0 -> "*"), IncludeToolExceptionMessages, RateLimits.ToolCallsPerSecond, AuthenticationHandler; RegisterMcpTools; StartMcpListener.
10. AskWorkTracker.Start, RemoteTunnel.Start; recover/maintain planning sessions, vessel import, refinement sessions; HealthCheckLoopAsync.
