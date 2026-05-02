# Vessel Workspace Plan

Last updated: 2026-05-01

This document tracks the proposed and later live implementation of Armada's first-class `Workspace` feature. It should stay actionable: completed items are checked, remaining items stay explicit, and each workstream points to concrete surfaces a developer can touch.

## Product Goal

Armada should let a user open a vessel as a browsable, editable workspace inside the React dashboard. `Workspace` is not a hidden tab on the existing vessel detail page. It is a first-class dashboard area with:

- its own navigation entry
- its own routes
- its own REST surface
- its own shell layout
- direct hooks into Armada planning, dispatch, context curation, and review flows

The core value is not "a generic file browser in the browser." The core value is "a vessel-aware repository workspace that can hand selected code directly into Armada workflows."

## Current Status

Core Workspace MVP is now implemented in Armada. The remaining unchecked items below are real follow-up work, not placeholders.

- [x] A first-class `Workspace` route exists in the React dashboard
- [x] The sidebar/nav supports nested hierarchy with `Vessels > Workspace`
- [x] Users can browse a vessel file tree rooted at `Vessel.WorkingDirectory`
- [x] Users can open and edit text files in a browser editor
- [x] The Workspace editor uses a line-number gutter and fills the vertical pane instead of stacking cards below the editor
- [x] Users can save files with concurrency and path-safety protections
- [x] Users can search files and inspect local changes from Workspace
- [x] Users can hand selected files directly into Planning
- [x] Users can hand selected files directly into Dispatch
- [x] Users can use Workspace to curate `ProjectContext`, `StyleGuide`, and `ModelContext`
- [x] The file tree exposes per-entry actions for open, rename, delete, and metadata inspection
- [ ] Users can create or append playbooks from Workspace selections
- [x] Core backend and dashboard tests cover the shipped Workspace flows
- [ ] README, getting-started, and operator docs cover Workspace end to end

## Decisions

These are the recommended implementation decisions for the first pass. They should be treated as the default plan unless a future change explicitly supersedes them.

- Feature name: `Workspace`
- Primary UI: React dashboard only for MVP
- Canonical UI routes:
  - `/workspace`
  - `/workspace/:vesselId`
  - `/workspace/:vesselId/search`
  - `/workspace/:vesselId/changes`
  - `/workspace/:vesselId/context`
- Navigation placement: `Fleet > Vessels > Workspace` as the first child under a new collapsible `Vessels` group
- Existing vessel table keeps living under `Fleet > Vessels > Registry`
- Editable root: `Vessel.WorkingDirectory`
- No editing against the vessel bare repo `LocalPath`
- No editing against live captain dock worktrees in the MVP
- Editor engine for the MVP: a lightweight textarea-based editor inside `Workspace.tsx`
- Monaco or richer editor chrome is a follow-on improvement, not a ship blocker
- Save target: live files in the working directory, with no implicit git commit, branch creation, or merge action
- Initial center-pane behavior: open blank until the user explicitly selects a file
- Persistence in the MVP: keep recent files and tree expansion state, but do not auto-restore open tabs or file selections across reloads
- Center-pane layout in the MVP: let the editor fill the remaining viewport height; do not force a bottom card stack under the editor
- Explorer interaction in the MVP:
  - folders open and collapse inline from the tree
  - each file and folder row exposes a right-justified action menu
  - Workspace owns pane-level scrolling; the full page should not scroll during normal file navigation
- Binary files: metadata-only or download-only in the MVP
- Hidden paths:
  - always hide `.git`
  - hide or collapse noisy generated directories by default (`node_modules`, `bin`, `obj`, `dist`, `coverage`)
- Mission handoff format:
  - Workspace-driven dispatch should reuse Armada's existing mission scope directive style
  - generated mission text should include `Touch only ...` lines so current scope validation keeps working
- Legacy dashboard:
  - no full Workspace parity in the MVP
  - if necessary, legacy UI can link users into the React dashboard Workspace route

## Scope and Non-Goals

### In Scope

- Vessel-scoped file tree browsing
- Text file viewing and editing
- Save / rename / create / delete operations inside the working directory
- Search across files
- Change awareness for the current working tree
- First-class dashboard navigation and route structure
- Planning / Dispatch handoff from selected files
- Context curation actions tied to files and selections

### Out of Scope for the MVP

- Full VS Code parity
- Terminal emulator
- Debugger integration
- Multi-user live collaborative editing
- Editing active dock worktrees
- Image or binary asset editing beyond preview/metadata
- MCP exposure of arbitrary file editing tools
- Persisted database-backed file history indexing

## Why This Should Be First-Class

Making Workspace first-class changes Armada in three important ways:

1. It turns vessels from static registry records into active working surfaces.
2. It gives Planning and Dispatch a concrete code-selection source instead of relying on freeform text alone.
3. It creates a shared place where manual edits, local repo state, active missions, and captain orchestration can meet.

This is a better fit for Armada than hiding file actions under `VesselDetail`, because users will think of Workspace as a destination, not as a vessel metadata subform.

## Recommended Dashboard Information Architecture

The current React dashboard sidebar already supports section collapse state in `Layout.tsx`, but each section is still a flat list. Workspace is a good reason to promote the nav model from "flat items under sections" to "hierarchical nodes."

### Recommended Sidebar Structure

- Dashboard
- Operations
  - Planning
  - Dispatch
  - Voyages
  - Missions
  - Merge Queue
- Fleet
  - Fleets
  - Vessels
    - Workspace
    - Registry
  - Captains
  - Docks
- Activity
  - Signals
  - Events
  - Notifications
- System
  - Personas
  - Pipelines
  - Templates
  - Playbooks
  - Server
  - Doctor
- Administration
  - Tenants
  - Users
  - Credentials

### Navigation Rules

- `Workspace` should be the first child under `Vessels`
- `Registry` should map to the existing `/vessels` table view
- `/workspace` should open a vessel picker or restore the last-opened vessel
- deep links from other pages should be able to open a specific vessel and optional path
- section and sub-item collapse state should persist in local storage, just like the current section state does

## Target User Experience

### Workspace Home

When the user opens `/workspace` without a selected vessel:

- show a searchable vessel picker
- show recent vessels
- show vessel metadata that matters for Workspace:
  - name
  - fleet
  - working directory configured or not
  - active missions count
  - ahead/behind status if available

If a vessel does not have a valid working directory, Workspace should explain why the feature is unavailable for that vessel and link to the vessel edit screen.

### Workspace Shell

For `/workspace/:vesselId`, use a multi-pane shell:

- Left pane:
  - file tree
  - recent files
  - file search launcher
- Center pane:
  - editor tabs
  - breadcrumbs
  - welcome state when no file is open
  - diff view when comparing local edits or reviewing selected content
- Right pane:
  - selected files summary
  - active mission warnings
  - show only when the user has explicitly selected files; do not reserve persistent space for an empty selection rail

Implementation note:

- The first Workspace pass should optimize for a strong editor surface, not a card-heavy center pane
- Search, changes, and problem surfacing can move to side panels or dedicated follow-on UI if needed
- Primary workflow actions such as `Plan`, `Dispatch`, and `Context` should live in the header band near branch/ahead-behind state, not be buried in a side rail
- Do not reserve explorer space for a `Recent Files` section in the MVP
- Keep the vessel switcher on the same toolbar row as save/refresh/workflow actions, aligned to the far right

### Workspace Header

Recommended header content:

- vessel switcher
- branch name
- ahead/behind badge
- local dirty-state indicator
- active mission count
- save button
- refresh button
- inline `Plan` button
- inline `Dispatch` button
- inline `Context` button that opens a modal editor for vessel context fields

## First-Class Entry Points Into Workspace

- [x] Sidebar `Workspace` route
- [x] `Open Workspace` action from the vessel list
- [x] `Open Workspace` action from vessel detail
- [ ] `Open in Workspace` links from mission detail where file paths are known
- [ ] `Open in Workspace` links from voyage/dock/event surfaces where vessel + path context is available

## Concrete REST/API Contract

Workspace should have its own route surface instead of quietly overloading vessel CRUD routes. Recommended route family:

- `GET /api/v1/workspace/vessels/{vesselId}/tree`
- `GET /api/v1/workspace/vessels/{vesselId}/file?path=...`
- `PUT /api/v1/workspace/vessels/{vesselId}/file`
- `POST /api/v1/workspace/vessels/{vesselId}/directory`
- `POST /api/v1/workspace/vessels/{vesselId}/rename`
- `DELETE /api/v1/workspace/vessels/{vesselId}/entry?path=...`
- `GET /api/v1/workspace/vessels/{vesselId}/search?q=...`
- `GET /api/v1/workspace/vessels/{vesselId}/changes`
- `GET /api/v1/workspace/vessels/{vesselId}/status`

Optional later helper routes:

- `POST /api/v1/workspace/vessels/{vesselId}/selection/plan-draft`
- `POST /api/v1/workspace/vessels/{vesselId}/selection/dispatch-draft`
- `GET /api/v1/workspace/vessels/{vesselId}/history?path=...`

## Backend Contract Requirements

- All path operations must normalize and validate against the vessel root
- No request may escape the configured working directory
- Symlinks or junctions that resolve outside the root must be blocked
- `.git` must remain inaccessible
- Binary files must be detected and treated safely
- Save operations should use optimistic concurrency:
  - the read route returns a content hash or revision token
  - save requires the expected token
  - conflicting external changes produce a user-facing conflict response instead of silent overwrite
- Preserve newline style where practical
- Text decoding should be predictable and safe
- File reads and searches should enforce file size and result limits

## Recommended Backend Surfaces

- `src/Armada.Core/Services/Interfaces/IWorkspaceService.cs`
- `src/Armada.Core/Services/WorkspaceService.cs`
- `src/Armada.Server/Routes/WorkspaceRoutes.cs`
- workspace DTOs in `src/Armada.Core/Models` or a dedicated workspace DTO surface
- `src/Armada.Dashboard/src/api/client.ts`
- `src/Armada.Dashboard/src/types/*` for Workspace payloads

## Recommended Frontend Surfaces

- `src/Armada.Dashboard/src/App.tsx`
- `src/Armada.Dashboard/src/components/Layout.tsx`
- `src/Armada.Dashboard/src/pages/Workspace.tsx`
- `src/Armada.Dashboard/src/components/workspace/WorkspaceTree.tsx`
- `src/Armada.Dashboard/src/components/workspace/WorkspaceVesselPicker.tsx`
- `src/Armada.Dashboard/src/components/workspace/workspaceUtils.ts`

Implementation note:

- The MVP currently keeps the shell, editor, search panel, changes panel, and action rail inline inside `Workspace.tsx`
- Split-out components can happen later if the page becomes hard to maintain

## Workstreams

### 1. Navigation and Route Reorganization

- [x] Refactor the React sidebar model in `src/Armada.Dashboard/src/components/Layout.tsx` from flat `NavSection.items` to hierarchical nodes
- [x] Keep section-level collapse behavior and add nested group collapse behavior
- [x] Add `Vessels > Workspace` as the first child under a new collapsible `Vessels` nav group
- [x] Add `Vessels > Registry` for the existing `/vessels` list
- [x] Add a new `/workspace` route in `src/Armada.Dashboard/src/App.tsx`
- [x] Add a new `/workspace/:vesselId/*` route in `src/Armada.Dashboard/src/App.tsx`
- [x] Add last-opened vessel persistence in local storage and show recent vessels on `/workspace`
- [x] Add "Open Workspace" quick actions from `Vessels.tsx` and `VesselDetail.tsx`

Acceptance criteria:

- [x] A user can reach Workspace from the sidebar without going through vessel detail
- [x] The nav clearly presents Workspace as a first-class area
- [x] The existing `/vessels` list remains intact under `Registry`

### 2. Workspace Backend Service and Safety Layer

- [x] Add an explicit `IWorkspaceService`
- [x] Implement workspace root resolution from `Vessel.WorkingDirectory`
- [x] Reject vessels without a configured or existing working directory
- [x] Implement path normalization for Windows and Unix separators
- [x] Block traversal outside the vessel root
- [x] Block `.git` access completely
- [x] Define hidden-path policy for generated/noisy directories
- [x] Implement file type detection:
  - editable text
  - large text / read-only
  - binary / metadata-only
- [x] Add optimistic concurrency token generation for file reads
- [x] Add concurrency validation for file writes

Acceptance criteria:

- [x] A malicious or malformed path cannot escape the workspace root
- [x] Unsafe or unsupported files fail cleanly with clear error responses
- [x] Save conflicts are detected and surfaced

### 3. Workspace REST Routes

- [x] Create `src/Armada.Server/Routes/WorkspaceRoutes.cs`
- [x] Add tree route
- [x] Add file read route
- [x] Add file save route
- [x] Add create directory route
- [x] Add rename/move route
- [x] Add delete entry route
- [x] Add workspace status route
- [x] Add file search route
- [x] Add working tree changes route
- [x] Register new routes in the server startup path
- [x] Add OpenAPI summaries/descriptions for all new routes

Recommended payloads:

- [x] `WorkspaceTreeEntry`
- [x] `WorkspaceFileResponse`
- [x] `WorkspaceSaveRequest`
- [x] `WorkspaceSaveResult`
- [x] `WorkspaceSearchResult`
- [x] `WorkspaceChangesResult`
- [x] `WorkspaceStatusResult`

Acceptance criteria:

- [x] The dashboard can render the full MVP Workspace without ad hoc file-system access hacks
- [x] Route behavior is documented and testable in isolation

### 4. Workspace Shell UI

- [x] Add `Workspace.tsx` page
- [x] Add vessel picker landing state for `/workspace`
- [x] Add shell layout with left tree, center editor, right action rail, bottom panel
- [x] Add vessel switcher in the Workspace header
- [x] Add breadcrumbs and open tabs
- [x] Add empty-state UX when no file is selected
- [x] Add loading, unavailable, and error states
- [x] Persist opened tabs, active file, expanded tree state, selected files, and recent files in local storage

Acceptance criteria:

- [x] Workspace feels like a destination, not a hidden detail pane
- [x] A user can switch vessels without losing the overall workspace context

### 5. File Tree and File Viewing

- [x] Implement lazy-loading tree expansion
- [ ] Add search/filter within the visible tree
- [x] Add recent files list
- [x] Add open, rename, and delete actions for the active file
- [ ] Add copy-relative-path and reveal-in-tree actions
- [x] Add read-only preview for unsupported or oversized files
- [x] Add clear active-file metadata display:
  - relative path
  - size
  - modified timestamp
  - editability state

Acceptance criteria:

- [x] Large repositories remain usable
- [x] Users can quickly navigate to files they care about

### 6. Editor Integration and Save Flow

- [ ] Add Monaco dependencies to the dashboard package
- [x] Implement MVP text editing directly inside `Workspace.tsx`
- [ ] Add syntax highlighting for common text formats
- [x] Add dirty-state tracking
- [x] Add save action with optimistic concurrency token
- [ ] Add richer external-change conflict resolution UX beyond error/toast handling
- [x] Add keyboard shortcut for save
- [ ] Add keyboard shortcut for search
- [ ] Add inline or side-by-side diff view for local unsaved edits

Recommended MVP editor behaviors:

- [x] unsaved changes indicator
- [x] save success/failure toasts
- [ ] reopen file at last cursor position
- [x] preserve tab order across refresh where practical

Acceptance criteria:

- [x] A user can open, edit, and save text files reliably
- [x] A conflicting external change does not silently clobber content

### 7. Search and Changes

- [x] Add full-workspace content search
- [ ] Add file-name search
- [x] Return line numbers and short snippets for content hits
- [x] Add a current-working-tree changes panel
- [x] Show modified, added, deleted, and untracked files
- [ ] Add filters for changed-only and open-only views
- [x] Surface file-level git change details inside Workspace

Implementation note:

- [x] Prefer a reliable managed implementation first
- [ ] Optional later optimization: use `rg` when available with a managed fallback

Acceptance criteria:

- [x] A user can find code without leaving Armada
- [x] A user can understand current local change state before planning or dispatching new work

### 8. Armada-Aware Planning and Dispatch Integration

This is the part that makes Workspace distinct from a generic repo browser.

- [x] Add file multi-select in Workspace
- [ ] Add folder multi-select in Workspace
- [x] Add "Plan from selection" action
- [x] Add "Dispatch from selection" action
- [x] Add "Copy scoped file directive" action
- [x] Generate mission or planning drafts that include Armada-compatible scope lines such as `Touch only src/Foo.cs, src/Bar.cs`
- [x] Pre-fill `Planning.tsx` with selected paths and vessel context
- [x] Pre-fill `Dispatch.tsx` with selected paths and vessel context
- [x] Show active mission overlap warnings for selected files when active mission scope can be inferred from mission descriptions

Leverage already present in Armada:

- [x] Reuse the current `Touch only ...` scope directive style
- [ ] Centralize scope parsing with `MissionService` instead of route-local mirroring
- [x] Do not invent a second scope convention for the MVP

Acceptance criteria:

- [x] Workspace can generate useful Planning and Dispatch drafts from file selections
- [x] The resulting missions are more safely scoped than freehand dispatches

### 9. Context, Playbook, and Prompt Curation

- [x] Add "Send to Project Context" action
- [x] Add "Send to Style Guide" action
- [x] Add "Send to Model Context" action
- [ ] Add "Create Playbook from file or selection" action
- [ ] Add "Append file reference into playbook" helper flow
- [ ] Add preview UX before mutating vessel context or playbook content

Acceptance criteria:

- [x] Users can turn real repository content into reusable vessel context without copy/paste gymnastics
- [ ] Users can turn repository content into playbooks from Workspace

### 10. Active Mission Awareness and Safety

- [ ] Show active-mission banner for the current vessel
- [x] Show whether the vessel currently has running or handoff-state missions
- [x] Warn when selected files overlap with inferred active mission scopes
- [ ] Warn when local unsaved edits exist and the user is about to dispatch overlapping work
- [x] Decide whether some actions should be soft-warn only or hard-blocked

Recommended MVP policy:

- [x] Editing the working directory is allowed
- [x] Overlap detection should warn, not hard-block
- [x] Active dock worktrees stay out of scope for editing

Acceptance criteria:

- [x] Workspace helps users avoid self-inflicted landing conflicts

### 11. File History and Review Follow-Up

This is valuable, but it should be treated as a second-wave feature unless the first pass lands cleanly and quickly.

- [ ] Decide whether to persist changed-file lists per mission for efficient history lookup
- [ ] If yes, add a mission changed-files persistence surface
- [ ] Add "recent missions touching this file" view
- [ ] Add links from file history entries to mission detail and mission diff
- [ ] Add "open diff in Workspace" review flow

Implementation note:

- [ ] Current Armada captures diffs and can detect changed files during mission execution, but it does not appear to expose a durable file-history index yet

### 12. Legacy Dashboard Strategy

- [x] Decide whether the legacy dashboard gets:
  - no Workspace support
  - a simple link into React Workspace
  - a minimal read-only file browser

Recommended MVP decision:

- [x] React dashboard only
- [ ] Legacy dashboard may expose an "Open Workspace" link if needed, but should not duplicate the feature

### 13. Testing

#### Backend

- [ ] Unit tests for path normalization
- [ ] Unit tests for root escape prevention
- [x] Unit tests for hidden-path rules
- [ ] Unit tests for binary/text detection
- [x] Unit tests for save conflict handling
- [ ] Unit tests for search result shaping and limits

#### API / Automated

- [ ] Automated API tests for tree, file read, save, create directory, rename, delete
- [ ] Automated API tests for search
- [ ] Automated API tests for workspace changes/status
- [ ] Automated tests for missing or invalid working directories

#### React Dashboard

- [ ] Vitest/component tests for nav hierarchy
- [ ] Vitest/component tests for Workspace landing state
- [ ] Vitest/component tests for tree expansion and file open
- [ ] Vitest/component tests for dirty-state and save behavior
- [x] Vitest/unit tests for selection-to-planning and selection-to-dispatch draft generation

#### Manual Smoke Checklist

- [ ] Open vessel with valid working directory
- [ ] Open vessel without working directory
- [ ] Edit and save a text file
- [ ] Trigger save conflict
- [ ] Search by content
- [ ] Review changed files
- [ ] Launch planning from selected files
- [ ] Launch dispatch from selected files

### 14. Documentation

- [ ] Update `README.md` to introduce Workspace as a first-class feature
- [ ] Update `GETTING_STARTED.md` with Workspace entry points
- [ ] Update operator docs to explain the `WorkingDirectory` requirement
- [ ] Update REST API docs for the new Workspace endpoints
- [ ] Add screenshots or diagrams once the feature stabilizes

## Delivery Order

- [x] 1. Navigation hierarchy and Workspace routes
- [x] 2. Backend safety layer and REST contract
- [x] 3. Workspace shell and vessel picker
- [x] 4. File tree and read-only viewing
- [x] 5. Editing and save flow
- [x] 6. Search and changes
- [x] 7. Planning and Dispatch integration
- [ ] 8. Context/playbook curation helpers
- [ ] 9. Hardening, tests, and docs

## Practical Definition of Done

The MVP should be considered done when all of the following are true:

- [x] The React dashboard sidebar has a first-class `Workspace` entry under `Vessels`
- [x] A user can open `/workspace`, choose a vessel, and browse its `WorkingDirectory`
- [x] A user can open, edit, and save text files from the dashboard
- [x] Workspace blocks unsafe filesystem access outside the vessel root
- [x] Workspace shows current local changes for the vessel
- [x] Workspace can send selected files directly into Planning
- [x] Workspace can send selected files directly into Dispatch using Armada-compatible scoped-file wording
- [x] Workspace can link back to existing vessel pages and accept deep links from them
- [ ] Tests cover the safety-critical backend behaviors and the primary React flows
- [ ] Operator/docs surfaces explain how Workspace depends on `WorkingDirectory`

## Follow-On Opportunities After MVP

These are intentionally not required for the first ship, but they are the most promising expansions once the core Workspace exists:

- [ ] Read-only dock worktree explorer for active captain work
- [ ] Persisted mission changed-file index for file-level history
- [ ] "Recent missions touching this file" panel
- [ ] Inline mission diff review anchored to file paths
- [ ] Structured planning/dispatch selection contracts instead of draft-text prefill only
- [ ] Symbol outline, go-to-file, and richer code intelligence
- [ ] Binary previews for common image types
- [ ] Manual branch/commit helpers if Armada later wants tighter human-edit workflows
