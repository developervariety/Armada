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

Armada does not yet support:

- provider-backed PR release evidence
- CI/CD provider-ingested deployment status
- remote proxy-driven deployment, verification, rollback, or incident execution

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

Use `Activity > History` and `Activity > Requests` when you need supporting evidence beyond the deployment record itself.

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

- releases should link to the work and checks that justify them
- deployments should target named environments rather than ad-hoc host notes
- verification should be captured on the deployment record
- rollback should be performed through deployment detail when possible
- incidents should be created from the affected deployment or environment so context is not retyped and lost

## Current Limitations

- No provider-backed PR evidence is attached to releases yet.
- No external CI provider deployment evidence is attached yet.
- Remote proxy workflows do not yet expose deployments, environments, incidents, releases, or runbooks as first-class remote-managed surfaces.
