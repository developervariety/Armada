---
type: "Reference"
title: "Fleet action, run, and target (fac_, far_, fat_)"
description: "FleetAction (fac_) = saved Command (shell in each working dir) or Mission (one voyage per vessel) action; a run (far_) applies it to vessels; each vessel is a target (fat_) with status and reason code."
timestamp: "2026-10-05T05:31:16.0003010Z"
created: "2026-10-05T05:31:15.4788950Z"
slug: "term-fleet-action"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-fleet-actions-runner", "term-vessel-health"]
version: "1"
salience: "0.5"
---
Terms (src/Armada.Core/Models/FleetAction*.cs; docs/FLEET_ACTIONS.md):
- FleetAction (fac_): Kind (FleetActionKindEnum Command | Mission), CommandText or PromptTemplate, RequireCleanTree (default for Command), timeout, Persona (stored but not yet applied), Active (deleting a built-in soft-deletes it so it is not re-seeded).
- FleetActionRun (far_): one invocation over a vessel set, with a snapshot of the definition (ad hoc runs have no action id), Concurrency, Status (FleetActionRunStatusEnum Pending, Running, Completed, CompletedWithFailures, Cancelled, Failed) and counts.
- FleetActionRunTarget (fat_): one vessel; Status (FleetActionTargetStatusEnum Pending, Skipped, Running, Succeeded, Failed, Cancelled, TimedOut), stable reason codes (DirtyTree, HarborUnavailable, NoBuildCommand, DispatchRejected, VoyageFailed, Interrupted), exit code, stdout/stderr tail (MaxOutputBytes, OutputTruncated) or VoyageId.
Template variables: {{vessel.name}}, {{vessel.id}}, {{vessel.defaultBranch}}, {{vessel.workingDirectory}}, {{vessel.buildCommand}}, {{health.summary}}; unknown names rejected (400). Five built-ins per tenant: Fast-forward default branch, Prune merged branches, Build, Update outdated dependencies (Mission), Add a test project (Mission). See [[arch-fleet-actions-runner]].
