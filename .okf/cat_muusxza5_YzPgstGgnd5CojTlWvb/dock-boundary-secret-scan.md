---
type: "Reference"
title: "Dock boundary scan: secrets, protected paths, private identifiers before landing"
description: "DockBoundaryScanner inspects a mission's diff and changed paths before landing; findings carry rule id, path, and line, never secret bytes; diffs parsed structurally by UnifiedDiffParser and git -z machine formats."
timestamp: "2026-10-05T05:35:14.0362330Z"
created: "2026-10-05T05:35:13.6977470Z"
slug: "dock-boundary-secret-scan"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["decision-git-machine-formats", "external-process-machine-formats-rule-4", "secrets-redaction-and-request-history"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/DockBoundaryScanner.cs: pure, side-effect-free scan of a mission's unified diff plus changed-path set against a policy, returning structured findings for secrets a captain introduced, changes to protected paths, and private-identifier leaks into public repos. Findings never carry the secret bytes, only rule id, path, and (for secrets) matched line number. An empty list means clean. Fragility R1a hardened it: UnifiedDiffParser consumes hunks by their @@ ranges, so an added line starting with "++ " is no longer skipped (it was a secret-scan bypass), C-quoted non-ASCII paths, deletes, and both sides of renames reach protected-path and auto-land rules, and when the dock worktree exists paths and counts come from `git diff --name-status -z --no-renames` and `--numstat -z` (IGitService.GetBranchChangesAsync); git runs with LC_ALL=C. Auto-land limits use the same counts. Unit-tested against diff fixtures.
