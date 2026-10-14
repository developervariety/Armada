---
type: "Reference"
title: "Vessel health (vhl_): ten graded criteria per repository"
description: "VesselHealth (vhl_) = one current row per vessel grading GitDivergence, WorkingTree, Branches, CommitRecency, Dependencies, Vulnerabilities, TestInfrastructure, ContinuousIntegration, ArmadaReadiness, MissionOutcomes as Pass/Warn/Fail/NotApplicable/Unknown."
timestamp: "2026-10-05T05:31:36.6210220Z"
created: "2026-10-05T05:31:36.1656330Z"
slug: "term-vessel-health"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-vessel-health-evaluator", "term-fleet-action"]
version: "1"
salience: "0.5"
---
Term: Vessel health (src/Armada.Core/Models/VesselHealth.cs prefix vhl_; child rows VesselHealthFinding vhf_, VesselDependency vdp_, VesselHealthOverride vho_; tables from migration 73).

VesselHealthStatusEnum: Pass, Warn, Fail, NotApplicable, Unknown (a check that could not run is Unknown, never Pass). VesselHealthCriterionEnum: GitDivergence, WorkingTree, Branches (stale and leftover armada/* branches), CommitRecency (informational), Dependencies (dotnet list --outdated / npm outdated), Vulnerabilities (dotnet --vulnerable / npm audit), TestInfrastructure (test projects + latest test check run), ContinuousIntegration (also HasLicense/HasReadme), ArmadaReadiness, MissionOutcomes, plus Overall.
Overall = worst of ScoredCriteria (default all except ContinuousIntegration and CommitRecency) after overrides; Unknown/NotApplicable are neutral. Status columns store EFFECTIVE values (after overrides); raw results stay on findings. An override (criterion or Overall, with note) recomputes immediately without re-evaluating. Never-evaluated vessels still appear in enumeration with a null Id. Each finding has a stable detail code (e.g. BareRepository, FetchFailed, ToolMissing, RestoreRequired, Timeout, ParseError, RepositoryUnavailable) so UI text is localizable. See [[arch-vessel-health-evaluator]]; CLI `armada health`; MCP vessel_health, evaluate_vessel_health, set_vessel_health_override.
