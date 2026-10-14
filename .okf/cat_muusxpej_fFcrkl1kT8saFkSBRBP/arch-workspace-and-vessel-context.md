---
type: "Project"
title: "Workspace service and vessel model-context building"
description: "WorkspaceService gives safe browse/edit/search/exec over a vessel's WorkingDirectory (/api/v1/workspace/vessels/{id}/...); VesselContextService launches a captain in a fresh worktree with the vessel.build_context template to write or refine Vessel.ModelContext."
timestamp: "2026-10-05T05:44:17.8127500Z"
created: "2026-10-05T05:44:16.0937090Z"
slug: "arch-workspace-and-vessel-context"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-vessel", "arch-prompt-and-instructions-file"]
version: "1"
salience: "0.5"
---
WorkspaceService (src/Armada.Core/Services/WorkspaceService.cs, IWorkspaceService; routes src/Armada.Server/Routes/WorkspaceRoutes.cs): tree, file (read/save with conflict detection -> WorkspaceConflictException), entry, directory, rename, search (WorkspaceSearchResult with Truncated), changes and diff (git status --porcelain=v2 -z --branch parsed structurally), status, exec (runs a command in the working directory; tenant admin; recorded as audit.command). Paths are contained with PathContainment (Path.GetRelativePath) so requests cannot escape the vessel directory. Backs the dashboard Vessels > Workspace tab and TUI. Not exposed over MCP.

VesselContextService (src/Armada.Server/VesselContextService.cs): POST /api/v1/vessels/{id}/build-context (VesselBuildContextRequest) -> launches the chosen captain in a fresh worktree of the vessel repo with the editable vessel.build_context prompt template plus operator guidance; if ModelContext exists it asks for a refinement; saves the result to Vessel.ModelContext (EnableModelContext). Errors: 501 when unavailable, 504 on timeout (sent with Error BadRequest). Agents can also update context via MCP update_vessel_context / PATCH /api/v1/vessels/{id}/context.
