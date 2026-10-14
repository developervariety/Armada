---
type: "Project"
title: "Git layout: bare clones, per-mission worktrees, branches, reclaim"
description: "Armada keeps one bare clone per vessel (LocalPath or ReposDirectory/<vessel>.git) and creates per-mission worktrees at DocksDirectory/<vessel>/<missionId> on branch armada/<captain>/<missionId>; per-repo semaphores serialize provisioning; Armada only deletes repo dirs strictly under its managed paths."
timestamp: "2026-10-05T05:40:08.3761400Z"
created: "2026-10-05T05:40:07.7740670Z"
slug: "arch-docks-and-bare-repos"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-dock", "arch-pipeline-stage-handoff", "term-landing-mode"]
version: "1"
salience: "0.5"
---
Code: src/Armada.Core/Services/DockService.cs (ProvisionAsync, ReclaimAsync, RepairAsync, UnstickAsync, DeleteAsync), GitService.cs (machine-format git: -z, porcelain=v2, --numstat, LC_ALL=C via GitProcessEnvironment), ManagedPaths.

Paths (ArmadaSettings, under DataDirectory ~/.armada or ARMADA_DATA_DIR): ReposDirectory (bare clones), DocksDirectory (worktrees; also _merge-queue/ temp worktrees), LogDirectory. Provision: per-normalized-repo SemaphoreSlim; ensure the bare clone exists (a corrupt dir is removed only if ManagedPaths.IsStrictlyUnder(ReposDirectory), else an error); git worktree add at DocksDirectory/<vesselName>/<missionId> (per-mission path avoids reuse races; legacy per-captain-name path) on the mission branch; record Dock (State, lease, HarborId when on a Harbor). The generated instructions file is marked skip-worktree / excluded so it never lands; attached playbooks are excluded via the dock's git exclude file.
Reclaim: after completion handoff (MissionService), on stall exhaustion/max runtime, by ReclaimOrphanedDocksAsync (>5 min, no working captain), or via operator unstick/purge. Downstream pipeline stages and review rework reuse the same branch; HardenStageBranchAsync force-advances it (git update-ref) to the prior stage's HEAD. Captain recovery repairs the worktree (git worktree repair). Branch cleanup after landing follows BranchCleanupPolicy.
