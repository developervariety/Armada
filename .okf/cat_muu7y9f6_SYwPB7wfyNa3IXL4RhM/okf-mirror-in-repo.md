---
type: "Project"
title: "The Armada scope's OKF bundle is committed at .okf/ and agents maintain it by hand"
description: "The remote NotDory server (view.homedns.org:8720) can't see the checkout, so the scope's filesystemMirror is off; after each memory_upsert or memory_delete, update .okf/<categoryId>/<slug>.md and regenerate .okf/index.md (OKF v0.2)."
timestamp: "2026-10-14T20:58:31.8638260Z"
created: "2026-10-14T20:58:31.3004250Z"
slug: "okf-mirror-in-repo"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["living-plan-docs-update", "commit-message-format", "preserve-line-endings"]
version: "1"
salience: "0.5"
---
Since 2026-10-14 this scope's Open Knowledge Format bundle is committed at <repo>/.okf/. The NotDory server is remote (view.homedns.org:8720) and cannot see the local checkout, so the scope's filesystemMirror stays off (targetPath is recorded as /Users/joelchristner/Code/Armada for reference only). Agents keep .okf/ in sync by hand. The NotDory repo uses the same arrangement (its memory okf-mirror-in-repo).

Layout (matches NotDory's OkfDocument.Serialize and FilesystemMemoryStore.RegenerateOkfIndexAsync in ~/Code/NotDory/src/NotDory.Core/Stores/Filesystem/):
- .okf/<categoryId>/<slug>.md with LF endings, frontmatter in this order: type, title, description (= summary), tags (only if any), timestamp (= lastUpdateUtc), status: "deprecated" only when superseded, created, slug, category (= categoryId), links (only if any), supersededBy (the replacing memory's id) only when superseded, version, salience, then the body verbatim ending in a newline.
- Every scalar is double-quoted (newlines collapsed to spaces, backslash and quote escaped). Timestamps use .NET "O" format: 7 fractional digits and a Z.
- .okf/index.md has only okf_version: "0.2" as frontmatter, then one "# <categoryId>" section per category sorted case-insensitively, each listing "* [title](<categoryId>/<slug>.md) - <summary>" sorted by path, " (deprecated)" appended for superseded memories, then a blank line.

How to apply: simplest is to regenerate the whole bundle from a fresh export: memory_enumerate the scope with maxResults 1000, then rewrite .okf/ from the result with the rules above, deleting files for memories that no longer exist. Commit .okf/ changes with the code they describe.
