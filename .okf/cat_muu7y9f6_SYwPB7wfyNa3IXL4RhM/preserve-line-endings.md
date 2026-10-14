---
type: "Project"
title: "Preserve each file's existing line endings (some files are CRLF)"
description: "Several repo files use CRLF (e.g. dashboard App.css); edits via scripts or tools must keep the file's original endings to avoid whole-file churn; commit \"Restore CRLF line endings in files edited for R1b\" fixed such churn."
timestamp: "2026-10-05T05:44:34.8777530Z"
created: "2026-10-05T05:44:34.6400690Z"
slug: "preserve-line-endings"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["test-git-environment-pinning", "dashboard-table-cell-sizing", "lesson-parallel-agent-merge-pitfalls"]
version: "1"
salience: "0.5"
---
The repository mixes line endings: some files (for example src/Armada.Dashboard/src/App.css and various C# files) use CRLF. Editing them with tools or sed that normalize to LF rewrites every line and buries the real diff; the R1b work needed a follow-up commit "Restore CRLF line endings in files edited for R1b". Before editing, check the file's endings (file <path> or git diff --stat after editing) and keep them. Tests are pinned to core.autocrlf=false/core.eol=lf (TestGitEnvironment) and the dashboard dist drift check runs on Linux so committed endings must match a Linux build. New test literals should not depend on the platform newline unless the product documents it.
