# Mux Captain Integration Plan

Last updated: 2026-04-30

This document now tracks the live state of Armada's Mux captain integration instead of only the proposed work. It should stay actionable: completed items are checked, remaining items stay explicit, and each section points to the concrete surfaces involved.

## Current Status

- [x] Armada can create, validate, persist, inspect, and launch `Mux` captains with a user-specified named endpoint.
- [x] Endpoint/config selection is exposed through REST, MCP, Helm, the React dashboard, and the legacy dashboard.
- [x] Planning sessions and mission instruction files support `Mux`.
- [ ] Generated API docs and higher-fidelity end-to-end tests still need follow-up.

## Scope and Constraints

The shipped Armada contract assumes the Mux improvements in `C:\code\mux\ARMADA_IMPROVEMENTS.md` are present:

- `mux print` is the non-interactive launch surface
- `mux probe --output-format json --require-tools` is the validation surface
- `mux endpoint list/show --output-format json` is the endpoint inspection surface
- `--config-dir` is supported
- `--output-last-message <path>` is supported
- authenticated captains should use named endpoints stored in Mux config

Still out of scope for the Armada MVP:

- [ ] Raw ad-hoc auth/header entry in Armada for Mux captains
- [ ] Rich parsing of every Mux JSONL event into Armada telemetry
- [ ] Mux-managed MCP configuration inside Armada captain forms

## Implemented Armada Contract

### Launch

- [x] Armada launches Mux captains with `mux print`
- [x] Armada passes `--working-directory`
- [x] Armada passes `--endpoint <name>`
- [x] Armada passes `--config-dir <path>` when configured
- [x] Armada passes `--output-format jsonl`
- [x] Armada passes `--output-last-message <path>` and uses the artifact as canonical mission output
- [x] Armada passes `--model <Captain.Model>` when configured
- [x] Armada passes optional safe overrides from `MuxCaptainOptions`
- [x] Armada defaults to `--yolo` unless an explicit Mux approval policy override is set

### Validation

- [x] Armada validates `Mux` captains with `mux probe --output-format json --require-tools`
- [x] Validation considers runtime options, not just `Captain.Model`
- [x] Validation rejects missing endpoints before launch
- [x] Validation rejects invalid runtime-options JSON
- [x] Validation rejects unsupported contract versions
- [x] Validation rejects tool-disabled endpoints

### Endpoint Inspection

- [x] Armada exposes endpoint discovery backed by `mux endpoint list/show`
- [x] REST routes:
  - `GET /api/v1/runtimes/mux/endpoints`
  - `GET /api/v1/runtimes/mux/endpoints/{name}`
- [x] React dashboard uses endpoint discovery
- [x] Legacy dashboard now uses endpoint discovery via a refreshable endpoint picker

## Completed Workstreams

### 1. Add `Mux` as a built-in runtime everywhere Armada hard-codes runtime lists

- [x] `src/Armada.Core/Enums/AgentRuntimeEnum.cs`
- [x] `src/Armada.Runtimes/AgentRuntimeFactory.cs`
- [x] `src/Armada.Core/Services/RuntimeDetectionService.cs`
- [x] `src/Armada.Core/Settings/ArmadaSettings.cs`
- [x] `src/Armada.Server/Routes/StatusRoutes.cs`
- [x] `src/Armada.Helm/Commands/*` runtime selection/help text
- [x] React dashboard runtime lists
- [x] Legacy dashboard runtime lists
- [x] Planning-support strings in captain model and UI

Notes:

- Runtime detection now includes Mux install hints.
- Auto-creation flows intentionally skip Mux because a named endpoint is required.

### 2. Add runtime options to captains and persistence

- [x] `Captain.RuntimeOptionsJson` added
- [x] Typed `MuxCaptainOptions` added
- [x] Helper serialization/deserialization added through `CaptainRuntimeOptions`
- [x] SQLite migration added
- [x] PostgreSQL migration added
- [x] MySQL migration added
- [x] SQL Server migration added
- [x] Captain CRUD paths preserve Mux runtime options correctly

Decision implemented:

- [x] `Endpoint` is required for `Runtime = Mux`
- [x] `ConfigDirectory` is optional
- [x] `Captain.Model` remains the generic model override

### 3. Implement `MuxRuntime`

- [x] `src/Armada.Runtimes/MuxRuntime.cs`
- [x] `src/Armada.Core/Services/MuxCommandBuilder.cs`
- [x] Runtime inherits from `BaseAgentRuntime`
- [x] Final-output recovery uses `--output-last-message`
- [x] Mission launch path passes the full captain object into the runtime
- [x] Planning launch path passes the full captain object into the runtime

### 4. Add Mux-specific validation

- [x] `AgentLifecycleHandler` has a runtime-specific Mux validation path
- [x] Validation uses `MuxCliService`
- [x] Validation is endpoint-aware and config-dir-aware
- [x] Validation surfaces user-facing errors for missing/invalid endpoint configuration

Remaining improvement:

- [ ] Add deterministic positive probe tests using a fake `mux` shim or injectable CLI abstraction

### 5. Expose Mux endpoint configuration through Armada UX

- [x] REST captain create/update/read round-trips `RuntimeOptionsJson`
- [x] MCP create/update args expose typed Mux fields
- [x] Helm `captain add` supports Mux endpoint/config and overrides
- [x] Helm `captain update` supports Mux endpoint/config and overrides
- [x] React dashboard create/edit/detail views support Mux endpoint/config and overrides
- [x] Legacy dashboard create/edit modals support Mux endpoint/config and overrides

Minimum MVP fields shipped:

- [x] `Runtime`
- [x] `Model`
- [x] `Mux Config Directory`
- [x] `Mux Endpoint`

Advanced fields shipped:

- [x] `Base URL`
- [x] `Adapter Type`
- [x] `Temperature`
- [x] `Max Tokens`
- [x] `System Prompt Path`
- [x] `Approval Policy`

Known UX limitation:

- [ ] Helm/dashboard do not yet provide an explicit "clear this one Mux override back to null" affordance for every optional field

### 6. Add endpoint discovery helpers for user-specified endpoints

- [x] `MuxCliService.ListEndpointsAsync`
- [x] `MuxCliService.ShowEndpointAsync`
- [x] `RuntimeRoutes` REST endpoints
- [x] React dashboard endpoint browsing
- [x] Legacy dashboard endpoint browsing

Notes:

- Armada relies on the redacted endpoint payloads returned by Mux and does not attempt to reconstruct secrets.

### 7. Add mission instruction file support for Mux

- [x] `MUX.md` is now the Mux-specific instruction filename
- [x] Mission prompt generation includes `Mux`
- [x] Mission artifact ignore/fallback lists include `MUX.md`

### 8. Wire Mux into planning sessions

- [x] Mux captains are planning-session capable
- [x] Planning launches route through the runtime with captain-specific options
- [x] Planning UI messaging now includes Mux

### 9. Add test coverage

- [x] Runtime adapter tests for `MuxRuntime`
- [x] Unit validation tests for missing endpoint and invalid runtime-options JSON
- [x] Runtime factory tests now cover Mux
- [x] Captain serialization tests now cover `RuntimeOptionsJson`
- [x] Runtime test runner now actually executes the Mux suite

Still needed:

- [ ] Positive end-to-end API tests for a valid Mux captain, backed by a fake `mux` executable or dedicated test fixture
- [ ] Automated tests for endpoint discovery routes with a controllable Mux fixture
- [ ] React dashboard component tests for Mux endpoint selection UX

### 10. Update docs and operator guidance

- [x] README updated for Mux runtime support, planning support, security defaults, Helm examples, and endpoint requirements
- [x] `docs/TESTING.md` updated to reflect runtime coverage

Still needed:

- [ ] Regenerate or hand-update `docs/REST_API.md` for `RuntimeOptionsJson`, Mux runtime values, and runtime route endpoints
- [ ] Regenerate or hand-update `docs/MCP_API.md` for Mux captain fields
- [ ] Update orchestrator instruction docs (`docs/INSTRUCTIONS_FOR_*.md`) where runtime lists still omit Mux

## Remaining Follow-Up Tasks

These are the concrete items still worth doing after the current implementation pass:

1. [ ] Add a fake `mux` test harness so Armada can run stable positive-path API and runtime-route tests without requiring a developer's real local Mux setup.
2. [ ] Regenerate or update REST and MCP API docs to reflect the new captain contract and runtime routes.
3. [ ] Decide whether Helm/dashboard should support clearing individual optional Mux override fields back to `null` explicitly.
4. [ ] Decide whether the legacy dashboard also needs endpoint detail inspection, not just endpoint-name browsing.

## Delivery Order From This Point

- [x] 1. Runtime registration and detection
- [x] 2. Captain runtime options and persistence
- [x] 3. `MuxRuntime` launch flow with final-message artifacts
- [x] 4. `mux probe` validation
- [x] 5. Endpoint/config UX in REST, MCP, Helm, and dashboards
- [x] 6. Endpoint discovery helper APIs and picker UX
- [x] 7. `MUX.md` mission file and planning support
- [ ] 8. Final doc regeneration and Mux-positive automated integration tests

## Practical Definition of Done

The core product goal is now met when judged against runtime behavior:

- [x] A user can create a captain with `Runtime = Mux` from the dashboard, REST API, Helm CLI, and MCP
- [x] That captain stores a user-specified Mux endpoint in Armada
- [x] Armada validates the selected endpoint through `mux probe --require-tools`
- [x] Armada launches the captain with `mux print` using the stored endpoint/config settings
- [x] Armada captures the final answer from `--output-last-message`
- [x] Authenticated backends work through named endpoints stored in the selected Mux config directory
- [x] Planning sessions work for Mux captains
- [ ] Automated positive-path tests and generated API docs still need to catch up to the shipped behavior
