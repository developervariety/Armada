---
type: "Reference"
title: "Dock: a git worktree for one mission (dck_) and its states"
description: "Dock (dck_) = git worktree under DocksDirectory/<vessel>/<missionId> on a bare clone; states Available, Provisioning, Leased, Reclaiming, Failed; pinned to one host (Harbor affinity)."
timestamp: "2026-10-05T05:29:14.8258310Z"
created: "2026-10-05T05:29:14.4889620Z"
slug: "term-dock"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-docks-and-bare-repos", "term-harbor", "term-mission"]
version: "1"
salience: "0.5"
---
Term: Dock (src/Armada.Core/Models/Dock.cs, prefix dck_). The isolated git worktree a captain works in, created by DockService.ProvisionAsync from the vessel's bare clone (vessel.LocalPath or ReposDirectory/<vessel>.git) at DocksDirectory/<vesselName>/<missionId> (per-mission path; older per-captain-name path as fallback), on the mission branch armada/<captain>/<missionId>.

Fields: VesselId, CaptainId, HarborId (host that owns the worktree), WorktreePath, BranchName, Active, State (DockStateEnum: Available, Provisioning, Leased, Reclaiming, Failed), LeaseExpiresUtc, OwnerToken, GitAnchorsJson. Lease fields came with migration 44 (v0.9 reliability work).
A dock exists on exactly one host's filesystem, so a mission that owns a dock is pinned to that Harbor. Operator tools: repair (git worktree repair), unstick (release the holding captain to Idle, then reclaim), purge, delete (blocked while an active mission uses it); REST /api/v1/docks/{id}/repair|unstick, MCP repair_dock/unstick_dock. The health check reclaims orphaned docks older than ~5 minutes with no working captain. See [[arch-docks-and-bare-repos]].
