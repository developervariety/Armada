# Proxy Expansion Plan

> Superseded by [CONSISTENT_DASHBOARD.md](CONSISTENT_DASHBOARD.md) for long-term direction as of `2026-05-16`.
> Keep this file only as historical context for the retired bounded-shell expansion approach.

## Status Snapshot

As of `2026-05-15`, `Armada.Proxy` now exposes the planned Phase 0-4 remote-operations surface: playbooks, backlog/refinement/planning, delivery workflows, captain/runtime diagnostics, request-history and bounded API exploration, plus read-only reference views for workspace, pipelines, personas, and prompt templates. The remaining open work is primarily verification and UX hardening rather than missing feature families: route-level proxy coverage, end-to-end shell smoke coverage, and explicit mobile/tablet/desktop validation still need to be closed out.

- [x] Proxy documentation exactly matches shipped proxy behavior, with no stale playbook or route claims.
- [x] Proxy UI selectors always prefer live remote data over shell-local assumptions, especially for pipelines and persona-backed workflows.
- [x] Proxy restores real playbook parity for remote dispatch flows.
- [x] Proxy supports remote backlog/objective intake, refinement, and lineage-aware promotion.
- [x] Proxy supports remote planning-session creation, monitoring, and dispatch handoff.
- [x] Proxy supports remote delivery orchestration with workflow profiles, checks, environments, releases, deployments, incidents, and runbooks.
- [x] Proxy exposes captain tool inventory and MCP source visibility for remote diagnostics.
- [x] Proxy exposes request-history and API-explorer diagnostics appropriate for remote operations.
- [x] Proxy planning explicitly tracks mobile, tablet, and desktop UX validation for every new or materially changed remote shell surface.
- [x] Proxy explicitly documents and preserves non-goals such as setup wizard, secret editing, duplicate actions, and full workspace editing.
- [~] Proxy has meaningful automated verification for server routes, tunnel forwarding, and core shell workflows.

## How To Maintain This File

- Use `[ ]` for not started, `[~]` for in progress, and `[x]` for complete.
- Update the status snapshot, locked decisions, capability matrix, workstreams, file map, and verification sections when implementation reality changes.
- This plan assumes proxy expansion begins after `v0.8.0`. If another release lands first, update version references rather than leaving stale assumptions.
- If a capability is intentionally deferred, keep the rationale current instead of silently removing it from the plan.
- If a capability ships partially, mark the exact completed tasks and leave the remaining tasks open rather than converting the whole workstream to complete.

## Product Goal

Armada.Proxy should become the bounded remote-operations companion to Armada:

1. connect to a remote Armada deployment through the tunnel
2. inspect the current operational state of that deployment
3. capture and refine future work remotely when needed
4. plan and dispatch work without requiring local-dashboard access
5. observe delivery workflows and follow-through after dispatch
6. diagnose captain/runtime/tool availability issues from the remote shell

The goal is not full dashboard parity. The goal is high-value remote orchestration parity.

## Proxy Role And Guardrails

- Proxy remains a bounded remote shell, not a full clone of the React dashboard.
- Proxy should prioritize remote orchestration, delivery, and diagnostics over authoring-heavy or repo-heavy experiences.
- Proxy should avoid direct editing of sensitive credentials or secret-bearing fields until the proxy auth model is materially stronger.
- Proxy should prefer auditable, reversible, and confirmation-gated actions for destructive operations.
- Proxy should favor compact list/detail/modals and workflow handoffs over sprawling administrative configuration screens.
- Proxy should be validated explicitly on mobile, tablet, and desktop for spacing, padding, typography, color contrast, overflow, touch targets, and modal/table behavior before a workstream is considered complete.
- Proxy should not require a UI rewrite as part of the first expansion pass; extend the existing shell unless it becomes the clear blocker.

## Current State

### What The Proxy Actually Does Today

- Proxy serves a remote shell for deployment selection, summary, activity, missions, voyages, captains, fleets, vessels, dispatch, playbooks, backlog, planning, delivery, diagnostics, and reference views.
- Proxy now also serves backlog, planning, delivery, diagnostics, and read-only reference sections through the same bounded shell.
- Proxy forwards bounded management actions for fleets, vessels, voyages, missions, captains, playbooks, backlog/objectives, refinement sessions, planning sessions, workflow profiles, checks, environments, releases, deployments, incidents, runbooks, and runbook executions.
- Proxy shell exposes captain-tool inspection, request-history inspection, and a bounded instance-scoped API Explorer with safe GET presets.
- Proxy shell trusts live remote pipeline data instead of relying on shell-local built-in pipeline assumptions and now exposes read-only pipeline/persona/prompt-template reference views.

### Known Gaps And Drift

- Proxy automated coverage is still thin relative to the widened surface and does not yet provide route-level confidence across the full proxy endpoint family.
- Manual responsive UX validation is still open across mobile, tablet, and desktop for the newly expanded shell.
- The proxy auth model is still intentionally basic: shared-password login plus client confirmation prompts rather than per-user authorization.

## Non-Goals For This Plan

- Full local-dashboard parity in the proxy
- Setup wizard or first-run onboarding inside the proxy
- Tenant, user, or credential administration through the proxy
- Editing secret-bearing fields such as vessel GitHub token overrides
- Factory reset, local install, Windows script, or local deployment management workflows
- Full workspace browsing/editing parity with the main dashboard
- Duplicate actions for configuration records
- Prompt-template or persona authoring as a first-class proxy workflow
- Notification inboxes or broad SaaS-style account management

## Locked Decisions

- [x] Proxy should stay a bounded remote-operations shell, not a second full dashboard.
- [x] Proxy documentation drift is a release blocker for proxy work.
- [x] Restoring real playbook parity is a foundation task because the docs already claim it.
- [x] Backlog/objective and planning support are the highest-value missing orchestration surfaces.
- [x] Delivery workflows belong in proxy after work-intake and planning parity land.
- [x] Captain tool inventory and MCP source visibility belong in proxy as a diagnostics surface.
- [x] Request history and API explorer belong in proxy later than core orchestration and delivery parity.
- [x] Workspace support, if added, should begin as read-only or deep-link oriented rather than full remote editing.
- [x] Pipelines should be treated as live remote data, not as shell-local constants.
- [x] Persona and prompt-template surfaces should start as reference/selectors before any authoring is considered.
- [x] GitHub token override editing remains out of scope for proxy until the auth model is stronger.
- [x] Setup wizard, duplicate actions, and factory reset remain out of scope for proxy.
- [x] Every new or materially changed proxy view, modal, form, table, and detail surface must receive an explicit mobile/tablet/desktop UX pass before that workstream can be closed.
- [x] No proxy UI rewrite is required for the first expansion pass unless the existing shell becomes the clear implementation blocker.

## Capability Decision Matrix

| Capability | Core Activity In Last Month | Proxy Decision | Target Phase | Why |
| --- | --- | --- | --- | --- |
| Setup wizard and onboarding | Expanded | Do not add | Deferred | Proxy is not the first-run local onboarding surface |
| Playbooks | Added earlier, docs still claim proxy support | Add | Phase 0 | Directly impacts remote dispatch workflows and current docs are stale |
| Planning sessions and planning handoff | Added and iterated heavily | Add | Phase 1 | High-value remote orchestration surface |
| Backlog/objectives/refinement/lineage | Added heavily | Add | Phase 1 | Remote work intake and promotion are core proxy use cases |
| Workflow profiles | Added | Add | Phase 2 | Needed for delivery governance and dispatch context |
| Structured checks and review gates | Added | Add | Phase 2 | Needed to observe and control delivery readiness |
| Environments | Added | Add | Phase 2 | Natural remote-operations surface |
| Releases | Expanded | Add | Phase 2 | Remote release orchestration fits proxy well |
| Deployments | Expanded | Add | Phase 2 | Core remote-operations workflow |
| Incidents | Expanded | Add | Phase 2 | Core remote-operations workflow |
| Runbooks | Expanded | Add | Phase 2 | Strong fit for remote response workflows |
| Captain tool inventory and MCP visibility | Added heavily | Add | Phase 3 | High-value diagnostics for remote operators |
| Request history | Added | Add | Phase 3 | Strong diagnostics and audit fit |
| API Explorer | Added | Add in bounded form | Phase 3 | Useful diagnostics, but lower priority than orchestration and delivery |
| Workspace | Added | Add read-only or deep-link only | Phase 4 | Repo-heavy surface; useful, but not first-wave proxy material |
| Personas | Expanded | Add read-only/reference only | Phase 4 | Useful for understanding dispatch context, not priority authoring |
| Prompt templates | Expanded | Add read-only/reference only | Phase 4 | Useful for understanding behavior, not priority authoring |
| Pipelines | Expanded indirectly through personas | Strengthen existing support | Phase 0 and Phase 4 | Must stop drifting from live remote data |
| Product Manager and Usability Engineer built-ins | Added | Support indirectly | Phase 0 and Phase 4 | Ride on live pipeline/persona data; no special proxy-only flow required |
| Mux captain integration | Added | Support indirectly | Phase 3 | Covered by captain/runtime diagnostics rather than a dedicated proxy feature |
| Vessel GitHub token overrides | Added | Do not add editing | Deferred | Secret-bearing field under weak auth model |
| Duplicate actions | Added in dashboard | Do not add | Deferred | Low value compared to higher-priority remote flows |
| Dashboard i18n | Added | Consider later | Phase 4 | Nice to have, not a blocker for remote-ops parity |
| Factory reset and local deployment script work | Added | Do not add | Deferred | Local-install concern, not a remote proxy concern |

## Workstreams

### Cross-Cutting UX Validation And Responsive Design

### Scope

Establish explicit UX-validation work that must be completed alongside every proxy feature phase so the shell remains usable and coherent across mobile, tablet, and desktop.

### Viewport Targets

- Mobile: `390x844`
- Tablet: `768x1024`
- Desktop: `1440x900`

### Tasks

- [x] Define the standard proxy viewport matrix and use it consistently for manual smoke and any future visual-validation automation.
- [ ] For each new or materially changed proxy view, verify layout at mobile, tablet, and desktop widths before marking that feature complete.
- [ ] Verify page-level spacing, section spacing, card padding, and modal padding at all three target sizes.
- [ ] Verify typography scale, line length, line height, and hierarchy at all three target sizes.
- [ ] Verify color usage, contrast, tag/badge readability, and focus/hover/active affordances at all three target sizes.
- [ ] Verify button sizes, touch targets, form controls, and tap spacing for mobile and tablet usage.
- [ ] Verify modal sizing, modal scrolling, table overflow behavior, sticky actions, and close affordances at all three target sizes.
- [ ] Verify long IDs, URLs, logs, tables, and empty states do not break layout or create unreadable wrapping.
- [ ] Verify list/detail transitions, action menus, and confirmation prompts remain clear and usable on mobile and tablet.
- [ ] Verify proxy navigation remains understandable and reachable on mobile, tablet, and desktop as new sections are added.
- [ ] Verify remote error states, empty states, loading states, and unavailable/offline states remain well-spaced and readable at all three target sizes.
- [ ] Capture and fix responsive issues as part of the active workstream rather than deferring them to a later polish pass.

### Exit Criteria

- [ ] No workstream closes without explicit mobile, tablet, and desktop UX validation for all user-facing changes shipped in that workstream.
- [ ] Proxy surfaces do not rely on desktop-only layouts or unreadable dense tables for critical workflows.

### Phase 0: Foundation, Drift Removal, And Playbook Parity

### Scope

Close current proxy drift before adding more surface area. Restore any already-documented remote capabilities that are missing in the actual shipped proxy.

### Tasks

- [x] Audit `docs/PROXY_API.md`, `src/Armada.Proxy`, and the current tunnel capability list for mismatches.
- [x] Decide whether playbooks land immediately in proxy or are temporarily removed from docs until implemented.
- [x] If playbooks are restored immediately, add proxy REST routes for playbook list/detail/create/update/delete.
- [x] If playbooks are restored immediately, add tunnel methods for playbook list/detail/create/update/delete through `RemoteControlManagementService`.
- [x] Add playbook listing and detail support in the proxy shell.
- [x] Add playbook create/edit/delete support in the proxy shell.
- [x] Add playbook selection during proxy dispatch flows.
- [x] Remove shell-local pipeline drift by replacing hard-coded built-in pipeline assumptions with live `/pipelines` data.
- [x] Keep only a clearly-labeled fallback pipeline list for tunnel-failure scenarios, if needed.
- [x] Ensure proxy dispatch, fleet, and vessel forms surface live pipeline data even when new personas/pipeline stages land later.
- [x] Ensure mission editing and display reflect current persona names such as `Test Engineer`.
- [x] Update proxy docs to match the actual phase-0 proxy feature set.
- [ ] Add automated route-level coverage for all currently shipped proxy endpoints.
- [ ] Add automated shell smoke coverage for login, instance selection, summary load, fleet add/edit, vessel add/edit, mission browse, voyage browse, and dispatch.
- [ ] Validate the current proxy shell and any phase-0 changes at mobile, tablet, and desktop sizes, including dispatch, fleet, vessel, mission, and detail modals.

### Exit Criteria

- [x] There are no documented proxy capabilities missing from the shipped proxy server and shell.
- [x] Pipeline and persona display in proxy no longer depends on stale shell-local assumptions.
- [x] Playbook behavior is either truly shipped or explicitly removed from docs until shipped.

### Phase 1: Remote Work Intake, Backlog, Refinement, And Planning

### Scope

Bring the new future-work orchestration model into the proxy in a bounded but useful way: backlog/objectives, captain-backed refinement, planning sessions, and dispatch handoff.

### Tasks

#### Tunnel And Server Surface

- [x] Add proxy-facing query methods for backlog/objective list, detail, and lineage summary.
- [x] Add proxy-facing management methods for backlog/objective create and update.
- [x] Add proxy-facing query methods for objective-refinement-session list, detail, and latest summary state.
- [x] Add proxy-facing management methods for objective-refinement-session create, message, and apply/promote actions.
- [x] Add proxy-facing query methods for planning-session list, detail, transcript summary, and dispatch readiness.
- [x] Add proxy-facing management methods for planning-session create, message, cancel, and dispatch handoff.
- [x] Preserve lineage identifiers across objective -> refinement -> planning -> voyage -> mission where applicable.
- [x] Extend proxy REST routes under `/api/v1/instances/{instanceId}/...` for backlog/objectives, refinement, and planning.
- [ ] Update proxy OpenAPI metadata for the new routes.

#### Proxy Shell UI

- [x] Add `Backlog` to proxy navigation.
- [x] Add `Planning` to proxy navigation.
- [x] Add backlog list view with compact remote-friendly filters for status, backlog state, owner, fleet, vessel, and priority.
- [x] Add backlog detail modal showing lifecycle status, backlog state, lineage links, latest refinement status, and latest planning linkage.
- [x] Add backlog create/edit modal with the bounded field set appropriate for proxy use.
- [x] Add captain selection and `Start Refinement` action from backlog detail.
- [x] Add refinement session status, summary, and transcript preview in proxy detail views.
- [x] Decide and implement whether proxy refinement is summary-first only or supports bounded transcript continuation.
- [x] Add `Promote To Planning` and `Dispatch From Objective` actions where the underlying state allows it.
- [x] Add planning-session list view with captain, vessel, linked objective, status, and timestamps.
- [x] Add planning-session detail modal with summary, transcript preview, selected captain, vessel, and dispatch-handoff actions.
- [x] Replace the modal-and-prompt planning flow with an inline transcript workspace that supports chat continuation, dispatch-message selection, draft summarization, and dispatch handoff directly in the proxy shell.
- [x] Auto-refresh responding planning sessions in the proxy so captain replies appear without requiring a manual reload.
- [x] Support creating planning sessions from backlog/objectives.
- [x] Support creating planning sessions from vessel context when no backlog record exists.

#### Security And UX Constraints

- [x] Gate state-changing actions with confirmation prompts where they can materially change execution state.
- [x] Keep proxy backlog/planning screens compact and modal-driven rather than attempting full-dashboard parity.
- [ ] Ensure proxy messages clearly distinguish between remote instance failure, tunnel failure, and validation failure.

#### Docs And Tests

- [x] Update `docs/PROXY_API.md` with backlog/objective, refinement, and planning routes.
- [x] Update any proxy examples in `README.md` or related docs if they mention remote shell scope.
- [x] Add unit coverage for new tunnel query and management handlers.
- [~] Add automated proxy flow coverage for backlog create/edit, refinement start, planning create, and dispatch handoff.
- [ ] Validate backlog, refinement, and planning views at mobile, tablet, and desktop sizes, including filters, transcript previews, forms, modals, and lineage summaries.

### Exit Criteria

- [x] A remote operator can capture future work, refine it with a captain, open planning, and hand off to dispatch entirely through the proxy.
- [x] Objective lineage remains visible and intact through proxy views.

### Phase 2: Remote Delivery Operations

### Scope

Expose the core delivery-and-follow-through surfaces added to the main platform: workflow profiles, checks, review gates, environments, releases, deployments, incidents, and runbooks.

### Tasks

#### Workflow Profiles, Checks, And Review Gates

- [x] Add proxy-facing query methods for workflow-profile list and detail.
- [x] Add proxy-facing management methods for workflow-profile create and update if the current REST surface supports safe bounded editing.
- [x] Add proxy-facing query methods for check-run list and detail.
- [x] Add proxy-facing management methods for safe check actions that exist in the core platform.
- [ ] Surface review-gate state in mission, voyage, release, and deployment detail when present.
- [x] Add workflow-profile selection or display in dispatch and delivery flows where relevant.

#### Environments

- [x] Add proxy-facing query methods for environment list and detail.
- [x] Add proxy-facing management methods for environment create and update if the current REST surface supports safe bounded editing.
- [x] Add environment linkage display in releases, deployments, and incidents.

#### Releases

- [x] Add proxy-facing query methods for release list and detail.
- [x] Add proxy-facing management methods for release create, update, refresh, and delete where supported by the core platform.
- [x] Add release detail modal showing linked objectives, voyages, checks, deployments, and evidence.

#### Deployments

- [x] Add proxy-facing query methods for deployment list and detail.
- [x] Add proxy-facing management methods for deployment create and core bounded state transitions supported by the platform.
- [x] Add deployment detail modal showing environment, release, status, approvals, verification, rollback data, and linked incident context.

#### Incidents

- [x] Add proxy-facing query methods for incident list and detail.
- [x] Add proxy-facing management methods for incident create and bounded state changes supported by the platform.
- [x] Add incident detail modal showing linked deployment, environment, release, and remediation context.

#### Runbooks

- [x] Add proxy-facing query methods for runbook list and detail.
- [x] Add proxy-facing management methods for bounded runbook operations supported by the platform.
- [x] Add runbook detail and execution-launch flows appropriate for proxy operators.

#### Proxy Shell Information Architecture

- [x] Add a `Delivery` navigation grouping or equivalent compact sections.
- [x] Keep delivery views compact and operator-focused rather than mirroring full dashboard layouts.
- [x] Cross-link missions, voyages, backlog items, releases, deployments, incidents, and runbooks where lineage exists.

#### Docs And Tests

- [x] Update `docs/PROXY_API.md` for workflow, delivery, and incident surfaces.
- [ ] Update Postman collections if proxy endpoints are tracked there.
- [x] Add server-side tests for workflow, check, environment, release, deployment, incident, and runbook tunnel handlers.
- [~] Add automated proxy flow coverage for at least one end-to-end release/deployment/incident scenario.
- [ ] Validate workflow, delivery, release, deployment, incident, and runbook surfaces at mobile, tablet, and desktop sizes, including tables, detail modals, action clusters, and status badges.

### Exit Criteria

- [x] A remote operator can observe and manage delivery state from the proxy without needing the full dashboard for common workflows.
- [x] Delivery surfaces in proxy preserve objective/work lineage where the core platform already supports it.

### Phase 3: Captain Diagnostics, Request History, And API Diagnostics

### Scope

Bring the new captain-tool visibility and diagnostic surfaces into the proxy after core orchestration and delivery parity are underway.

### Tasks

#### Captain Tool Inventory

- [x] Add proxy-facing query method for captain tool inventory and runtime source metadata.
- [x] Extend proxy REST routes for captain tool inspection.
- [x] Add captain detail modal actions to view available tools.
- [x] In that view, show configured MCP servers, runtime-managed sources, runtime built-ins, and MCP tool inventory where available.
- [x] Distinguish `reachable`, `unreachable at query time`, `not configured`, and `runtime unsupported`.
- [x] Preserve server/source attribution so operators can tell which MCP server exposed a given tool.
- [x] Ensure the proxy uses the same point-in-time wording around temporarily offline MCP servers as the main dashboard.

#### Request History

- [x] Add proxy-facing query methods for request-history list and detail.
- [x] Add proxy REST routes for request-history views.
- [x] Add a compact `Requests` diagnostics section in the proxy shell.
- [x] Add filtering for operation, status, time, and linked entity where supported by the core platform.

#### API Explorer

- [x] Decide whether proxy API explorer should target proxy endpoints, remote-instance endpoints, or both.
- [x] Add a bounded API-explorer mode that is safe under the current proxy auth model.
- [x] Seed explorer affordances with safe common GET routes before allowing broad arbitrary execution.
- [x] Clearly label whether an execution is hitting the proxy itself or a tunneled Armada instance.

#### Docs And Tests

- [x] Update `docs/PROXY_API.md` for captain-tools, request-history, and API-explorer support.
- [x] Add tests for captain tool inventory forwarding and failure-state handling.
- [ ] Add tests for request-history and API-explorer proxy routes.
- [ ] Validate captain-tools, request-history, and API-explorer proxy UX at mobile, tablet, and desktop sizes, including dense tables, source metadata, modals, and error states.

### Exit Criteria

- [x] A remote operator can diagnose captain tool availability and request history through the proxy.
- [x] Proxy diagnostic messaging clearly separates runtime limits from temporary MCP reachability issues.

### Phase 4: Optional Read-Only Parity And Quality-Of-Life Surfaces

### Scope

Add lower-priority read-only or reference surfaces that improve remote understanding without pushing proxy into full-dashboard parity.

### Tasks

#### Workspace

- [x] Decide whether proxy workspace support is deep-link only, read-only summary only, or a minimal read-only browser.
- [x] If shipped, expose vessel workspace context such as current dock, branch, and working directory state without full remote file editing.

#### Pipelines, Personas, And Prompts

- [x] Add pipeline list/detail reference views if the existing selector-only model is insufficient.
- [x] Add persona list/detail reference views showing runtime, purpose, and linked backing prompt summary.
- [x] Add prompt-template list/detail reference views showing category, description, and content summary where appropriate.
- [x] Avoid full authoring unless the proxy auth model and UX expectations are revisited explicitly.

#### Internationalization

- [x] Re-evaluate whether the proxy shell should adopt the dashboard i18n model once primary proxy parity stabilizes.
- [x] Only take on proxy i18n if the shell is expected to remain a long-lived operator experience.

#### Architectural Re-evaluation

- [x] Re-evaluate whether the existing plain-JS shell remains the pragmatic implementation vehicle after phases 0 through 3.
- [x] If the shell becomes a sustained blocker, write a separate migration plan rather than folding a rewrite into ongoing feature delivery silently.

### Exit Criteria

- [x] Optional reference surfaces improve remote operator comprehension without materially bloating proxy scope or weakening guardrails.
- [ ] Optional reference surfaces are also validated at mobile, tablet, and desktop sizes before they are considered complete.

## Deferred Or Explicitly Out Of Scope

- [x] Setup wizard in proxy
- [x] Tenant, user, and credential administration in proxy
- [x] Editing vessel GitHub token overrides or other secret-bearing fields in proxy
- [x] Factory reset, Windows deployment scripts, framework override tools, or local environment bootstrap inside proxy
- [x] Duplicate actions across config records in proxy
- [x] Full workspace browser/editor parity in proxy
- [x] Prompt-template and persona authoring as first-wave proxy work
- [x] Full dashboard-like notifications and broad account-management workflows in proxy

## File Map

### Proxy Server And Shell

- `src/Armada.Proxy/ArmadaProxyServer.cs`
- `src/Armada.Proxy/Program.cs`
- `src/Armada.Proxy/Settings/ProxySettings.cs`
- `src/Armada.Proxy/Services/ProxyAuthService.cs`
- `src/Armada.Proxy/Services/InstanceRegistry.cs`
- `src/Armada.Proxy/Services/RemoteInstanceSession.cs`
- `src/Armada.Proxy/wwwroot/index.html`
- `src/Armada.Proxy/wwwroot/app.js`
- `src/Armada.Proxy/wwwroot/app.css`

### Core Server Tunnel Surface

- `src/Armada.Server/ArmadaServer.cs`
- `src/Armada.Server/RemoteControlManagementService.cs`
- `src/Armada.Server/RemoteControlQueryService.cs`
- `src/Armada.Server/RemoteControlOperationsService.cs`
- Any new request/response models needed under `src/Armada.Core/Models`

### Documentation And Packaging

- `docs/PROXY_API.md`
- `docs/TUNNEL_PROTOCOL.md`
- `docs/REMOTE_MGMT.md`
- `docs/TUNNEL_OPERATIONS.md`
- `docs/DELIVERY_OPERATIONS.md`
- `docs/REST_API.md` when proxy-visible remote routes need cross-reference
- `Armada.postman_collection.json` if proxy routes are tracked there
- `docker/compose-proxy.yaml`

### Tests

- Existing proxy-related tests such as `test/Armada.Test.Unit/Suites/Services/ProxyRegistryTests.cs`
- Existing remote-control tests such as:
  - `test/Armada.Test.Unit/Suites/Services/RemoteControlManagementServiceTests.cs`
  - `test/Armada.Test.Unit/Suites/Services/RemoteControlQueryServiceTests.cs`
- Additional automated coverage to be added for proxy routes and proxy shell workflows

## Verification Plan

### Foundation

- [x] Proxy project builds cleanly.
- [x] Proxy docs match actual routes and UI.
- [ ] Current login and deployment-selection flow still works after any route/UI additions.
- [x] The proxy shell UX validation checklist is defined and ready to be applied at mobile, tablet, and desktop sizes.

### Phase 0

- [x] Playbook routes, if claimed, are implemented and verified.
- [x] Proxy dispatch uses live pipeline data and no longer depends on stale hard-coded assumptions.
- [ ] Phase-0 proxy UI changes are validated at `390x844`, `768x1024`, and `1440x900`.

### Phase 1

- [~] Backlog create/edit/detail works through the proxy.
- [~] Refinement session start and status inspection work through the proxy.
- [~] Planning session create/detail/dispatch handoff works through the proxy.
- [ ] Phase-1 proxy UI changes are validated at `390x844`, `768x1024`, and `1440x900`.

### Phase 2

- [~] Workflow-profile and check visibility works through the proxy.
- [~] Environment, release, deployment, incident, and runbook views work through the proxy.
- [~] At least one lineage-rich delivery scenario is validated end to end.
- [ ] Phase-2 proxy UI changes are validated at `390x844`, `768x1024`, and `1440x900`.

### Phase 3

- [~] Captain tool inventory shows source attribution and runtime-state distinctions correctly.
- [~] Request-history routes and UI work through the proxy.
- [~] API-explorer behavior is bounded, labeled clearly, and verified against intended targets.
- [ ] Phase-3 proxy UI changes are validated at `390x844`, `768x1024`, and `1440x900`.

### Phase 4

- [~] Optional read-only/reference surfaces are verified without weakening proxy guardrails.
- [ ] Phase-4 proxy UI changes are validated at `390x844`, `768x1024`, and `1440x900`.

## Manual Smoke Checklist

- [ ] Browser login and deployment selection
- [ ] Proxy logout and session-expiry handling
- [ ] Mobile (`390x844`) review of every changed proxy surface, including menus, forms, modals, tables, and empty/error states
- [ ] Tablet (`768x1024`) review of every changed proxy surface, including menus, forms, modals, tables, and empty/error states
- [ ] Desktop (`1440x900`) review of every changed proxy surface, including menus, forms, modals, tables, and empty/error states
- [ ] Deployment summary, activity, missions, voyages, captains, fleets, and vessels
- [ ] Fleet create/update and vessel create/update
- [ ] Dispatch with current remote pipelines
- [ ] Playbook selection during dispatch if phase 0 ships it
- [ ] Backlog create/edit/detail
- [ ] Captain-backed backlog refinement
- [ ] Planning-session creation, monitoring, and dispatch handoff
- [ ] Workflow-profile and check visibility
- [ ] Environment, release, deployment, incident, and runbook navigation
- [ ] Captain tool inventory modal with reachable and unreachable MCP sources
- [ ] Request-history diagnostics
- [ ] API-explorer diagnostics

## Definition Of Done

- [x] Proxy docs, proxy routes, tunnel methods, and shell UI agree on shipped scope.
- [x] High-priority remote-operations capabilities from the last month of core-platform work are available through the proxy in bounded form.
- [x] Deferred capabilities remain explicitly documented as deferred or out of scope.
- [ ] Proxy verification covers both route-level forwarding and the highest-value shell workflows.
- [ ] Responsive UX validation has been performed and documented for mobile, tablet, and desktop on every shipped proxy surface changed by the work.
- [x] There is no known drift between live core workflows and what the proxy claims to support.
