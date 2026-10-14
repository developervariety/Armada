---
type: "Reference"
title: "Workflow profile (wfp_): a project's build/test/release/deploy commands"
description: "WorkflowProfile (wfp_) stores per-scope (Global/Fleet/Vessel) commands: Lint, Build, Unit/Integration/E2E test, Migration, SecurityScan, Package, ReleaseVersioning, Changelog, Deploy per environment, verification and rollback."
timestamp: "2026-10-05T05:31:59.4147310Z"
created: "2026-10-05T05:31:59.1076010Z"
slug: "term-workflow-profile"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-check-run", "term-project-profile-and-skill", "term-deployment-environment"]
version: "1"
salience: "0.5"
---
Term: Workflow profile (src/Armada.Core/Models/WorkflowProfile.cs, prefix wfp_; WorkflowProfileService). "Delivery recipe" defining how a project builds, tests, releases, and deploys; managed under Configuration > Workflow Profiles.

Fields: Scope (WorkflowProfileScopeEnum Global=0, Fleet=1, Vessel=2) with FleetId/VesselId, IsDefault, OwnershipScope (TenantWide/UserSpecific; named so because Scope already means application scope), LanguageHints, LintCommand, BuildCommand, UnitTestCommand, IntegrationTestCommand, E2ETestCommand, MigrationCommand, SecurityScanCommand, PerformanceCommand, PackageCommand, DeploymentVerificationCommand, RollbackVerificationCommand, PublishArtifactCommand, ReleaseVersioningCommand, ChangelogGenerationCommand, RequiredSecrets (WorkflowInputReference list), ExpectedArtifacts, Environments (WorkflowEnvironmentProfile: per-environment Deploy/Rollback/SmokeTest/HealthCheck commands).
Resolution Global -> Fleet -> Vessel (most specific wins); REST /api/v1/workflow-profiles (CRUD, validate, resolve, resolution preview). Used by check runs, releases (version inference), deployments, environments, and vessel readiness. Distinct from [[term-project-profile-and-skill]], which references a workflow profile.
