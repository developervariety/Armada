---
type: "Project"
title: "Fragility rule 4: read external processes through machine formats with LC_ALL=C"
description: "Use git -z, --porcelain=v2, --numstat, --name-status, --json, show-ref --verify, symbolic-ref, and exit codes; launch with LC_ALL=C (and DOTNET_CLI_UI_LANGUAGE=en) where text must be read."
timestamp: "2026-10-05T05:45:09.5249220Z"
created: "2026-10-05T05:45:07.5132610Z"
slug: "external-process-machine-formats-rule-4"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["decision-git-machine-formats", "dock-boundary-secret-scan", "no-branching-on-exception-text-rule-1"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 4 (R1a done 2026-10-04). Examples: branch existence via `git show-ref --verify`, default branch via `git symbolic-ref` (keeps slashes like release/v2), workspace status `git status --porcelain=v2 -z --branch`, diffs `git diff --name-status -z --no-renames` and `--numstat -z`, worktree list -z, iso-strict dates, PR URL via `gh pr view --json url`, npm errors from error.code (ENOLOCK), dotnet restore-required from a missing obj/project.assets.json with DOTNET_CLI_UI_LANGUAGE=en. Every git and gh launch in Core and Server runs with LC_ALL=C / LANG=C and UTF-8 decoding. GitService failures throw GitCommandException with exit code and stderr; decisions use exit codes and pre-checks, never git's wording. Put `--` before user-supplied git arguments (O-07).
