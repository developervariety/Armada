# End-to-End Workflows Plan

Last updated: 2026-05-02

This document tracks the proposed expansion of Armada from a strong agent-orchestration platform into a fuller developer-lifecycle platform. It is intentionally actionable: work is grouped into concrete workflow areas, checklists are explicit, and developers should be able to annotate progress directly in this file.

This plan does **not** impose implementation priority. The workstreams are organized by lifecycle area and platform concern, not by sequence. Prioritization should be chosen later.

## Product Goal

Armada should support the complete path a developer or engineering team follows from incoming work to production verification:

- understand and define work
- prepare the affected repositories and environments
- plan and dispatch implementation
- validate changes locally and in CI
- review and land them safely
- release and deploy them
- verify production behavior
- retain the operational memory of what happened

Armada already has strong foundations for planning, dispatch, pipelines, Workspace, request history, API exploration, merge queueing, and remote operations. The goal of this plan is to identify the remaining workflows that should be integrated so Armada can become the operational and memory layer for software delivery, not just agent task execution.

## Guiding Principles

- Armada should orchestrate and connect delivery workflows, not blindly replace every external system.
- Armada should preserve durable memory about builds, tests, reviews, releases, deployments, incidents, and post-deploy outcomes.
- Armada should remain repository-aware and vessel-aware at every stage.
- Every integrated workflow should be queryable through dashboard, REST, and where appropriate MCP or WebSocket surfaces.
- Workflows should support both human-driven and agent-driven execution.
- Existing Armada concepts should be extended where possible instead of inventing disconnected new silos.
- Build, test, and deploy logic must be project-specific and configurable; Armada should not assume one universal toolchain.

## Current Armada Foundations

Armada already ships pieces of this lifecycle:

- [x] Planning sessions
- [x] Direct dispatch and multi-mission voyages
- [x] Persona- and pipeline-based orchestration
- [x] Vessel-aware Workspace for browsing and editing repositories
- [x] Request history and API Explorer
- [x] Merge queue
- [x] Playbooks for reusable guidance
- [x] Remote-control tunnel and proxy surfaces
- [x] Multi-runtime support including Mux
- [x] Queryable mission, voyage, log, diff, and event history

These existing foundations should be treated as anchor points for the workflows below rather than parallel systems to replace.

## Lifecycle Map

The full developer lifecycle Armada should eventually cover can be thought of as the following workflow chain:

1. Work intake
2. Scope definition
3. Repository and environment preparation
4. Planning
5. Implementation
6. Local validation
7. Code review and approval
8. CI validation
9. Landing and merge management
10. Release preparation
11. Deployment
12. Post-deploy verification
13. Incident response and rollback
14. Historical reporting and learning

The workstreams below are organized around this map.

## Recommended New Core Concepts

These are candidate first-class Armada concepts that would make the workflows below coherent.

### `Objective` or `Campaign`

A top-level cross-repository change record that can group multiple voyages, releases, environments, and deployments under one initiative.

- [ ] Decide whether the product term should be `Objective`, `Campaign`, or another name
- [ ] Define a durable data model with title, description, state, owner, tags, links, and acceptance criteria
- [ ] Allow an objective to reference multiple vessels and fleets
- [ ] Allow an objective to aggregate planning sessions, voyages, releases, deployments, and incidents

Acceptance criteria:

- A user can open one record and understand the full story of a multi-repo change
- Objective status can be derived from subordinate work without becoming lossy or misleading

### `WorkflowProfile`

A vessel-scoped or fleet-scoped definition of how a project builds, tests, packages, releases, and deploys.

- [ ] Define a project workflow profile model
- [ ] Support commands or scripts for lint, build, unit test, integration test, e2e, package, publish, smoke test, deploy, rollback, and health check
- [ ] Support environment-variable inputs and secret references without storing secrets inline
- [ ] Allow profiles to inherit from fleet defaults and override per vessel

Acceptance criteria:

- Armada can understand how a given repository actually ships without hard-coding one toolchain
- A vessel can expose build/test/deploy actions using its profile

### `CheckRun`

A structured record for any automated validation or execution step.

- [ ] Define a `CheckRun` model with type, status, started/completed times, logs, artifacts, summary, and links to the triggering entity
- [ ] Support check categories such as lint, build, unit test, integration test, e2e, migration, security scan, smoke test, deploy, and rollback
- [ ] Support both Armada-executed and externally-ingested checks

Acceptance criteria:

- Builds, tests, and post-deploy validations are represented consistently
- A mission, release, or deployment can show all relevant checks in one place

### `Environment`

A first-class deployment target such as `dev`, `staging`, `prod`, or named customer-hosted instances.

- [ ] Define an environment model with name, kind, configuration source, health endpoint, access notes, and deployment rules
- [ ] Support multiple environments per vessel or per objective
- [ ] Support environment-specific deploy and rollback commands through workflow profiles

Acceptance criteria:

- Armada can tell the difference between shipping to staging and shipping to production
- Deployments and incidents can be traced to environments explicitly

### `Release`

A named deliverable that groups versioning, notes, approvals, artifacts, and deployable outputs.

- [ ] Define a release model with version, changelog summary, artifact list, related PRs, related missions, and related objectives
- [ ] Support draft, candidate, shipped, failed, and rolled-back states

Acceptance criteria:

- A user can query what went into a release and what happened after it shipped

### `Deployment`

A first-class record for a single deployment execution.

- [ ] Define a deployment model with target environment, triggered artifacts or refs, status, logs, timings, approvals, and linked checks
- [ ] Support deploy, verify, rollback, and postmortem linkage

Acceptance criteria:

- Every rollout has a durable record and is not just buried in shell history or external CI logs

## Workstream A: Work Intake and Scope Definition

Armada should better support the workflow before planning starts: understanding what needs to be done and why.

### Capability Target

- [ ] Create intake records manually from the dashboard
- [ ] Import work from external systems such as GitHub Issues, GitHub PR comments, Jira, Linear, or service-desk sources
- [ ] Link intake items to one or more vessels
- [ ] Capture acceptance criteria, non-goals, rollout constraints, and evidence links
- [ ] Convert intake items into planning sessions, objectives, or voyages

### Recommended Product Surface

- `Operations > Intake` or `Operations > Objectives`
- inline links from Workspace, Request History, Events, and API Explorer into new intake/objective creation

### Concrete Work

- [ ] Define intake/objective data models in `src/Armada.Core/Models`
- [ ] Add persistence and enumeration support in database drivers
- [ ] Add REST CRUD routes and query/filter support
- [ ] Add dashboard pages for list, detail, create, edit, and link management
- [ ] Add import adapters or import stubs for external systems

Acceptance criteria:

- A user can capture incoming work without immediately dispatching it
- Scope and acceptance criteria remain queryable after implementation starts

## Workstream B: Repository Readiness and Onboarding

Armada should help answer whether a repository is actually ready for work, testing, release, and deployment.

### Capability Target

- [ ] Detect whether a vessel has a valid working directory, clean branch strategy, required toolchains, and deploy metadata
- [ ] Detect missing workflow profile commands and required secrets/config references
- [ ] Expose readiness warnings before planning, dispatch, or deployment
- [ ] Support a vessel setup checklist

### Concrete Work

- [ ] Extend vessel metadata and readiness models
- [ ] Add a vessel/workspace readiness service
- [ ] Add dashboard setup panels for repo readiness
- [ ] Add REST endpoints for readiness inspection

Acceptance criteria:

- Armada can clearly explain why a vessel is or is not ready for build, test, or deploy workflows

## Workstream C: Workflow Profiles for Build, Test, Release, and Deploy

This is the core abstraction that unlocks the rest of the lifecycle.

### Capability Target

- [ ] Let each vessel define how to run its build, test, release, deploy, verify, and rollback steps
- [ ] Support multiple named workflow profiles per vessel when needed
- [ ] Allow fleet defaults with vessel overrides
- [ ] Support profile validation and dry-run inspection

### Suggested Workflow Profile Fields

- name
- description
- default branch
- language/runtime hints
- lint command
- build command
- unit test command
- integration test command
- e2e test command
- package command
- publish artifact command
- release versioning command
- changelog generation command
- deploy commands per environment
- rollback commands per environment
- smoke test commands per environment
- required secrets/config references
- expected artifacts

### Concrete Work

- [ ] Add workflow profile models and persistence
- [ ] Add list/detail/edit dashboard flows
- [ ] Add validation service and preview output
- [ ] Add REST and MCP access where useful

Acceptance criteria:

- A developer can configure how a project is built and shipped without modifying Armada code

## Workstream D: Structured Build and Validation Checks

Armada should represent validation as first-class structured work, not just freeform logs inside missions.

### Capability Target

- [ ] Run build and test checks from Workspace, vessel detail, mission detail, release detail, and deployment detail
- [ ] Capture logs, durations, exit status, artifacts, parsed test counts, and coverage summaries
- [ ] Support retry and compare-last-run behavior
- [ ] Support per-check scope such as branch, commit, mission, voyage, or release

### Check Types Armada Should Support

- [ ] lint
- [ ] build
- [ ] unit test
- [ ] integration test
- [ ] e2e test
- [ ] package
- [ ] migration check
- [ ] security scan
- [ ] performance check
- [ ] smoke test
- [ ] deployment verification
- [ ] rollback verification

### Concrete Work

- [ ] Add `CheckRun` and related artifact models
- [ ] Add services to execute and persist check runs
- [ ] Add dashboard views for list, detail, logs, artifacts, and summaries
- [ ] Add REST endpoints for execution and retrieval
- [ ] Add hooks from missions, voyages, releases, deployments, and environments

Acceptance criteria:

- A developer can answer “what checks ran, where, on what code, and with what outcome?” without leaving Armada

## Workstream E: CI/CD Provider Integration

Armada should integrate with CI/CD providers rather than assume it must execute everything itself.

### Capability Target

- [ ] Ingest build/test/deploy status from GitHub Actions
- [ ] Ingest from other systems later such as Azure DevOps, Jenkins, Buildkite, GitLab CI, or CircleCI
- [ ] Link provider runs to missions, PRs, releases, and deployments
- [ ] Show provider artifacts and logs or deep links to them

### Concrete Work

- [ ] Define external run adapter interfaces
- [ ] Add provider connection configuration
- [ ] Add polling or webhook ingestion surfaces
- [ ] Normalize external runs into `CheckRun` and `Deployment` records
- [ ] Expose inbound webhook or scheduled sync infrastructure

Acceptance criteria:

- Armada can present a unified view whether a check ran inside Armada or in external CI

## Workstream F: Pull Request and Review Workflow

Armada already reaches branch creation and landing workflows. It should also model human and automated review more explicitly.

### Capability Target

- [ ] Create or update PRs as first-class linked records
- [ ] Capture PR status, approvals, review comments, change requests, and required checks
- [ ] Link review comments back to missions and Workspace files
- [ ] Support replaying review feedback into planning or follow-up dispatches
- [ ] Support “ship blocked by review” visibility

### Concrete Work

- [ ] Define a PR/review domain model or integration abstraction
- [ ] Extend merge-queue integration with richer review state
- [ ] Add review panels to mission, voyage, objective, and release pages
- [ ] Add comment ingestion or sync from provider APIs

Acceptance criteria:

- Armada can show whether work is ready to land and why or why not

## Workstream G: Landing, Branch, and Merge Strategy Workflows

Armada already has merge queue and branch cleanup. The remaining work is to make landing strategy a richer configurable workflow.

### Capability Target

- [ ] Let a vessel define landing policy by branch or environment
- [ ] Support protected-branch checks and merge requirements
- [ ] Support release branches and hotfix branches
- [ ] Surface “what will happen when this lands?” clearly before landing

### Concrete Work

- [ ] Extend vessel workflow and branch policy metadata
- [ ] Add landing previews and preflight validation
- [ ] Add merge-queue detail improvements
- [ ] Add support for release-branch aware strategies

Acceptance criteria:

- A developer can predict the landing behavior for a change before it is merged

## Workstream H: Release Management Workflow

Armada should support the workflow between “code landed” and “software shipped.”

### Capability Target

- [ ] Create draft releases from one or more objectives, voyages, or merged PRs
- [ ] Generate release notes from linked work and request human editing
- [ ] Support semantic versioning workflows where relevant
- [ ] Support changelog drafting and tagging
- [ ] Track publish/package/image artifacts

### Concrete Work

- [ ] Define release models and artifact references
- [ ] Add release drafting UI
- [ ] Add changelog and note generation services
- [ ] Add artifact publishing hooks via workflow profiles
- [ ] Add release detail pages with linked missions, checks, PRs, and deployments

Acceptance criteria:

- A user can see what is shipping in a release and what evidence exists that it is ready

## Workstream I: Deployment Workflow

Deployment should be a first-class operational workflow, not just an external step developers remember to do manually.

### Capability Target

- [ ] Deploy a branch, tag, release, or artifact to a named environment
- [ ] Require environment-specific approvals when configured
- [ ] Run pre-deploy validations and post-deploy smoke tests
- [ ] Record deploy logs, timings, and outcomes
- [ ] Support manual and automated rollbacks

### Concrete Work

- [ ] Add environment and deployment models
- [ ] Add deploy execution services using workflow profiles
- [ ] Add approval and confirmation surfaces
- [ ] Add dashboard pages for environment list, environment detail, deployment list, and deployment detail
- [ ] Add REST APIs for triggering and observing deployments

Acceptance criteria:

- Armada can answer who deployed what, where, when, how, and whether it worked

## Workstream J: Post-Deploy Verification and Observability Workflow

Deployment without verification is incomplete. Armada should capture evidence after rollout.

### Capability Target

- [ ] Run smoke tests after deploy
- [ ] Run targeted API checks through API Explorer or reusable API test definitions
- [ ] Use request-history and status telemetry as deployment evidence
- [ ] Track environment health after rollout windows
- [ ] Allow operator sign-off or automatic pass/fail rules

### Concrete Work

- [ ] Define reusable post-deploy verification definitions
- [ ] Integrate with request-history summaries and health endpoints
- [ ] Add deploy verification panels and timelines
- [ ] Add support for alerting when post-deploy checks regress

Acceptance criteria:

- A deployment is not just “done”; it is “verified” or “failed verification” with evidence

## Workstream K: Incident, Hotfix, and Rollback Workflow

Software delivery includes failure handling. Armada should support that operational loop explicitly.

### Capability Target

- [ ] Capture incidents tied to environments, objectives, releases, or deployments
- [ ] Trigger hotfix planning and dispatch flows from an incident
- [ ] Support rollback records and rollback verification
- [ ] Preserve postmortem notes and corrective actions

### Concrete Work

- [ ] Define incident and rollback models
- [ ] Add incident list/detail/create flows
- [ ] Add links from requests, deployments, and environments into incident creation
- [ ] Add hotfix templates and playbooks
- [ ] Add postmortem fields and action tracking

Acceptance criteria:

- A team can go from detected issue to hotfix to rollback or recovery without losing context

## Workstream L: Runbooks and Executable Playbooks

Playbooks already exist. They should expand from static guidance into reusable operational workflows.

### Capability Target

- [ ] Support parameterized playbooks/runbooks for release, deploy, rollback, migration, and incident response
- [ ] Allow a runbook to reference workflow profile commands and environment targets
- [ ] Allow operators to execute a runbook step-by-step inside Armada

### Concrete Work

- [ ] Define runbook metadata and parameter schema
- [ ] Extend playbook models or add a distinct runbook concept
- [ ] Add dashboard execution UX for step-by-step runbooks
- [ ] Add audit trail for who ran what and with what inputs

Acceptance criteria:

- Repeated operational procedures stop living only in markdown and become guided executable workflows

## Workstream M: Secrets, Configuration, and Environment Inputs

Armada should support secure references to operational inputs without becoming the secret store itself.

### Capability Target

- [ ] Reference secrets/config values by provider and key
- [ ] Validate that required inputs exist before running build/test/deploy workflows
- [ ] Support environment-scoped configuration references

### Concrete Work

- [ ] Add secret/config reference models
- [ ] Add provider abstractions for external secret stores
- [ ] Add preflight validation in workflow execution
- [ ] Add dashboard configuration UX that stores references, not raw secret values

Acceptance criteria:

- Armada can orchestrate delivery workflows that need secrets without inlining sensitive material in its own models

## Workstream N: Reporting, Audit, and Historical Memory

This is the area that best fits Armada’s core identity as a memory layer.

### Capability Target

- [ ] Show historical timelines that connect intake, planning, dispatch, checks, PRs, releases, deployments, incidents, and postmortems
- [ ] Allow filtering by vessel, objective, environment, release, user, or time range
- [ ] Support “show me everything that happened for this launch” and similar questions

### Concrete Work

- [ ] Add cross-entity timeline aggregation services
- [ ] Add dashboard reporting pages and saved views
- [ ] Add REST query surfaces for historical lifecycle views
- [ ] Add export or backup-friendly summaries where useful

Acceptance criteria:

- A user can reconstruct the full delivery story of a change from a single place

## Workstream O: Remote Operations and Multi-Host Delivery

Armada already has remote-control foundations. Delivery workflows should eventually respect that.

### Capability Target

- [ ] Trigger and inspect deploy/verify/rollback workflows through remote-managed Armada instances
- [ ] Show environment and deployment state across multiple remote instances
- [ ] Support remote operator workflows without requiring local shell access

### Concrete Work

- [ ] Extend remote-control APIs and proxy surfaces for lifecycle entities
- [ ] Add proxy-side list/detail pages for deployments, environments, incidents, and releases
- [ ] Preserve scoped permissions and bounded execution semantics

Acceptance criteria:

- A user can understand and operate distributed delivery workflows across remote Armada instances

## Dashboard Information Architecture Impact

These workflow areas will require rethinking the dashboard layout over time.

### Candidate Navigation Areas

- `Operations`
  - Planning
  - Dispatch
  - Objectives or Intake
  - Voyages
  - Missions
  - Merge Queue
- `Fleet`
  - Fleets
  - Workspace
  - Vessels
  - Captains
  - Docks
- `Delivery`
  - Checks
  - Releases
  - Deployments
  - Environments
- `Activity`
  - Requests
  - Signals
  - Events
  - Notifications
  - Incidents
- `System`
  - API Explorer
  - Personas
  - Pipelines
  - Templates
  - Playbooks
  - Runbooks
  - Server
  - Doctor
- `Security`
  - Tenants
  - Users
  - Credentials

Implementation notes:

- [ ] Avoid one-off nested exceptions in the nav
- [ ] Keep delivery workflows coherent and discoverable
- [ ] Ensure Workflow/Delivery surfaces feel first-class, not admin-only afterthoughts

## API Surface Impact

Armada will likely need new REST route families for:

- [ ] objectives or intake records
- [ ] workflow profiles
- [ ] check runs
- [ ] releases
- [ ] environments
- [ ] deployments
- [ ] incidents
- [ ] runbooks
- [ ] CI/CD provider integrations

General API requirements:

- [ ] OpenAPI metadata should remain complete enough to drive API Explorer well
- [ ] Request-history capture should continue to record these operational workflows cleanly
- [ ] Long-running operations should expose status polling and event hooks
- [ ] Multi-tenant and role-scoped behavior must be explicit

## MCP and WebSocket Considerations

Not every lifecycle workflow belongs in MCP or WebSocket, but some should.

### MCP candidates

- [ ] query objectives or intake records
- [ ] start or inspect checks
- [ ] create releases
- [ ] inspect deployments
- [ ] trigger well-bounded deploy or rollback workflows
- [ ] run guided runbooks

### WebSocket candidates

- [ ] live check-run updates
- [ ] deployment progress streaming
- [ ] incident and environment health events
- [ ] approval-needed notifications

Acceptance criteria:

- Interactive operational workflows do not require constant polling when real-time state matters

## Database and Persistence Considerations

The plan adds multiple first-class lifecycle entities. Persistence work should stay backend-neutral.

- [ ] Ensure SQLite, PostgreSQL, SQL Server, and MySQL support the new workflow entities
- [ ] Add versioned migrations for all new lifecycle concepts
- [ ] Define retention strategies for noisy entities like check logs and deployment logs
- [ ] Separate durable metadata from bulky artifacts where appropriate

Acceptance criteria:

- Lifecycle workflows are not SQLite-only unless that limitation is explicitly accepted for an MVP

## Testing Strategy

Each workstream should ship with both backend and dashboard coverage where applicable.

### Required test layers

- [ ] unit tests for new domain services
- [ ] database tests for persistence behavior
- [ ] automated REST tests for lifecycle routes
- [ ] dashboard Vitest coverage for key pages and interactions
- [ ] remote/proxy tests for remote lifecycle workflows where implemented

### Suggested verification themes

- [ ] build/test/deploy flows respect vessel workflow profiles
- [ ] role and tenant scoping works correctly
- [ ] artifacts and logs are queryable after execution
- [ ] linked timelines remain consistent across entities
- [ ] rollback and incident flows do not silently lose context

## Documentation Work

As these workflows ship, documentation will need to stay synchronized.

- [ ] README lifecycle overview
- [ ] REST API docs
- [ ] MCP API docs
- [ ] WebSocket API docs
- [ ] testing docs
- [ ] deployment and remote-management docs
- [ ] Postman collection
- [ ] new operator guides for release, deploy, rollback, and incident workflows

Acceptance criteria:

- A developer or operator can discover and use shipped lifecycle workflows without reading the source

## Definition of Done for This Plan

This plan should be considered fulfilled only when Armada can reasonably support the complete software-delivery loop across at least one realistic stack:

- [ ] work is captured and scoped
- [ ] repositories are prepared with workflow metadata
- [ ] planning and dispatch produce implementation work
- [ ] build/test checks are tracked as first-class records
- [ ] review and landing status are visible
- [ ] releases are assembled and documented
- [ ] deployments are executed against named environments
- [ ] post-deploy verification is captured
- [ ] rollback and incident handling are supported
- [ ] the full lifecycle is queryable historically

## Notes for Execution

- This file is intentionally non-prioritized.
- Developers should feel free to annotate sections with owners, dates, and implementation notes.
- If a workstream is intentionally deferred, mark it explicitly rather than deleting it.
- If a workstream is split into a separate dedicated plan later, leave a pointer here so the lifecycle map remains intact.
