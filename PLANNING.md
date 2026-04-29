# Planning Session To Dispatch

## Status Snapshot

As of `2026-04-29`, the repo has a working first vertical slice of planning-to-dispatch.

- [~] Core planning-session workflow is implemented and buildable end to end.
- [x] React dashboard has a `Planning` page with session setup, transcript, message send, stop, and dispatch actions.
- [x] Server has a planning-session domain model, SQLite persistence, REST routes, WebSocket events, and coordinator logic.
- [x] Captains can enter a dedicated `Planning` state and are blocked from normal mission use while planning is active.
- [x] Voyages record lineage back to the planning session and source message.
- [~] Test coverage exists for models, SQLite persistence, and authz, but service/route/dashboard coverage is still incomplete.
- [ ] End-user docs in `README.md` and `GETTING_STARTED.md` do not describe the planning workflow yet.
- [ ] A true persistent interactive runtime contract does not exist yet; v1 uses transcript-backed turn relaunches.

## How To Maintain This File

- Use `[ ]` for not started, `[~]` for in progress, and `[x]` for complete.
- Add initials, date, PR number, or short notes inline when that helps future readers.
- When the implementation changes, update:
  - the status snapshot
  - the implementation reality section
  - the relevant workstream checklist
  - the verification section
- Do not quietly leave aspirational items marked complete if the shipped behavior differs.

Suggested annotation format:

- `[~] Add route tests - JC - 2026-04-29`

## Product Goal

Armada should support the full path from:

1. plan with a captain in the dashboard
2. preserve that transcript as Armada-owned context
3. create a dispatch directly from the planning output

This removes the current copy/paste seam between agent planning and Armada execution.

## Locked Decisions

- [x] User-facing name is `Planning`.
- [x] Domain/storage record name is `PlanningSession`.
- [x] V1 targets the React dashboard only.
- [x] A captain may not be both mission-busy and planning-busy at the same time.
- [x] `CaptainStateEnum` includes a distinct `Planning` state.
- [x] A vessel is required to start a planning session in v1.
- [x] A dock is provisioned when the session starts and held for the session lifetime.
- [x] V1 dispatch conversion creates one voyage with one seeded mission.
- [x] Dispatch can be launched directly from the planning page after optional title/description edits.
- [x] Transcript data persists in the database and survives refresh.
- [~] Playbook selections persist with the session, but playbook content is not snapshotted at session start.
- [~] SQLite is the first real backend. Other database backends compile but intentionally throw `NotSupportedException`.
- [ ] Runtime capability gating and a formal interactive-support allowlist are not implemented.

## Implementation Reality

The shipped v1 is not a raw terminal and not a true stdin-persistent interactive runtime.

Actual behavior today:

- A planning session reserves a captain and provisions a dock/branch for a specific vessel.
- Each user message is appended to the transcript in the database.
- For each assistant turn, Armada writes a `context.md` prompt file containing:
  - captain instructions
  - vessel context
  - style/model context
  - selected playbooks
  - the transcript so far
- Armada then launches the selected captain runtime with the existing one-shot `StartAsync` path and asks it to continue the planning conversation from that prompt file.
- Assistant output is streamed back into the current assistant transcript message over WebSocket events.
- When the turn exits, the session returns to `Active` and can accept another user message.

This is good enough for multi-turn planning inside Armada, but it is not yet a generalized interactive runtime contract.

## Delivered Architecture

### Core Domain

- [x] `PlanningSession`
- [x] `PlanningSessionMessage`
- [x] `PlanningSessionStatusEnum`
- [x] `CaptainStateEnum.Planning`
- [x] `Voyage.SourcePlanningSessionId`
- [x] `Voyage.SourcePlanningMessageId`

### Persistence

- [x] Planning session database interfaces were added to `DatabaseDriver`.
- [x] SQLite tables and CRUD implementations exist for sessions and messages.
- [x] SQLite voyage persistence includes planning lineage fields.
- [x] SQL Server, PostgreSQL, and MySQL drivers expose the shape but currently reject the feature with `NotSupportedException`.

### Server

- [x] `PlanningSessionCoordinator` owns reservation, dock lifecycle, turn execution, recovery, and dispatch conversion.
- [x] `PlanningSessionRoutes` exposes list/create/get/send/dispatch/stop endpoints.
- [x] `ArmadaServer` wires the coordinator, routes, and startup recovery.
- [x] Captain stop/delete flows understand planning sessions.

### Dashboard

- [x] `/planning` and `/planning/:id` routes exist in the React dashboard.
- [x] Planning sessions can be created with captain, fleet, vessel, pipeline, and playbook selection.
- [x] Transcript streaming works through WebSocket updates.
- [x] Assistant output can be selected and dispatched directly from the page.
- [x] Captain detail actions recognize the `Planning` state.

## Explicit Non-Goals For This Slice

- [x] No generic shell or terminal emulator.
- [x] No reuse of `Mission` as the planning persistence model.
- [x] No automatic multi-mission decomposition.
- [x] No legacy dashboard parity.
- [x] No forced redesign of the one-shot mission runtime contract.
- [x] No fake claim of universal runtime support.

## File Map

These are the primary files already carrying the feature:

### Core

- [x] `src/Armada.Core/Constants.cs`
- [x] `src/Armada.Core/Enums/CaptainStateEnum.cs`
- [x] `src/Armada.Core/Enums/PlanningSessionStatusEnum.cs`
- [x] `src/Armada.Core/Models/PlanningSession.cs`
- [x] `src/Armada.Core/Models/PlanningSessionMessage.cs`
- [x] `src/Armada.Core/Models/PlanningSessionCreateRequest.cs`
- [x] `src/Armada.Core/Models/PlanningSessionMessageRequest.cs`
- [x] `src/Armada.Core/Models/PlanningSessionDispatchRequest.cs`
- [x] `src/Armada.Core/Models/Voyage.cs`
- [x] `src/Armada.Core/Database/DatabaseDriver.cs`
- [x] `src/Armada.Core/Database/Interfaces/IPlanningSessionMethods.cs`
- [x] `src/Armada.Core/Database/Interfaces/IPlanningSessionMessageMethods.cs`
- [x] `src/Armada.Core/Database/Sqlite/Implementations/PlanningSessionMethods.cs`
- [x] `src/Armada.Core/Database/Sqlite/Implementations/PlanningSessionMessageMethods.cs`
- [x] `src/Armada.Core/Database/Sqlite/Queries/TableQueries.cs`

### Server

- [x] `src/Armada.Server/PlanningSessionCoordinator.cs`
- [x] `src/Armada.Server/Routes/PlanningSessionRoutes.cs`
- [x] `src/Armada.Server/Routes/CaptainRoutes.cs`
- [x] `src/Armada.Server/ArmadaServer.cs`

### Dashboard

- [x] `src/Armada.Dashboard/src/App.tsx`
- [x] `src/Armada.Dashboard/src/components/Layout.tsx`
- [x] `src/Armada.Dashboard/src/api/client.ts`
- [x] `src/Armada.Dashboard/src/types/models.ts`
- [x] `src/Armada.Dashboard/src/pages/Planning.tsx`

### Tests

- [x] `test/Armada.Test.Unit/Suites/Models/PlanningSessionModelTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Database/PlanningSessionDatabaseTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Models/VoyageModelTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Services/AuthorizationConfigTests.cs`
- [x] `test/Armada.Test.Unit/Suites/Database/DatabaseInitializationTests.cs`
- [x] `test/Armada.Test.Unit/Program.cs`

## Workstreams

### Workstream 1: Domain Model And Enums

- [x] Add `PlanningSession`.
- [x] Add `PlanningSessionMessage`.
- [x] Add `PlanningSessionStatusEnum`.
- [x] Add ID prefixes for sessions and messages.
- [x] Add `CaptainStateEnum.Planning`.
- [x] Add voyage lineage fields for planning origin.

### Workstream 2: Database And Persistence

- [x] Add planning session interfaces to the database layer.
- [x] Add SQLite schema and CRUD implementations.
- [x] Add session/message enumeration helpers.
- [x] Persist selected playbook selections with the session.
- [x] Persist planning lineage on voyages.
- [x] Add unit tests for create/read/update/enumerate/delete flows.
- [~] Keep non-SQLite backends buildable with explicit `NotSupportedException` stubs.
- [ ] Add real SQL Server support.
- [ ] Add real PostgreSQL support.
- [ ] Add real MySQL support.
- [ ] Decide whether playbook content snapshotting is required for reproducibility and implement it if yes.

### Workstream 3: Runtime Strategy

- [x] Preserve the existing one-shot mission runtime contract.
- [x] Implement transcript-backed multi-turn planning by relaunching the captain per turn against the same dock and transcript context.
- [x] Stream assistant output back into the transcript while the turn is running.
- [~] Recovery semantics exist at the session level on server restart.
- [ ] Add a real `IInteractiveAgentRuntime` contract if Armada needs persistent stdin sessions later.
- [ ] Add runtime capability reporting for planning support.
- [ ] Decide whether unsupported runtimes should be blocked in the UI or shown with warnings.

### Workstream 4: Planning Session Coordination

- [x] Reserve captains for planning sessions.
- [x] Provision planning docks and branches before first turn execution.
- [x] Release docks and captains on stop.
- [x] Persist user messages before execution.
- [x] Persist assistant output incrementally.
- [x] Emit events for create/stop/dispatch.
- [x] Recover active/responding sessions on server start and return them to a sane state.
- [x] Prevent starting a planning session on a busy captain.
- [x] Prevent concurrent turns in the same planning session.
- [ ] Add configurable inactivity timeout or retention cleanup if needed.

### Workstream 5: REST API

- [x] `GET /api/v1/planning-sessions`
- [x] `POST /api/v1/planning-sessions`
- [x] `GET /api/v1/planning-sessions/{id}`
- [x] `POST /api/v1/planning-sessions/{id}/messages`
- [x] `POST /api/v1/planning-sessions/{id}/dispatch`
- [x] `POST /api/v1/planning-sessions/{id}/stop`
- [x] Authz rules added for planning-session endpoints.
- [x] Non-supported backends fail explicitly with `501`.
- [ ] `DELETE /api/v1/planning-sessions/{id}`
- [ ] `POST /api/v1/planning-sessions/{id}/summarize`
- [ ] Route-level automated tests.

### Workstream 6: WebSocket Streaming

- [x] Broadcast `planning-session.changed`.
- [x] Broadcast `planning-session.message.created`.
- [x] Broadcast `planning-session.message.updated`.
- [x] Broadcast `planning-session.dispatch.created`.
- [x] Rehydrate current state after page refresh through REST detail fetch plus live event subscription.
- [ ] Add automated event-payload regression tests.

### Workstream 7: Dispatch Conversion

- [x] Select assistant output as the source for dispatch.
- [x] Default to the latest non-empty assistant response if no message is explicitly selected.
- [x] Allow the user to edit voyage title and mission description before launch.
- [x] Carry pipeline and selected playbooks from the session into voyage dispatch.
- [x] Persist planning lineage on the resulting voyage.
- [ ] Add a server-owned summarize/extract flow if dispatch should not rely on raw assistant output.
- [ ] Consider multi-mission decomposition after the single-mission path is stable.

### Workstream 8: Dashboard Integration

- [x] Add planning models to `src/types/models.ts`.
- [x] Add API client methods.
- [x] Register planning routes.
- [x] Add navigation entry.
- [x] Add planning page with session setup and transcript.
- [x] Add dispatch panel on the same page.
- [x] Handle live updates with WebSocket events.
- [~] Current page is functional but still monolithic.
- [ ] Split `Planning.tsx` into smaller planning-specific components if follow-on changes make it harder to maintain.
- [ ] Show explicit runtime support/capability status in the UI.
- [ ] Add `Summarize` and `Open In Dispatch` flows if product still wants both.
- [ ] Add dashboard tests for planning page behavior.

### Workstream 9: Resource Ownership And Safety

- [x] Captains in `Planning` state can be stopped but not deleted.
- [x] Stop-all handling also attempts to stop active planning sessions.
- [x] Mission flows are protected by the captain state model.
- [x] Session start failure reclaims the dock and resets the captain when possible.
- [x] Session stop reclaims the dock and returns the captain to `Idle`.
- [ ] Add stronger UI messaging around repo mutation/tool access expectations during planning.

### Workstream 10: Logging And Observability

- [x] Planning-turn artifacts are written under `logs/planning-sessions/<session-id>/`.
- [x] Session lifecycle emits Armada events.
- [x] Transcript persistence is the primary audit trail.
- [x] Failure reasons are surfaced on the session and returned to the UI.
- [ ] Add first-class dashboard surfacing of session logs if needed.
- [ ] Decide whether planning lineage should be displayed on voyage detail pages.

### Workstream 11: Tests

- [x] Add planning session model tests.
- [x] Add SQLite planning persistence tests.
- [x] Add authz tests for planning routes.
- [x] Add voyage lineage model coverage.
- [~] Test runner registration is done, but deeper coverage is still missing.
- [ ] Add coordinator/service tests for start/send/stop/recovery/failure paths.
- [ ] Add captain double-booking tests at the service/route layer.
- [ ] Add planning route tests.
- [ ] Add WebSocket event-shape tests.
- [ ] Add dashboard tests.

### Workstream 12: Documentation

- [ ] Update `README.md` with the planning workflow.
- [ ] Update `GETTING_STARTED.md` with a planning-to-dispatch walkthrough.
- [ ] Document the SQLite-first limitation and non-SQLite behavior.
- [ ] Document the runtime reality of transcript-backed turn relaunches.
- [ ] Document captain/dock reservation behavior during planning.

## Verification

Last verified in this branch on `2026-04-29`:

- [x] `dotnet build src\Armada.sln`
- [x] `dotnet test test\Armada.Test.Unit\Test.Unit.csproj`
- [x] `npm.cmd run build` in `src\Armada.Dashboard`
- [~] `dotnet run --project test\Armada.Test.Unit\Test.Unit.csproj --framework net10.0`
  The planning-session coverage added in this branch is green. The runner still reports one unrelated existing failure: `Status Health Route Uses ProductVersion Constant`.

If a later change touches planning behavior, rerun at least:

- [ ] `dotnet test test\Armada.Test.Unit\Test.Unit.csproj`
- [ ] `npm.cmd run build`

## Remaining High-Value Follow-On Work

Priority order after the current vertical slice:

1. [ ] Add user-facing docs in `README.md` and `GETTING_STARTED.md`.
2. [ ] Add coordinator and route tests to harden lifecycle behavior.
3. [ ] Add runtime support/capability gating in the dashboard.
4. [ ] Decide whether to build true interactive runtime support or keep the transcript-relaunch model.
5. [ ] Add non-SQLite implementations if planning sessions must work outside SQLite deployments.
6. [ ] Add summarize/open-in-dispatch flows if product wants a softer handoff than direct dispatch.

## Risks And Mitigations

- [~] Risk: some runtimes may behave poorly when relaunched turn-by-turn.
  Mitigation: add explicit runtime capability reporting and block or warn in the UI.
- [~] Risk: non-SQLite deployments will discover the feature only at runtime today.
  Mitigation: document the limitation and add UI/server capability checks.
- [~] Risk: selected playbooks can drift after session creation because only selections, not snapshots, are stored.
  Mitigation: decide whether to snapshot playbook content at start.
- [x] Risk: captain contention between planning and missions.
  Mitigation: dedicated `Planning` captain state plus busy checks are already in place.
- [x] Risk: dock leaks when startup fails halfway through.
  Mitigation: the coordinator attempts dock reclaim and captain reset in failure paths.

## Definition Of Done For The Current Slice

- [x] A user can start a planning session from the React dashboard.
- [x] The session preserves a transcript in Armada-owned persistence.
- [x] The captain is reserved while planning is active.
- [x] Assistant output streams live into the dashboard.
- [x] The user can dispatch directly from selected planning output.
- [x] The resulting voyage records source planning lineage.
- [~] Critical paths have basic automated coverage, but hardening coverage is not complete.
- [ ] User-facing documentation is still pending.
