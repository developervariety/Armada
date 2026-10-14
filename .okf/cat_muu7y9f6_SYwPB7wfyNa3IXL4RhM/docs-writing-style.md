---
type: "Project"
title: "Documentation style: prose, plain ASCII, operator-focused, kept in sync with code"
description: "Docs (README, docs/*.md) are written as prose paragraphs plus tables, no em-dashes, second person for operators, exact commands and paths, defaults stated; update docs in the same change as behavior."
timestamp: "2026-10-05T05:44:23.0163030Z"
created: "2026-10-05T05:44:21.7025360Z"
slug: "docs-writing-style"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["ascii-only-text", "living-plan-docs-update", "security-review-living-document"]
version: "1"
salience: "0.5"
---
Per WRITING_DOCUMENTS.md (shared requirements, cited by TUI_APP_PLAN.md and CODEHUB_CAPABILITIES.md): docs are written as prose (not bullet dumps), contain no em-dashes, and use plain ASCII. House pattern seen in docs/OPERATIONS.md, UPGRADING.md, RELEASING.md, TUI.md: start with who the page is for, explain why before how, give exact commands in fenced blocks, state defaults and ranges in tables (Setting | Default | Range | Meaning), call out cautions explicitly, and cross-link sibling docs. Living docs must be updated in the same change: SECURITY_REVIEW.md (entry points), api-surface baseline, TESTING.md, TUI.md key map, CHANGELOG. Several docs drift (TESTING.md flags, README Harbor port, DOCKER.md sample); fix drift when you find it rather than copying it.
