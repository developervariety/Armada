---
type: "Project"
title: "ASCII only in source, docs, commit messages, and agent output"
description: "No em dashes, smart quotes, or other extended Unicode in code, docs, commits, or captain output; use -- or plain hyphens; the TUI's ASCII glyph mode exists for terminals, not as an excuse for Unicode in source."
timestamp: "2026-10-05T05:43:54.8124900Z"
created: "2026-10-05T05:43:54.5474260Z"
slug: "ascii-only-text"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["docs-writing-style", "commit-message-format"]
version: "1"
salience: "0.5"
---
Rule: write ASCII only. Sources: the built-in mission rules prompt (src/Armada.Core/Services/PromptTemplateService.cs: "Do not use extended/Unicode characters (em dashes, smart quotes, etc.) -- use only ASCII characters in all output and commit messages"; persona template mission.rules "ASCII-only"), archive/HARBOR_RUNNER_PLAN.md "Output hygiene: ASCII only in source, docs, and commit messages", the writing requirements (no em-dashes; V1_READINESS asks to remove em-dashes repo-wide), and commit "R1c: ... ASCII test literal". Exceptions: localized catalog strings (armada.json) and test data that deliberately exercises non-ASCII (CJK width tests, C-quoted git paths) use \u escapes where possible. Applies to memories too.
