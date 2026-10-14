---
type: "Project"
title: "Git is read with machine formats and LC_ALL=C (R1a)"
description: "UnifiedDiffParser drives boundary scan/auto-land; --name-status -z/--numstat -z, porcelain=v2 -z, show-ref --verify, GitCommandException; every git/gh launch uses LC_ALL=C."
timestamp: "2026-10-05T05:24:42.5046860Z"
created: "2026-10-05T05:24:42.1480360Z"
slug: "decision-git-machine-formats"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["pref-structural-not-textual-logic"]
version: "1"
salience: "0.5"
---
Implemented 2026-10-05 (R1a): DockBoundaryScanner and auto-land counts use Armada.Core UnifiedDiffParser (hunks by @@ ranges, C-quoted paths, deletes and renames) plus IGitService.GetBranchChangesAsync (diff --name-status -z --no-renames and --numstat -z) - closing a secret-scan bypass where an added line starting with "++ " looked like a header. GitService failures throw GitCommandException (exit code, stderr); message checks replaced by merge-base exit codes, rev-parse MERGE_HEAD checks, show-ref --verify for branch existence, symbolic-ref for default branch, status --porcelain=v2 -z --branch, gh pr view --json url, worktree list -z, iso-strict dates. GitProcessEnvironment sets LC_ALL=C/LANG=C on every git and gh launch in Core and Server.
