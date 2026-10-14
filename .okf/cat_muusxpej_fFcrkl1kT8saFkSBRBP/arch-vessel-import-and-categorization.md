---
type: "Project"
title: "Vessel import pipeline: discovery, import, captain fleet categorization"
description: "VesselDiscoveryService scans allowed roots for .git dirs (breadth-first, MaxDepth 6, 5000 cap) into a batch; VesselImportService creates vessels (inline up to 25, else a job); FleetCategorizationService runs a captain on a generated REPOSITORIES.md and parses fleet-recommendations.json."
timestamp: "2026-10-05T05:42:49.0532080Z"
created: "2026-10-05T05:42:48.4250250Z"
slug: "arch-vessel-import-and-categorization"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-vessel-import-batch", "term-job", "term-fleet"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Core/Services/VesselDiscoveryService.cs, VesselImportService.cs, VesselImportPaths.cs, VesselMatchIndex.cs, FleetCategorizationService.cs, CaptainPromptRunner (Armada.Server); settings Import (VesselImportSettings): AllowedRoots (empty = user profile dir), MaxDepth 6, ExcludedDirectoryNames, InlineBatchLimit 25, CategorizationTimeoutMinutes 20.

Discover: every input path normalized (., .., ~, symlinks resolved) and required inside AllowedRoots (else 403 PathNotAllowed / VesselImportPathNotAllowedException); breadth-first scan, skips excluded and dot-prefixed names and symlinks, never descends into a repo; .git directory = repo, .git file = Worktree; Armada's own Repos/Docks/Data dirs = ArmadaManaged; origin via git remote get-url, default branch via origin/HEAD then current then main; VesselMatchIndex marks AlreadyOnboarded by WorkingDirectory or normalized RepoUrl (scheme, credentials, port, host case, trailing / and .git ignored); unique ProposedName with -2, -3; stops at 5000 candidates (Truncated). Optional background (job kind VesselDiscovery).
Import: creates vessels with WorkingDirectory set, LocalPath unset; larger than InlineBatchLimit -> job VesselImport.
Categorization: collect selected vessels, atomically reserve the captain Idle -> Analyzing, write <DataDirectory>/fleet-categorization/<jobId>/REPOSITORIES.md (ids, names, paths, remotes, languages, manifests, first 60 README lines), run once on the Admiral host with the import.fleet_categorization template + Admiral-owned output contract, parse/validate fleet-recommendations.json (drop unknown ids/duplicates, merge same-name fleets, Uncategorized bucket), optional ApplyAutomatically; apply reuses tenant fleets by name. Restart marks in-flight batches Failed (RecoverAsync).
