# Delivery Operations

This guide covers the current internal-first operator workflow for:

- drafting and curating releases
- executing deployments against named environments
- verifying deployments
- rolling back failed rollouts
- recording incidents and postmortems

It describes the shipped Armada-side workflow in `v0.8.0`. It does not assume external CI, PR providers, or remote multi-host delivery.

## Scope

Armada currently supports:

- `Delivery > Releases` for release drafting, notes, artifacts, and linked work
- `Delivery > Environments` for named rollout targets and rollout-monitoring settings
- `Delivery > Deployments` for deploy, verify, approve, deny, and rollback flows
- `Activity > Incidents` for incident records, hotfix handoff, rollback context, and postmortem notes
- `System > Runbooks` for step-by-step operational runbooks linked to deployments and incidents
- `Activity > History` for reconstructing what happened across releases, deployments, incidents, checks, requests, and runbook executions

Pending check runs are durable gates. Armada's server heartbeat resolves eligible non-deployment gates automatically in bounded batches: voyage and mission checks after completion, release checks after linked work completes, and vessel baseline checks when the vessel is idle. Deployment-linked gates are resolved by `DeploymentService` during deploy, verify, rollback, and rollout-monitoring actions so deployment aggregate state remains authoritative. Deploy and rollback commands remain explicit deployment actions and still honor environment approval requirements.

Autonomous mission recovery is also Armada-native. When a mission reaches `Failed` or `LandingFailed`, Armada opens or updates an Incident and records a completed `Autonomous Mission Recovery` runbook execution. Recoverable non-landing failures can dispatch bounded linked rescue missions. Landing and merge failures stay under landing/merge recovery ownership instead of receiving generic rescue work. Cancelled parent voyages suppress new rescue work and cancel any active linked rescue so operator cancellation does not produce surprise follow-up missions. Serious blockers such as auth/quota, review denial, protected paths, dependency failures, or exhausted recovery budgets remain as open incidents for human review. Live but quiet captains may receive throttled Mail nudges before the hard stall threshold; terminal failed missions should be recovered through incidents/runbooks/rescue dispatch instead of Mail.

Incident resolution is evidence-driven. Failed automatic checks open incidents linked to the failed `chk_` record. Later matching passing checks, completed linked rescue missions, successful verified deployments, shipped releases, or completed rollbacks can automatically mitigate or roll back the incident. Mitigated incidents close after `settings.incidentLifecycle.closeQuietPeriodMinutes`; newer matching failures reopen mitigated incidents and raise severity to at least `High`. A later same-vessel passing check also supersedes stale infrastructure-blocked check incidents, and cancelled/superseded mission evidence can close stale rescue/landing incidents after the quiet window. Operators should produce the linked check/deployment/release/rollback evidence instead of manually closing incidents from prose status alone.

Armada does not yet support:

- provider-backed PR release evidence
- CI/CD provider-ingested deployment status
- remote proxy-driven PR/review evidence ingestion from external providers

## 1. Prepare The Delivery Surface

Before using releases or deployments, make sure the vessel is set up with:

- a valid working directory
- a default branch
- a default captain if you want planning or dispatch handoff
- a workflow profile with the commands you expect to run

Minimum workflow-profile coverage for delivery work:

- `ReleaseVersioningCommand` when you want version inference
- `DeployCommand` for each target environment
- `SmokeTestCommand` or `DeploymentVerificationCommand` where verification should run automatically
- `RollbackCommand` when rollback should be executable through Armada
- `RollbackVerificationCommand` when rollback must produce proof

Use:

- `Fleet > Workspace`
- `Fleet > Vessels`
- `Delivery > Workflow Profiles`
- `Delivery > Checks`

to confirm the vessel is ready before operating on releases or deployments.

Create Pending check records as soon as the delivery intent is known. The automatic resolver updates those records in-place when they become eligible; it does not invent green evidence and it does not bypass approval-gated deploy or rollback actions.

When a check failure blocks delivery, keep retry evidence in Checks. Retrying or recording a newer passing check with the same vessel, context, and check type gives the incident lifecycle enough evidence to mitigate the incident. A newer failed matching check keeps or reopens the incident.

## 2. Draft A Release

You can start a release from:

- `Activity > Objectives`
- `Operations > Voyages`
- `Delivery > Checks`
- `Delivery > Releases > New`

Recommended operator flow:

1. Open the release draft.
2. Confirm the linked vessel, voyages, missions, and check runs are correct.
3. Review derived version, tag, summary, notes, and artifacts.
4. Edit notes before changing status.
5. Refresh the release if new linked checks or artifacts have appeared.

Use release detail when you need to answer:

- what work is shipping
- what evidence exists that it is ready
- what artifacts were produced
- what deployments happened after the release existed

## 3. Deploy To An Environment

Open `Delivery > Deployments` to start a rollout, or launch from release detail when you already have a release record.

Recommended operator flow:

1. Pick the target environment.
2. Confirm the source ref or linked release.
3. Confirm the workflow profile to be used.
4. Review whether approval is required.
5. Start the deployment.

During execution, deployment detail becomes the source of truth for:

- deploy status
- linked check runs
- smoke-test and verification results
- approval comments
- request-history evidence
- rollback state

If the environment requires approval, use:

- `Approve`
- `Deny`

from deployment detail instead of bypassing the workflow manually.

## 4. Verify The Rollout

After deployment starts or completes, use deployment detail to inspect:

- deploy check
- smoke test
- health check
- deployment verification check
- rollout monitoring window
- latest monitoring summary

Use `Verify` when you need to re-run the deployment verification path without re-running the original deployment command.

When verification starts, Armada first looks for a matching Pending check run and executes it in-place. If no matching Pending record exists, it creates a new run so verification evidence is still preserved.

Use `Activity > History` and `Activity > Requests` when you need supporting evidence beyond the deployment record itself.

## Dispatch And Code-Index Preflight

MCP dispatch is durable-first. A successful `armada_dispatch` response means the voyage and mission records were created; assignment, dock provisioning, worktree setup, and captain launch may still be queued. This is expected on first use of a large repository or a serialized vessel. Poll voyage or mission status instead of retrying immediately.

Before dispatch attaches automatic context packs, Armada checks the vessel code-index state. If a post-land or manual refresh is running, dispatch returns `code_index_update_in_progress` and does not create a voyage. Poll `armada_index_status` until `updateInProgress` is false, then retry. Refreshes reuse unchanged chunk embeddings and graph sidecars where possible, so post-land refreshes are incremental rather than full cold re-indexes.

## 5. Roll Back A Failed Release

When a rollout regresses:

1. Open the deployment.
2. Confirm the failing environment and evidence.
3. Use `Rollback`.
4. Review rollback and rollback-verification check runs.
5. Confirm the deployment reaches `RolledBack`.

The current internal-first model records rollback on the deployment record itself rather than creating a separate deployment object for the rollback.

That means the deployment detail page is the authoritative record for:

- original rollout
- verification outcome
- rollback execution
- rollback verification

## 6. Record The Incident

Use `Activity > Incidents` or launch from deployment or environment detail when a deployment needs incident tracking.

Recommended operator flow:

1. Create the incident from the affected deployment or environment when possible.
2. Keep the linked environment, deployment, release, vessel, mission, and voyage context intact.
3. Record:
   - impact
   - root cause
   - recovery notes
   - rollback linkage
   - postmortem notes
4. If a hotfix is needed, use the built-in planning or dispatch handoff from the incident context.

The incident record should be the durable answer to:

- what broke
- what release and deployment were involved
- how recovery happened
- what follow-up work is still required

## 7. Use Runbooks For Repeated Operations

Use `System > Runbooks` when the same release, deploy, rollback, migration, or incident response steps should be guided and repeatable.

Recommended uses:

- pre-release checklists
- manual deploy approvals and checklists
- rollback execution sequences
- incident response steps
- postmortem follow-up actions

Runbook execution history is preserved and appears in the broader delivery timeline.

## 8. Reconstruct The Story Later

When you need to understand what happened after the fact, use:

- `Activity > History` for cross-entity chronology
- `Activity > Requests` for API- and server-level request evidence
- `Delivery > Checks` for execution logs, parsed results, and artifacts
- `Delivery > Releases` for what was intended to ship
- `Delivery > Deployments` for what actually rolled out
- `Activity > Incidents` for failure, recovery, and postmortem context

## Recommended Minimum Discipline

For the cleanest operator experience, treat these as required:

- workflow profiles should own repeatable build, test, release, deploy, rollback, and verification commands
- checks should be created for meaningful validation instead of leaving proof only in mission logs; eligible non-deployment Pending gates are auto-run, while deployment gates are resolved through deployment actions
- releases should link to the work and checks that justify them
- deployments should target named environments rather than ad-hoc host notes or mission prose
- verification should be captured on the deployment record
- rollback should be performed through deployment detail when possible
- incidents should be created from the affected deployment or environment so context is not retyped and lost
- runbooks should be used for repeated release, deploy, rollback, migration, or incident steps

Mission and voyage descriptions should stay narrow: they identify the worker task, file boundaries, and related structured IDs. Scope belongs in objectives/backlog, commands in workflow profiles, proof in checks, rollout state in deployments, operational failure history in incidents, and repeated procedure history in runbook executions.

## Current Limitations

- No provider-backed PR evidence is attached to releases yet.
- No external CI provider deployment evidence is attached yet.
- Proxy delivery workflows remain bounded and operator-focused rather than full dashboard parity. They currently expose releases, environments, deployments, incidents, runbooks, and runbook executions through the tunnel shell, but they do not attempt full administrative coverage or secret editing.
