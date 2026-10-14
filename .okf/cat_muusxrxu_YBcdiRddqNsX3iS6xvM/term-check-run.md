---
type: "Reference"
title: "Check run (chk_): durable record of a build/test/deploy command"
description: "CheckRun (chk_) records one execution of a workflow-profile command (Lint, Build, UnitTest, ... Deploy, Rollback, SmokeTest, HealthCheck, Custom) with status Pending/Running/Passed/Failed/Canceled, logs, artifacts, test and coverage summaries."
timestamp: "2026-10-05T05:31:51.7733330Z"
created: "2026-10-05T05:31:51.3605920Z"
slug: "term-check-run"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-workflow-profile", "arch-delivery-subsystem"]
version: "1"
salience: "0.5"
---
Term: Check run (src/Armada.Core/Models/CheckRun.cs, prefix chk_; CheckRunService, CheckRunParsingService). A structured, retryable execution record for build/test/deploy/verification work, shown under Delivery > Checks.

CheckRunTypeEnum: Lint=0, Build, UnitTest, IntegrationTest, E2ETest, Migration, SecurityScan, Performance, Package, DeploymentVerification, RollbackVerification, PublishArtifact, ReleaseVersioning, Changelog, Deploy, Rollback, SmokeTest, HealthCheck, Custom=18. CheckRunStatusEnum: Pending=0, Running, Passed, Failed, Canceled. CheckRunSourceEnum: Armada (executed by Armada from a workflow profile) or External (imported, e.g. GitHub Actions sync).
Carries vessel/mission/voyage links, branch and commit, exit code, output, artifacts (CheckRunArtifact), parsed test summaries (CheckRunTestSummary; Jest JSON) and coverage (Istanbul). Commands come from the resolved [[term-workflow-profile]]. Writes need tenant admin. Vessel health's TestInfrastructure criterion reads the latest test-type check run. REST /api/v1/check-runs; MCP run_check, get_check_run, retry_check_run; WebSocket check-run.changed.
