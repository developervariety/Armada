# End-to-End Workflows Plan

Last updated: 2026-05-14

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
- [x] Configurable stage-level review gates with approve and deny flows
- [x] Workflow profiles for vessel- and fleet-aware build, test, release, deploy, and verification commands
- [x] Structured check runs with logs, artifacts, retry, parsed test and coverage summaries, and workflow-profile-backed execution
- [x] Pull-based GitHub objective import, GitHub Actions sync, and GitHub pull-request evidence with global and per-vessel token resolution
- [x] Vessel readiness and workflow-input preflight for Workspace, vessel detail, planning, dispatch, and checks
- [x] First-class release records with versions, tags, linked work, derived notes, and artifact aggregation
- [x] First-class deployment environments with startup default seeding from workflow profiles or fallback development records
- [x] First-class deployments with approvals, rollback, verification, request-history evidence, and linked checks
- [x] First-class incident records with hotfix handoff, rollback linkage, and postmortem fields
- [x] Playbook-backed executable runbooks with parameters, step tracking, execution history, and delivery-entity linkage
- [x] Vessel-aware Workspace for browsing and editing repositories
- [x] Request history and API Explorer
- [x] Cross-entity `Activity > History` timeline for operational memory across current Armada entities
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

## Shipped Backlog Workflow

Armada now ships a backlog-first workflow that covers the intake-to-dispatch path inside the product instead of treating intake as an external prerequisite.

Current shipped flow:

1. Capture a backlog item from the React dashboard, REST API, MCP, Helm, or Postman.
2. Set backlog metadata such as kind, category, priority, rank, backlog state, effort, target version, and linked vessels or fleets.
3. Start a captain-backed refinement session directly from the backlog item and explicitly choose the captain that will perform the refinement.
4. Keep the transcript, summarize a selected or latest assistant turn, and apply the summary back to the backlog item.
5. Start a repository-aware planning session once a vessel, captain, and optional pipeline are chosen.
6. Dispatch from the planning session while preserving the same backlog/objective linkage.
7. Draft releases, link deployments, attach incidents, and trace the lineage from `Activity > History`.

Important behavior:

- Refinement is intentionally lighter than planning and does not provision a dock or imply repository mutation by default.
- A vessel is optional for refinement but required before repository-aware planning or dispatch can begin.
- The internal domain record remains `Objective`, but user-facing surfaces prefer `Backlog`.
- Legacy `/api/v1/objectives/...` routes remain supported alongside `/api/v1/backlog/...` aliases.

Reference surfaces:

- Dashboard: `Backlog`, backlog detail, refinement transcript, planning handoff, release drafting, and `Activity > History`
- REST: `/api/v1/backlog`, `/api/v1/objectives/reorder`, `/api/v1/backlog/reorder`, `/api/v1/objective-refinement-sessions/...`
- MCP: backlog CRUD, reorder, refinement, planning handoff, and dispatch handoff tools
- Helm: `armada backlog list|show|create|update|delete|reorder`

## Recommended New Core Concepts

These are candidate first-class Armada concepts that would make the workflows below coherent.

### `Objective` or `Campaign`

A top-level cross-repository change record that can group multiple voyages, releases, environments, and deployments under one initiative.

- [x] Decide whether the product term should be `Objective`, `Campaign`, or another name
- [x] Define a durable data model with title, description, state, owner, tags, links, and acceptance criteria
- [x] Allow an objective to reference multiple vessels and fleets
- [x] Allow an objective to aggregate planning sessions, voyages, releases, deployments, and incidents

Acceptance criteria:

- [x] A user can open one record and understand the full story of a multi-repo change
- [x] Objective status can be derived from subordinate work without becoming lossy or misleading

### `WorkflowProfile`

A vessel-scoped or fleet-scoped definition of how a project builds, tests, packages, releases, and deploys.

- [x] Define a project workflow profile model
- [x] Support commands or scripts for lint, build, unit test, integration test, e2e, package, publish, smoke test, deploy, rollback, and health check
- [x] Support required secret/config references without storing secret values inline
- [x] Allow profiles to resolve from global or fleet defaults with vessel overrides

Acceptance criteria:

- [x] Armada can understand how a given repository actually ships without hard-coding one toolchain
- [x] A vessel can expose build/test/deploy actions using its profile

### `CheckRun`

A structured record for any automated validation or execution step.

- [x] Define a `CheckRun` model with type, status, started/completed times, logs, artifacts, summary, and links to the triggering entity
- [x] Support check categories such as lint, build, unit test, integration test, e2e, package, publish artifact, release versioning, changelog, smoke test, deploy, rollback, health check, and custom-command execution
- [x] Support both Armada-executed and externally-ingested checks

Acceptance criteria:

- [x] Builds, tests, and post-deploy validations are represented consistently for Armada-executed runs
- [x] A mission, release, or deployment can show all relevant checks in one place

### `Environment`

A first-class deployment target such as `dev`, `staging`, `prod`, or named customer-hosted instances.

- [x] Define an environment model with name, kind, configuration source, health endpoint, access notes, and deployment rules
- [x] Support multiple environments per vessel or per objective
- [x] Support environment-specific deploy and rollback commands through workflow profiles
- [x] Seed default environment records on system startup

Acceptance criteria:

- [x] Armada can tell the difference between shipping to staging and shipping to production
- [x] Deployments and incidents can be traced to environments explicitly

### `Release`

A named deliverable that groups versioning, notes, approvals, artifacts, and deployable outputs.

- [x] Define a release model with version, summary/notes, artifact list, related voyages, related missions, and related check runs
- [x] Support draft, candidate, shipped, failed, and rolled-back states

Acceptance criteria:

- [x] A user can query what went into a release
- [x] A user can query what happened after it shipped once deployments and environments exist

### `Deployment`

A first-class record for a single deployment execution.

- [x] Define a deployment model with target environment, triggered artifacts or refs, status, logs, timings, approvals, and linked checks
- [x] Support deploy, verify, rollback, and postmortem linkage

Acceptance criteria:

- [x] Every rollout has a durable record and is not just buried in shell history or external CI logs

## Workstream A: Work Intake and Scope Definition

Armada should better support the workflow before planning starts: understanding what needs to be done and why.

### Capability Target

- [x] Create intake records manually from the dashboard
- [x] Import work from GitHub Issues and GitHub-backed pull-request context into objectives
- [ ] Import work from Jira, Linear, service-desk sources, and additional providers
- [x] Link intake items to one or more vessels
- [x] Capture acceptance criteria, non-goals, rollout constraints, and evidence links
- [x] Convert intake items into planning sessions, objectives, or voyages

### Recommended Product Surface

- `Operations > Objectives`
- inline links from Workspace, Request History, Events, and API Explorer into new intake/objective creation

### Concrete Work

- [x] Define intake/objective data models in `src/Armada.Core/Models`
- [x] Add persistence and enumeration support in database drivers
- [x] Add REST CRUD routes and query/filter support
- [x] Add dashboard pages for list, detail, create, edit, and link management
- [x] Add objective handoff actions into Planning, Dispatch, and Release drafting with prefilled context and server-backed linkage
- [x] Add a GitHub import adapter for issue- and pull-request-backed objectives
- [ ] Add adapters or import stubs for additional external systems

Acceptance criteria:

- [x] A user can capture incoming work without immediately dispatching it
- [x] Scope and acceptance criteria remain queryable after implementation starts
- [x] A user can turn an objective into planning sessions, voyages, and draft releases without retyping the core work definition

## Workstream B: Repository Readiness and Onboarding

Armada should help answer whether a repository is actually ready for work, testing, release, and deployment.

### Already Shipped In This Area

- [x] `VesselReadinessService`, models, and REST inspection route ship for vessel-level and check-specific preflight
- [x] Readiness detects missing working directories, missing repository context, missing default branch, unresolved workflow profiles, invalid workflow profiles, missing required inputs, and missing command dependencies
- [x] Vessel detail, Workspace, Planning, Dispatch, and Checks surface readiness warnings before work or execution starts
- [x] Checks are blocked server-side when blocking readiness issues exist
- [x] The typed `ArmadaApiClient` now supports vessel readiness inspection
- [x] Workflow-profile-backed readiness can be evaluated with explicit profile, check type, and environment overrides

### Capability Target

- [x] Detect whether a vessel has a valid working directory, basic branch/default-branch context, and required local command dependencies
- [x] Detect whether a vessel has a clean branch strategy, richer toolchain probes, and deploy metadata
- [x] Detect missing workflow profile commands and required secrets/config references
- [x] Expose readiness warnings before planning, dispatch, or deployment
- [x] Support a vessel setup checklist

### Concrete Work

- [x] Extend vessel metadata and readiness models
- [x] Add a vessel/workspace readiness service
- [x] Add dashboard readiness panels for current workflow entry points
- [x] Add REST endpoints for readiness inspection
- [x] Add vessel setup checklist metadata to readiness results
- [x] Add branch-policy-aware readiness details, richer toolchain/version probes, and deployment metadata summaries
- [x] Add a fuller guided vessel setup checklist and onboarding flow

Acceptance criteria:

- [x] Armada can clearly explain why a vessel is or is not ready for current build, test, planning, dispatch, or ad-hoc check workflows
- [x] Armada can fully guide a new vessel from initial registration through deploy-ready onboarding

## Workstream C: Workflow Profiles for Build, Test, Release, and Deploy

This is the core abstraction that unlocks the rest of the lifecycle.

### Already Shipped In This Area

- [x] `WorkflowProfile` models, services, and persistence ship across SQLite, PostgreSQL, SQL Server, and MySQL
- [x] Profiles can be scoped globally, to fleets, or to vessels, with default-selection fallback from vessel to fleet to global
- [x] Profiles support named commands for lint, build, unit/integration/e2e test, package, publish artifact, release versioning, changelog, and environment-specific deploy/rollback/smoke/health flows
- [x] Profiles support typed, provider-aware, and environment-scoped secret/config reference lists and expected artifact declarations
- [x] Validation returns errors, warnings, and available check types before execution
- [x] Validation now returns resolved command previews for base and environment-scoped commands
- [x] Dashboard list/detail/edit flows ship under `Delivery > Workflow Profiles`
- [x] REST routes and `ArmadaApiClient` support create, read, update, delete, validate, resolve, enumerate, and vessel-targeted preview inspection
- [x] Workflow profiles are queryable through MCP enumeration
- [x] `Delivery > Checks` shows the resolved profile source, available check types, and full command preview matrix before a run starts

### Capability Target

- [x] Let each vessel define how to run its build, test, release, deploy, verify, and rollback steps
- [x] Support multiple named workflow profiles per vessel when needed
- [x] Allow fleet defaults with vessel overrides
- [x] Support profile validation and preview inspection
- [x] Support richer command-level dry-run inspection across environments and resolved fallbacks

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

- [x] Add workflow profile models and persistence
- [x] Add list/detail/edit dashboard flows
- [x] Add validation service and preview output
- [x] Add REST and MCP access where useful

### Follow-Up Remaining In This Area

- [x] Add richer preview output showing the fully resolved command set per target environment and scope fallback
- [x] Replace plain string secret/config references with first-class provider/key references for workflow-profile editing and validation
- [x] Connect workflow profiles into current release records
- [x] Connect workflow profiles into current deployment and environment records

The current workflow-profile surface now spans build, test, release, deployment, and environment-aware preview flows for the current internal-first lifecycle scope.

Acceptance criteria:

- [x] A developer can configure how a project is built and shipped without modifying Armada code

## Workstream D: Structured Build and Validation Checks

Armada should represent validation as first-class structured work, not just freeform logs inside missions.

### Already Shipped In This Area

- [x] `CheckRun` and `CheckRunArtifact` models, execution service, and persistence ship across SQLite, PostgreSQL, SQL Server, and MySQL
- [x] Checks resolve commands from workflow profiles and execute inside the vessel working directory
- [x] Checks capture status, timings, exit code, combined output, summaries, artifacts, branch metadata, commit metadata, and mission/voyage linkage
- [x] Checks parse structured test counts and coverage summaries from common runner output and coverage artifacts and surface them in `Delivery > Checks`
- [x] Check runs support retry by reusing the prior run context
- [x] Checks support source metadata and REST-backed import of external/provider-originated runs for unified viewing
- [x] Dashboard list/detail flows ship under `Delivery > Checks`
- [x] REST routes and `ArmadaApiClient` support execute, import, read, enumerate, retry, and delete
- [x] Workspace, vessel detail, and mission detail can launch check runs
- [x] Voyage detail can launch check runs with voyage/vessel prefill
- [x] Check runs are queryable through MCP enumeration

### Capability Target

- [x] Run build and test checks from Workspace, vessel detail, and mission detail
- [x] Run build and test checks from voyage detail
- [x] Run build and test checks from release detail
- [x] Run build and test checks from deployment detail
- [x] Capture logs, durations, exit status, and artifacts
- [x] Capture parsed test counts and coverage summaries
- [x] Support retry
- [x] Support compare-last-run behavior
- [x] Support per-check scope such as branch, commit, mission, or voyage
- [x] Support imported external/provider check runs in the current Checks list/detail experience
- [x] Support release-linked runs
- [x] Support deployment-linked runs

### Check Types Armada Should Support

- [x] lint
- [x] build
- [x] unit test
- [x] integration test
- [x] e2e test
- [x] package
- [x] publish artifact
- [x] release versioning
- [x] changelog
- [x] deploy
- [x] rollback
- [x] smoke test
- [x] health check
- [x] migration check
- [x] security scan
- [x] performance check
- [x] deployment verification
- [x] rollback verification

### Concrete Work

- [x] Add `CheckRun` and related artifact models
- [x] Add services to execute and persist check runs
- [x] Add dashboard views for list, detail, logs, artifacts, summaries, and parsed test/coverage data
- [x] Add REST endpoints for execution and retrieval
- [x] Add hooks from workspace, vessels, and missions
- [x] Add voyage-level launch hooks and imported-run source visibility
- [x] Add hooks from releases
- [x] Add hooks from deployments
- [x] Add direct check-launch hooks from environments where that UX makes sense

### Follow-Up Remaining In This Area

- [x] Expand structured parsing coverage to additional test runners and report formats as needed for current common cases
- [x] Add compare-last-run and regression-focused UX in the dashboard
- [x] Support externally-ingested CI/provider check runs alongside Armada-executed runs
- [x] Connect checks to current release records
- [x] Connect checks to current deployment records
- [x] Connect checks to current environment-oriented launch surfaces where that UX makes sense

The remaining unchecked items in this workstream are now limited to future deeper environment-record linkage rather than gaps in the current check-run execution, deployment linkage, release linkage, launch UX, and history surface.

Acceptance criteria:

- [x] A developer can answer "what checks ran, where, on what code, and with what outcome?" for current workflow-profile-backed runs without leaving Armada

## Workstream E: CI/CD Provider Integration

Armada should integrate with CI/CD providers rather than assume it must execute everything itself.

### Capability Target

- [x] Ingest build/test/deploy status from GitHub Actions
- [ ] Ingest from other systems later such as Azure DevOps, Jenkins, Buildkite, GitLab CI, or CircleCI
- [x] Link provider runs to deployments and current linked mission/voyage context
- [ ] Link provider runs to first-class PR and release records when those entities exist
- [x] Show provider deep links and imported provider output on synced runs
- [ ] Show provider-hosted artifacts and richer provider log surfaces where available

### Concrete Work

- [x] Add an initial pull-based GitHub Actions adapter surface
- [x] Add provider connection configuration with global `GitHubToken` plus per-vessel override fallback
- [x] Add on-demand pull-based sync surfaces
- [x] Normalize external runs into `CheckRun` and `Deployment` records
- [ ] Expose inbound webhook or scheduled sync infrastructure

Acceptance criteria:

- [x] Armada can present a unified current view for GitHub Actions runs alongside Armada-executed checks
- [ ] Armada can present the same unified view across additional CI providers

## Workstream F: Pull Request and Review Workflow

Armada already reaches branch creation and landing workflows. It should also model human and automated review more explicitly.

### Already Shipped In This Area

- [x] Pipeline stages can require explicit review before the next stage or landing may continue
- [x] Review decisions support approve and deny flows
- [x] Denied review can either retry the same stage with reviewer feedback or fail the pipeline and cancel downstream stages
- [x] Review-gated missions are exposed in the dashboard and REST API

The remaining work in this section is about expanding beyond stage-level mission review into richer PR-aware, provider-aware, and release-aware review workflows.

### Capability Target

- [ ] Create or update PRs as first-class linked records
- [x] Capture GitHub PR status, approvals, review comments, change requests, and required checks as linked evidence
- [ ] Link review comments back to missions and Workspace files
- [ ] Support replaying review feedback into planning or follow-up dispatches

### Concrete Work

- [x] Define an initial GitHub-backed PR/review integration abstraction and normalized detail model
- [ ] Extend merge-queue integration with richer review state
- [x] Add review panels to mission and release pages
- [ ] Add review panels to voyage and objective pages
- [x] Add comment ingestion or sync from GitHub APIs
- [ ] Add comment ingestion or sync from additional provider APIs

Acceptance criteria:

- Armada can show whether work is ready to land and why or why not

## Workstream G: Landing, Branch, and Merge Strategy Workflows

Armada already has merge queue and branch cleanup. The remaining work is to make landing strategy a richer configurable workflow.

### Already Shipped In This Area

- [x] `LandingPreviewService`, models, REST routes, and typed API-client support ship for vessel- and mission-level landing previews
- [x] Landing preview surfaces ship in vessel detail and mission detail
- [x] Merge-queue detail now shows landing preview context for the queued branch and target branch
- [x] Vessel metadata now includes `RequirePassingChecksToLand`
- [x] Vessel metadata now includes protected-branch patterns, release-branch prefixes, hotfix-branch prefixes, and protected-branch merge requirements
- [x] Landing previews show source branch, target branch, branch category, landing mode, branch cleanup policy, latest structured check, and pass/fail gating state
- [x] Landing previews flag current blockers such as missing source branch, source-target collisions, manual landing mode, missing passing checks, protected-branch requirements, and missions not yet in a landing-ready state

### Capability Target

- [x] Let a vessel define landing policy by branch for current Armada-managed landing flows
- [x] Support protected-branch checks and merge requirements
- [x] Support release branches and hotfix branches

### Concrete Work

- [x] Extend vessel workflow and branch policy metadata
- [x] Add landing previews and preflight validation
- [x] Add merge-queue detail improvements
- [x] Add support for release-branch aware strategies

Acceptance criteria:

- [x] A developer can predict the current supported landing behavior and blockers for a change before it is merged
- [x] A developer can predict Armada-managed release-branch strategy and protected-branch constraints before it is merged
- [ ] A developer can predict provider-specific merge behavior before it is merged

## Workstream H: Release Management Workflow

### Already Shipped In This Area

- [x] `Release`, `ReleaseArtifact`, and `ReleaseQuery` models ship across SQLite, PostgreSQL, SQL Server, and MySQL
- [x] `ReleaseService`, REST routes, and typed `ArmadaApiClient` support create, read, update, refresh, delete, and enumerate flows
- [x] Release drafting can infer vessel scope from linked voyages, missions, and check runs
- [x] Release drafting derives mission scope from linked voyages, aggregates linked check-run artifacts, derives summary/notes from linked work, and auto-defaults tags
- [x] Release drafting supports semantic version extraction from linked `ReleaseVersioning` checks and patch-version bumping from prior releases when no explicit version is supplied
- [x] `Delivery > Releases` ships as a first-class dashboard list/detail surface
- [x] Voyages and checks can draft releases directly from existing work
- [x] Objectives can now launch draft-release creation with server-backed release linkage
- [x] Release detail can surface linked GitHub pull-request evidence derived from related mission PR URLs


### Capability Target

- [x] Create draft releases from one or more objectives
- [ ] Create draft releases from merged PRs once those entities exist
- [x] Create draft releases from one or more voyages and linked checks
- [x] Generate release notes from linked work and request human editing
- [x] Support semantic versioning workflows where relevant
- [x] Support tagging and editable changelog-style release notes for current internal release records
- [x] Track publish/package/image artifacts

### Concrete Work

- [x] Define release models and artifact references
- [x] Add release drafting UI
- [x] Add note and summary derivation services
- [x] Add artifact-linkage hooks via workflow profiles and structured checks
- [x] Add release detail pages with linked voyages, missions, and checks
- [x] Add linked deployment views for current internal release records
- [x] Add linked GitHub pull-request evidence views for current mission-linked PR URLs
- [ ] Add linked first-class PR views once those entities exist

Acceptance criteria:

- [x] A user can see what is shipping in a release and what evidence exists that it is ready for the current internal release-management scope
- [x] A user can see linked deployment evidence for current internal release records
- [ ] A user can see linked PR evidence once those entities exist

## Workstream I: Deployment Workflow

Deployment should be a first-class operational workflow, not just an external step developers remember to do manually.

### Capability Target

- [x] Deploy a branch, tag, release, or artifact to a named environment
- [x] Require environment-specific approvals when configured
- [x] Run pre-deploy validations and post-deploy smoke tests
- [x] Record deploy logs, timings, and outcomes
- [x] Support manual and automated rollbacks

### Concrete Work

- [x] Add environment and deployment models
- [x] Add deploy execution services using workflow profiles
- [x] Add approval and confirmation surfaces
- [x] Add dashboard pages for environment list and environment detail
- [x] Add dashboard pages for deployment list and deployment detail
- [x] Add REST APIs for triggering and observing deployments
- [x] Seed default environment records on system startup so deployments always have a usable default target

Acceptance criteria:

- [x] Armada can answer who deployed what, where, when, how, and whether it worked

## Workstream J: Post-Deploy Verification and Observability Workflow

Deployment without verification is incomplete. Armada should capture evidence after rollout.

### Capability Target

- [x] Run smoke tests after deploy
- [x] Run targeted API checks through reusable API test definitions
- [x] Use request-history and status telemetry as deployment evidence
- [x] Track environment health after rollout windows
- [x] Allow operator sign-off or automatic pass/fail rules

### Concrete Work

- [x] Define reusable post-deploy verification definitions
- [x] Integrate with request-history summaries and health endpoints
- [x] Add deploy verification panels and timelines
- [x] Add support for alerting when post-deploy checks regress

Acceptance criteria:

- [x] A deployment is not just "done"; it is "verified" or "failed verification" with evidence
- [x] Rollout-window health and verification regressions remain queryable after the initial deployment completes

## Workstream K: Incident, Hotfix, and Rollback Workflow

Software delivery includes failure handling. Armada should support that operational loop explicitly.

### Capability Target

- [x] Capture incidents tied to current environments, releases, or deployments
- [x] Trigger hotfix planning and dispatch flows from an incident
- [x] Support rollback records and rollback verification
- [x] Preserve postmortem notes and corrective actions

### Concrete Work

- [x] Define incident and rollback models
- [x] Add incident list/detail/create flows
- [x] Add links from deployments and environments into incident creation
- [x] Add hotfix prompts and runbook handoff from incident context
- [x] Add postmortem fields and action tracking

Acceptance criteria:

- [x] A team can go from detected issue to hotfix to rollback or recovery without losing context

## Workstream L: Runbooks and Executable Playbooks

Playbooks already exist. They should expand from static guidance into reusable operational workflows.

### Capability Target

- [x] Support parameterized playbooks/runbooks for release, deploy, rollback, migration, and incident response
- [x] Allow a runbook to reference workflow profile commands and environment targets
- [x] Allow operators to execute a runbook step-by-step inside Armada

### Concrete Work

- [x] Define runbook metadata and parameter schema
- [x] Extend playbook models or add a distinct runbook concept
- [x] Add dashboard execution UX for step-by-step runbooks
- [x] Add audit trail for who ran what and with what inputs

Acceptance criteria:

- [x] Repeated operational procedures stop living only in markdown and become guided executable workflows

## Workstream M: Secrets, Configuration, and Environment Inputs

Armada should support secure references to operational inputs without becoming the secret store itself.

### Already Shipped In This Area

- [x] Workflow profiles support typed required-input references for environment variables, file paths, and directory paths
- [x] Workflow profiles support provider-backed and environment-scoped required-input references
- [x] Dashboard workflow-profile editing stores input references instead of raw secret values
- [x] Readiness and structured check execution validate that required inputs exist before workflow-profile-backed checks run
- [x] Readiness results expose missing-input warnings or errors through REST, dashboard, and the typed API client

### Capability Target

- [x] Reference secrets/config values by provider and key
- [x] Validate that required inputs exist before running build/test/deploy workflows
- [x] Support environment-scoped configuration references

### Concrete Work

- [x] Add secret/config reference models
- [x] Add provider abstractions for external secret stores
- [x] Add preflight validation in workflow execution
- [x] Add dashboard configuration UX that stores references, not raw secret values
- [x] Add environment-scoped and provider-backed secret-store integrations for readiness and workflow preflight

Acceptance criteria:

- [x] Armada can orchestrate workflow-profile-backed delivery checks that need referenced inputs without inlining sensitive material in its own models
- [x] Armada can integrate with external secret stores and environment-specific configuration sources at the reference and preflight layer without leaking raw secret values

## Workstream N: Reporting, Audit, and Historical Memory

### Already Shipped In This Area

- [x] `HistoricalTimelineService`, models, REST routes, and typed client support ship for a unified cross-entity timeline
- [x] `Activity > History` ships as a first-class dashboard page with vessel, actor, text, and source-type filtering
- [x] Current timeline aggregation spans releases, deployments, environments, incidents, runbook executions, missions, voyages, planning sessions, merge entries, check runs, events, and request history
- [x] Timeline entries carry route links, metadata inspection, status/severity badges, and workspace shortcuts where vessel context exists
- [x] `Activity > History` now supports saved views plus JSON, CSV, and Markdown export for backup-friendly summaries

This is the area that best fits Armada’s core identity as a memory layer.

### Capability Target

- [x] Show historical timelines that connect current Armada entities such as releases, planning, dispatch, checks, merge queue, requests, and events
- [x] Allow filtering by vessel, user/actor, source type, free text, and time range
- [x] Allow filtering by objective and incident for current lifecycle history
- [x] Add postmortem-specific timeline filters where incident workflows need them
- [x] Allow filtering by environment and deployment for current lifecycle history
- [x] Support "show me everything that happened for this launch" and similar questions for the current release/deployment history surface

### Concrete Work

- [x] Add cross-entity timeline aggregation services
- [x] Add dashboard reporting/history page for current lifecycle entities
- [x] Add REST query surfaces for historical lifecycle views
- [x] Add saved views, export, and backup-friendly summaries where useful
- [x] Extend the timeline for current deployments and environments
- [x] Extend the timeline for incidents, runbook executions, and the current deployment lifecycle
- [x] Extend the timeline once objectives exist

The remaining unchecked items in this workstream are now limited to future entity expansion rather than missing objective, incident, deployment, environment, or postmortem-aware history support.

Acceptance criteria:

- [x] A user can reconstruct the current Armada-side story of a change from a single place
- [x] A user can reconstruct the current end-to-end Armada-side delivery story of a change, including release and deployment history, from a single place

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
  - Workflow Profiles
  - Checks
  - Environments
  - Releases
  - Deployments
- `Activity`
  - History
  - Requests
  - Signals
  - Events
  - Notifications
  - Incidents
- `System`
  - API Explorer
  - Personas
  - Pipelines
  - Prompts
  - Playbooks
  - Runbooks
  - Server
  - Doctor
- `Security`
  - Tenants
  - Users
  - Credentials

Implementation notes:

- [x] Avoid one-off nested exceptions in the nav
- [x] Keep delivery workflows coherent and discoverable
- [x] Workflow Profiles and Checks now ship as first-class Delivery surfaces, not admin-only afterthoughts
- [x] Releases now ship as a first-class Delivery surface, not an external afterthought
- [x] Environments now ship as a first-class Delivery surface for internal-first deployment metadata
- [x] Ensure future Workflow/Delivery surfaces continue to feel first-class as release/deploy entities arrive

## API Surface Impact

Armada will likely need new REST route families for:

- [x] objectives or intake records
- [x] workflow profiles
- [x] check runs
- [x] releases
- [x] environments
- [x] deployments
- [x] incidents
- [x] runbooks
- [ ] CI/CD provider integrations

General API requirements:

- [x] OpenAPI metadata now remains complete enough to drive API Explorer well for shipped workflow-profile, check-run, release, environment, deployment, incident, runbook, and history routes
- [x] Request-history capture now continues to record shipped operational workflows cleanly, including release, deployment, incident, runbook, and history routes
- [x] Current long-running operational workflows now expose status polling and event hooks through check-run detail routes plus WebSocket change events
- [x] Multi-tenant and role-scoped behavior is now explicit across shipped workflow-profile, check-run, release, environment, deployment, incident, runbook, and history surfaces

## MCP and WebSocket Considerations

Not every lifecycle workflow belongs in MCP or WebSocket, but some should.

### MCP candidates

- [x] query objectives or intake records
- [x] start or inspect checks
- [x] create releases
- [x] inspect deployments
- [x] trigger well-bounded deploy or rollback workflows
- [x] run guided runbooks

### WebSocket candidates

- [x] live check-run updates
- [x] deployment progress streaming
- [x] objective, deployment, and environment health events
- [ ] add incident lifecycle events if live incident streaming becomes necessary
- [x] approval-needed notifications

Acceptance criteria:

- Interactive operational workflows do not require constant polling when real-time state matters

## Database and Persistence Considerations

The plan adds multiple first-class lifecycle entities. Persistence work should stay backend-neutral.

- [x] Workflow profiles, check runs, and releases now ship across SQLite, PostgreSQL, SQL Server, and MySQL
- [x] Ensure later lifecycle entities keep the same backend-neutral coverage
- [x] Add versioned migrations for all new lifecycle concepts
- [ ] Define retention strategies for noisy entities like check logs and deployment logs
- [ ] Separate durable metadata from bulky artifacts where appropriate

Acceptance criteria:

- Lifecycle workflows are not SQLite-only unless that limitation is explicitly accepted for an MVP

## Testing Strategy

Each workstream should ship with both backend and dashboard coverage where applicable.

### Required test layers

- [x] Unit tests now cover workflow-profile validation/resolution, objective history, deployment execution, and release derivation/refresh flows
- [x] database tests for persistence behavior
- [x] Automated REST tests now cover objective, workflow-profile, check-run, release, environment, and deployment routes
- [x] dashboard Vitest coverage for key pages and interactions
- [ ] remote/proxy tests for remote lifecycle workflows where implemented

### Suggested verification themes

- [x] build/test flows respect vessel workflow profiles
- [x] role and tenant scoping works correctly for workflow profiles and check runs
- [x] artifacts and logs are queryable after execution for workflow-profile-backed check runs
- [x] linked timelines remain consistent across entities
- [x] rollback and incident flows do not silently lose context

## Documentation Work

As these workflows ship, documentation will need to stay synchronized.

- [x] README lifecycle overview
- [x] REST API docs
- [x] MCP API docs
- [x] WebSocket API docs
- [x] testing docs
- [x] deployment and remote-management docs
- [x] Postman collection
- [x] new operator guides for release, deploy, rollback, and incident workflows

Acceptance criteria:

- A developer or operator can discover and use shipped lifecycle workflows without reading the source

## Definition of Done for This Plan

This plan should be considered fulfilled only when Armada can reasonably support the complete software-delivery loop across at least one realistic stack:

- [x] work is captured and scoped for the current internal-first objective/intake surface
- [x] repositories are prepared with workflow metadata
- [x] planning and dispatch produce implementation work
- [x] build/test checks are tracked as first-class records
- [x] review and landing status are visible
- [x] releases are assembled and documented
- [x] deployments are executed against named environments
- [x] post-deploy verification is captured
- [x] rollback and incident handling are supported
- [x] the full lifecycle is queryable historically for the current Armada-side objective, release, deployment, incident, and runbook surface

## Notes for Execution

- This file is intentionally non-prioritized.
- Developers should feel free to annotate sections with owners, dates, and implementation notes.
- If a workstream is intentionally deferred, mark it explicitly rather than deleting it.
- If a workstream is split into a separate dedicated plan later, leave a pointer here so the lifecycle map remains intact.
