---
type: "Project"
title: "Vessel health evaluation: evaluator, criteria plug-ins, schedule, jobs"
description: "VesselHealthService runs evaluations as Report jobs (one per tenant, MaxConcurrency 4 vessels); VesselHealthEvaluator picks working dir or bare clone, fetches, runs IVesselHealthCriterion plug-ins with isolation, caches dependency scans by manifest hash for 24h, applies overrides, rolls up, persists."
timestamp: "2026-10-05T05:42:58.8612310Z"
created: "2026-10-05T05:42:58.1140200Z"
slug: "arch-vessel-health-evaluator"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-vessel-health", "term-job", "arch-admiral-health-check-loop"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Core/Services/Health/ (VesselHealthService, VesselHealthEvaluator.CreateDefaultCriteria, IVesselHealthCriterion implementations GitDivergenceCriterion, WorkingTreeCriterion, BranchesCriterion, CommitRecencyCriterion, DependenciesCriterion, VulnerabilitiesCriterion, TestInfrastructureCriterion, ContinuousIntegrationCriterion, ArmadaReadinessCriterion, MissionOutcomesCriterion; DependencyScanner + DependencyToolRunner (dotnet list --format json, npm outdated/audit --json, parsed into typed Json/* classes), ManifestHasher, VesselHealthEvaluationCache, VesselHealthRollup, VesselHealthTelemetry). Built in ArmadaServer with a LocalHostCommandExecutor.

Evaluation: path = WorkingDirectory if it exists and is a git checkout, else bare LocalPath (BareRepository -> several criteria NotApplicable; neither -> RepositoryUnavailable); git fetch --all --prune when FetchBeforeEvaluate (failure -> FetchFailed Unknown); each criterion timed and exception-isolated (stage:<Criterion> spans); dependency/vulnerability checks skipped while the manifest hash is unchanged and results younger than DependencyMaxAgeHours (24) unless forced (manual evaluations force); failed scans never count as fresh; overrides applied to effective status columns; rollup over ScoredCriteria; findings replace previous ones (no history). Schedule: VesselHealthService.RunScheduleAsync on each health tick starts one Report job per tenant every IntervalMinutes (360; 0 off), measured from the last job in the DB so restarts do not re-trigger; a second request while running returns the running job id with 409. Settings RepositoryHealth.* applied live. Metrics armada_health_evaluations_total{outcome}, armada_health_criterion_duration_seconds{criterion}.
