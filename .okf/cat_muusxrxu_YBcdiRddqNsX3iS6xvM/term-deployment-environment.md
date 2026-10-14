---
type: "Reference"
title: "Deployment environment (env_) and Deployment (dpl_)"
description: "DeploymentEnvironment (env_) is a named rollout target (Development, Test, Staging, Production, CustomerHosted, Custom); Deployment (dpl_) is one rollout with approval, verification, and rollback state (PendingApproval..RolledBack)."
timestamp: "2026-10-05T05:32:13.4263560Z"
created: "2026-10-05T05:32:12.9041070Z"
slug: "term-deployment-environment"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-release", "term-incident", "arch-delivery-subsystem", "term-workflow-profile"]
version: "1"
salience: "0.5"
---
Terms:
- DeploymentEnvironment (src/Armada.Core/Models/DeploymentEnvironment.cs, prefix env_; DeploymentEnvironmentService): rollout target metadata for a vessel; Kind (EnvironmentKindEnum Development, Test, Staging, Production, CustomerHosted, Custom); approval requirement; rollout-monitoring window settings; verification definitions (dvd_). Defaults seeded at startup (SeedDefaultsAsync). WebSocket environment.health.
- Deployment (src/Armada.Core/Models/Deployment.cs, prefix dpl_; DeploymentService): one rollout of a source ref or Release to an environment using the workflow profile's Deploy command. Status DeploymentStatusEnum: PendingApproval=0, Running, Succeeded, VerificationFailed, Failed, Denied, RollingBack, RolledBack=7; verification DeploymentVerificationStatusEnum NotRun, Running, Passed, Failed, Partial, Skipped. Rollback is recorded on the same deployment record (no separate rollback deployment). Linked check runs: deploy, smoke test, health check, verification, rollback, rollback verification. DeploymentService.MonitorRolloutWindowsAsync runs on every health tick. Approvals appear in the inbox (deployment_approval). REST /api/v1/deployments, /environments; MCP create/approve/verify/rollback_deployment; WebSocket deployment.changed, deployment.progress.
