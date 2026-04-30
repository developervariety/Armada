# Planning Session To Dispatch

## Status Snapshot

As of `2026-04-29`, the planning-to-dispatch product backlog for the current slice is complete.

- [x] React dashboard supports planning-session creation, transcript streaming, follow-up turns, stop, delete, summarize, open-in-dispatch, and direct dispatch.
- [x] Server supports planning-session persistence, routing, coordination, recovery, cleanup, summarize/delete flows, and voyage lineage.
- [x] Captains enter a dedicated `Planning` state and are blocked from normal mission assignment while planning is active.
- [x] Planning dispatch carries the selected pipeline and playbooks into normal Armada dispatch behavior.
- [x] Runtime support is explicitly gated to built-in planning-capable runtimes, with server-side enforcement and dashboard messaging.
- [x] Route-level automated tests exist for planning endpoints.
- [x] WebSocket payload regression tests exist for planning events.
- [x] Dashboard behavior tests exist for the extracted planning components and shared planning-page utilities.
- [x] An explicit `IInteractiveAgentRuntime` contract now exists for future persistent-session work, while the shipped v1 flow still uses transcript-backed relaunches.

## How To Maintain This File

- Use `[ ]` for not started, `[~]` for in progress, and `[x]` for complete.
- When implementation reality changes, update:
  - the status snapshot
  - the shipped behavior section
  - the workstream checklists
  - the verification section
- Keep future expansion ideas separate from the shipped-slice completion checklist.

## Product Goal

Armada should support the full path from:

1. plan with a captain in the dashboard
2. preserve that transcript as Armada-owned context
3. create a dispatch directly from the planning output

This removes the copy-paste seam between agent planning and Armada execution.

## Shipped Behavior

The shipped implementation is intentionally a `Planning` workflow, not a generic terminal emulator.

Actual behavior today:

- A planning session reserves one captain and provisions one dock and branch against one vessel.
- The session transcript is persisted in Armada-owned storage through `PlanningSession` and `PlanningSessionMessage`.
- Each user message is stored first, then Armada launches one planning turn for the selected captain.
- Armada writes a `context.md` prompt artifact per session containing captain instructions, vessel context, style/model context, selected playbooks, and the transcript so far.
- The selected built-in runtime is relaunched for each assistant turn using the existing one-shot runtime contract.
- Assistant output is streamed back into the current assistant message over WebSocket events and persisted incrementally.
- The user can choose an assistant reply, summarize it into a cleaner server-owned draft, open that draft in the main `Dispatch` page, or dispatch it directly from the planning page.
- Planning sessions can be stopped manually, deleted manually, or cleaned up automatically through inactivity/retention settings.
- The resulting voyage records `SourcePlanningSessionId` and `SourcePlanningMessageId`.

## Current Limitations

- [x] Planning currently supports only built-in `ClaudeCode`, `Codex`, `Gemini`, and `Cursor` runtimes.
- [x] `Custom` captains are explicitly blocked from planning sessions.
- [x] SQLite is the only fully implemented backend for planning sessions today.
- [x] SQL Server, PostgreSQL, and MySQL compile but intentionally reject planning-session operations with explicit unsupported behavior.
- [x] Playbook selections are persisted with the session, but playbook content is not snapshotted at session start.
- [x] The current shipped planning flow still uses transcript-backed turn relaunches instead of a long-lived stdin-persistent runtime.

## Locked Decisions

- [x] User-facing name is `Planning`.
- [x] Domain/storage record name is `PlanningSession`.
- [x] V1 targets the React dashboard only.
- [x] A captain may not be both mission-busy and planning-busy at the same time.
- [x] `CaptainStateEnum` includes a distinct `Planning` state.
- [x] A vessel is required to start a planning session in v1.
- [x] A dock is provisioned when the session starts and held for the session lifetime.
- [x] Dispatch launches from the planning page after optional title and description edits.
- [x] Dispatch conversion uses one seeded planning output and then hands off to the normal Armada pipeline system.
- [x] Transcript data persists in the database and survives refresh.
- [x] Runtime support is explicitly gated for planning in both the server and the dashboard.
- [x] Unsupported runtimes are blocked, not merely warned.
- [x] Non-SQLite planning-session support remains explicitly out of scope for the current product slice.
- [x] Session-start playbook snapshotting is not required for the current product slice; dispatch-time mission snapshots remain the reproducibility boundary.

## Delivered Architecture

### Core Domain

- [x] `PlanningSession`
- [x] `PlanningSessionMessage`
- [x] `PlanningSessionStatusEnum`
- [x] `CaptainStateEnum.Planning`
- [x] `Voyage.SourcePlanningSessionId`
- [x] `Voyage.SourcePlanningMessageId`
- [x] `Captain.SupportsPlanningSessions`
- [x] `Captain.PlanningSessionSupportReason`
- [x] `PlanningSessionSummaryRequest`
- [x] `PlanningSessionSummaryResponse`

### Persistence

- [x] Planning-session database interfaces were added to `DatabaseDriver`.
- [x] SQLite tables and CRUD implementations exist for sessions and messages.
- [x] SQLite voyage persistence includes planning lineage fields.
- [x] Voyage playbook selections and mission snapshots continue to work when dispatch is created from planning output.
- [x] Non-SQLite drivers expose the shape and fail explicitly instead of silently ignoring the feature.

### Runtimes

- [x] `IAgentRuntime` exposes planning capability gating.
- [x] `IInteractiveAgentRuntime` now exists as an explicit future-facing contract for persistent-session runtimes.
- [x] The shipped product still uses transcript-backed relaunches rather than the interactive contract.

### Server

- [x] `PlanningSessionCoordinator` owns reservation, dock lifecycle, turn execution, recovery, summarize, cleanup, and dispatch conversion.
- [x] `PlanningSessionRoutes` exposes list/create/get/send/summarize/dispatch/stop/delete endpoints.
- [x] `ArmadaServer` wires the coordinator, routes, startup recovery, and planning cleanup maintenance.
- [x] Planning runtime creation is centrally gated through the coordinator.
- [x] Captain stop and delete flows understand planning sessions.

### Dashboard

- [x] `/planning` and `/planning/:id` routes exist in the React dashboard.
- [x] Planning sessions can be created with captain, fleet, vessel, pipeline, and playbook selection.
- [x] Transcript streaming works through WebSocket updates.
- [x] Assistant output can be selected, summarized, opened in the main dispatch page, or dispatched directly from the page.
- [x] Unsupported captains are visibly disabled for planning in the UI.
- [x] The page calls out that planning reserves the captain and dock and may inspect or modify the repo.
- [x] `Planning.tsx` has been split into smaller planning components and shared utilities.
- [x] `Dispatch.tsx` accepts planning-prefilled handoff state from the planning page.

### Documentation

- [x] `README.md` explains planning before dispatch.
- [x] `GETTING_STARTED.md` includes a planning workflow walkthrough.
- [x] The docs call out built-in-runtime-only support.
- [x] The docs call out the SQLite-first limitation.
- [x] The docs call out the transcript-backed relaunch behavior.
- [x] The docs call out captain and dock reservation behavior.
- [x] The docs describe summarize/open-in-dispatch/delete behavior and the cleanup settings.

### Tests

- [x] Planning session model coverage exists.
- [x] Planning session SQLite persistence coverage exists.
- [x] Voyage lineage model coverage exists.
- [x] Authorization coverage includes planning routes.
- [x] Coordinator lifecycle coverage exists for create, failure cleanup, double-booking protection, summarize fallback, dispatch, default dispatch source selection, stop, delete, maintenance cleanup, and recovery.
- [x] Route-level automated planning API coverage exists.
- [x] Planning WebSocket payload regression coverage exists.
- [x] Dashboard behavior and utility tests exist for the extracted planning UI.
- [x] Release-version test coverage remains clean.

## File Map

These are the primary files carrying the shipped feature today.

### Core

- [x] `src/Armada.Core/Constants.cs`
- [x] `src/Armada.Core/Enums/CaptainStateEnum.cs`
- [x] `src/Armada.Core/Enums/PlanningSessionStatusEnum.cs`
- [x] `src/Armada.Core/Models/Captain.cs`
- [x] `src/Armada.Core/Models/PlanningSession.cs`
- [x] `src/Armada.Core/Models/PlanningSessionMessage.cs`
- [x] `src/Armada.Core/Models/PlanningSessionCreateRequest.cs`
- [x] `src/Armada.Core/Models/PlanningSessionMessageRequest.cs`
- [x] `src/Armada.Core/Models/PlanningSessionDispatchRequest.cs`
- [x] `src/Armada.Core/Models/PlanningSessionSummaryRequest.cs`
- [x] `src/Armada.Core/Models/PlanningSessionSummaryResponse.cs`
- [x] `src/Armada.Core/Models/Voyage.cs`
- [x] `src/Armada.Core/Settings/ArmadaSettings.cs`
- [x] `src/Armada.Core/Database/DatabaseDriver.cs`
- [x] `src/Armada.Core/Database/Interfaces/IPlanningSessionMethods.cs`
- [x] `src/Armada.Core/Database/Interfaces/IPlanningSessionMessageMethods.cs`
- [x] `src/Armada.Core/Database/Sqlite/Implementations/PlanningSessionMethods.cs`
- [x] `src/Armada.Core/Database/Sqlite/Implementations/PlanningSessionMessageMethods.cs`
- [x] `src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs`

### Runtimes

- [x] `src/Armada.Runtimes/Interfaces/IAgentRuntime.cs`
- [x] `src/Armada.Runtimes/Interfaces/IInteractiveAgentRuntime.cs`
- [x] `src/Armada.Runtimes/BaseAgentRuntime.cs`

### Server

- [x] `src/Armada.Server/PlanningSessionCoordinator.cs`
- [x] `src/Armada.Server/Routes/PlanningSessionRoutes.cs`
- [x] `src/Armada.Server/Routes/CaptainRoutes.cs`
- [x] `src/Armada.Server/ArmadaServer.cs`

### Dashboard

- [x] `src/Armada.Dashboard/src/App.tsx`
- [x] `src/Armada.Dashboard/src/App.css`
- [x] `src/Armada.Dashboard/src/api/client.ts`
- [x] `src/Armada.Dashboard/src/types/models.ts`
- [x] `src/Armada.Dashboard/src/pages/Planning.tsx`
- [x] `src/Armada.Dashboard/src/pages/Dispatch.tsx`
- [x] `src/Armada.Dashboard/src/pages/planning/planningUtils.ts`
- [x] `src/Armada.Dashboard/src/components/planning/PlanningStartCard.tsx`
- [x] `src/Armada.Dashboard/src/components/planning/PlanningSessionListCard.tsx`
- [x] `src/Armada.Dashboard/src/components/planning/PlanningTranscriptCard.tsx`
- [x] `src/Armada.Dashboard/src/components/planning/PlanningDispatchCard.tsx`

### Docs

- [x] `README.md`
- [x] `GETTING_STARTED.md`
- [x] `PLANNING.md`

### Tests

- [x] `test/Armada.Test.Unit/Suites/Models/PlanningSessionModelTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Database/PlanningSessionDatabaseTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Models/VoyageModelTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Models/CaptainModelTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Services/AuthorizationConfigTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Services/PlanningSessionCoordinatorTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Services/ReleaseVersionTests.cs`
- [x] `test/Armada.Test.Unit/Program.cs`
- [x] `test/Armada.Test.Automated/Suites/PlanningSessionTests.cs`
- [x] `test/Armada.Test.Automated/Suites/PlanningWebSocketTests.cs`
- [x] `test/Armada.Test.Automated/Program.cs`
- [x] `src/Armada.Dashboard/src/components/planning/PlanningDispatchCard.test.tsx`
- [x] `src/Armada.Dashboard/src/components/planning/PlanningTranscriptCard.test.tsx`
- [x] `src/Armada.Dashboard/src/pages/planning/planningUtils.test.ts`

## Workstreams

### Workstream 1: Domain Model And Persistence

- [x] Add planning-session domain models and enums.
- [x] Add captain planning-state support.
- [x] Add voyage lineage fields for planning origin.
- [x] Add SQLite schema and CRUD implementations.
- [x] Persist selected playbook selections with the session.
- [x] Persist planning lineage on voyages.
- [x] Keep non-SQLite backends buildable with explicit unsupported behavior.
- [x] Lock the current product scope to SQLite-backed planning sessions and keep other backends on explicit unsupported behavior.
- [x] Lock the current product scope to dispatch-time playbook snapshots rather than session-start playbook-content snapshots.

### Workstream 2: Runtime Strategy And Capability Gating

- [x] Preserve the existing one-shot mission runtime contract for v1.
- [x] Implement transcript-backed multi-turn planning by relaunching the captain per turn.
- [x] Surface runtime capability support on captains.
- [x] Block unsupported runtimes in the dashboard.
- [x] Enforce unsupported-runtime rejection in the coordinator.
- [x] Document the transcript-relaunch runtime reality.
- [x] Add an explicit `IInteractiveAgentRuntime` contract for future persistent-session work.

### Workstream 3: Planning Session Coordination And Dispatch

- [x] Reserve captains for planning sessions.
- [x] Provision planning docks and branches before first turn execution.
- [x] Persist user messages before execution.
- [x] Persist assistant output incrementally.
- [x] Prevent starting a planning session on a busy captain.
- [x] Prevent double-booking a captain already reserved for planning.
- [x] Prevent concurrent turns in the same planning session.
- [x] Release docks and captains on stop.
- [x] Recover active and responding sessions on server start into a sane state.
- [x] Dispatch from selected assistant output.
- [x] Default dispatch to the latest non-empty assistant output when no message is explicitly selected.
- [x] Carry pipeline and selected playbooks into downstream Armada dispatch behavior.
- [x] Add configurable inactivity timeout and retention cleanup controls.

### Workstream 4: API And Streaming

- [x] `GET /api/v1/planning-sessions`
- [x] `POST /api/v1/planning-sessions`
- [x] `GET /api/v1/planning-sessions/{id}`
- [x] `POST /api/v1/planning-sessions/{id}/messages`
- [x] `POST /api/v1/planning-sessions/{id}/summarize`
- [x] `POST /api/v1/planning-sessions/{id}/dispatch`
- [x] `POST /api/v1/planning-sessions/{id}/stop`
- [x] `DELETE /api/v1/planning-sessions/{id}`
- [x] Explicit `501` behavior for unsupported backends.
- [x] Broadcast `planning-session.changed`.
- [x] Broadcast `planning-session.message.created`.
- [x] Broadcast `planning-session.message.updated`.
- [x] Broadcast `planning-session.summary.created`.
- [x] Broadcast `planning-session.dispatch.created`.
- [x] Broadcast `planning-session.deleted`.
- [x] Add route-level automated tests.
- [x] Add event-payload regression tests.

### Workstream 5: Dashboard And UX

- [x] Add planning models and API client methods.
- [x] Register planning routes and navigation.
- [x] Add session setup, transcript, stop, and dispatch actions.
- [x] Handle live updates from WebSocket events.
- [x] Show explicit runtime support status in the UI.
- [x] Show explicit messaging around captain reservation, dock reservation, and repo mutation expectations.
- [x] Split `Planning.tsx` into smaller components and shared planning utilities.
- [x] Add `Summarize` and `Open In Dispatch` flows.
- [x] Add dashboard behavior tests.

### Workstream 6: Documentation And Verification

- [x] Update `README.md` with the planning workflow.
- [x] Update `GETTING_STARTED.md` with a planning-to-dispatch walkthrough.
- [x] Document the SQLite-first limitation and non-SQLite behavior.
- [x] Document the transcript-backed turn-relaunch model.
- [x] Document captain and dock reservation behavior.
- [x] Document summarize/open-in-dispatch/delete behavior and cleanup settings.
- [x] Keep `PLANNING.md` aligned with the shipped state.

## Verification

Verified in this branch on `2026-04-29`:

- [x] `dotnet build src\Armada.sln`
- [x] `dotnet test test\Armada.Test.Unit\Test.Unit.csproj`
- [x] Focused automated planning harness covering `PlanningSessionTests` and `PlanningWebSocketTests`
- [x] `npm.cmd run build` in `src\Armada.Dashboard`
- [x] `npm.cmd run test:run` in `src\Armada.Dashboard`
- [x] `git diff --check`

## Future Expansion Ideas

These are not part of the completed current-slice backlog:

- Real SQL Server, PostgreSQL, and MySQL implementations for planning-session persistence if demand appears outside SQLite deployments.
- Adoption of `IInteractiveAgentRuntime` for a true long-lived planning session instead of transcript-backed relaunches.
- Playbook-content snapshotting at session start if reproducibility requirements move earlier than mission dispatch.
- Richer planning analytics, retention policies, lineage views, or branch/share workflows if the product moves beyond the current planning-to-dispatch slice.

## Definition Of Done For The Current Slice

- [x] A user can start a planning session from the React dashboard.
- [x] The session preserves a transcript in Armada-owned persistence.
- [x] The captain is reserved while planning is active.
- [x] Assistant output streams live into the dashboard.
- [x] The user can summarize, open in dispatch, or dispatch directly from selected planning output.
- [x] The resulting voyage records source planning lineage.
- [x] User-facing docs explain the workflow and its current limitations.
- [x] Runtime capability gating is enforced in both the server and the dashboard.
- [x] Automated route, WebSocket, unit, and dashboard tests cover the shipped planning workflow.
