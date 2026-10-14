---
type: "Project"
title: "Delivery subsystem: profiles, checks, releases, environments, deployments, incidents, runbooks, GitHub"
description: "WorkflowProfileService resolves commands; CheckRunService executes them as CheckRuns; ReleaseService, DeploymentEnvironmentService, DeploymentService (approval, verify, rollback, rollout windows), IncidentService, RunbookService build delivery records; GitHubIntegrationService pulls issues/PRs/Actions; HistoricalTimelineService unifies history."
timestamp: "2026-10-05T05:43:07.5178080Z"
created: "2026-10-05T05:43:07.1125400Z"
slug: "arch-delivery-subsystem"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-check-run", "term-workflow-profile", "term-deployment-environment", "term-release", "term-incident", "term-runbook"]
version: "1"
salience: "0.5"
---
Services (src/Armada.Core/Services, all constructed in ArmadaServer.StartAsync): WorkflowProfileService (resolution Global -> Fleet -> Vessel, validation), ProjectProfileService, VesselReadinessService (readiness issues and preflight warnings used by planning, dispatch, checks, and health), CheckRunService + CheckRunParsingService (execute workflow-profile commands as CheckRuns with logs, artifacts, Jest/Istanbul parsing; OnCheckRunChanged -> check-run.changed), DeploymentEnvironmentService (seeded defaults, verification definitions), ReleaseService (version via ReleaseVersioningCommand, links, artifacts), DeploymentService (approval -> deploy check -> smoke/health/verification checks -> rollback/rollback verification on the same record; MonitorRolloutWindowsAsync every health tick; OnDeploymentChanged -> deployment.changed/progress), IncidentService (OnIncidentChanged), RunbookService (playbook-backed runbooks and executions; OnRunbookExecutionChanged), ObjectiveService (OnObjectiveChanged), GitHubIntegrationService (pull-based: import an issue/PR into an objective, sync recent GitHub Actions runs into External check runs, load PR review/comment/required-check evidence; token = settings GitHubToken or vessel GitHubTokenOverride, never returned), LandingPreviewService, HistoricalTimelineService (GET /api/v1/history: unified cross-entity timeline filterable by objectiveId etc.). Operator workflow: docs/DELIVERY_OPERATIONS.md (internal-first; no external CI/CD ingestion beyond GitHub pulls). Check run writes require tenant admin.
