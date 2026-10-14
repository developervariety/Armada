---
type: "Project"
title: "Commit message format: \"Area: summary\", explanatory body, Co-Authored-By trailer"
description: "Subject \"Area: what changed\" (Tests:, TUI:, CI fixes:, R1c:, docs/HARBOR.md:), ASCII, no trailing period; body explains why, often as \"- \" bullets; end with the Co-Authored-By trailer; merges read \"Merge work/<branch>: summary\"."
timestamp: "2026-10-05T05:44:04.1704660Z"
created: "2026-10-05T05:44:03.8868510Z"
slug: "commit-message-format"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["ascii-only-text", "changelog-unreleased-entries", "pref-commit-and-push-to-main", "pref-read-results-before-commit"]
version: "1"
salience: "0.5"
---
Observed in git log (2026-10): subjects are short, ASCII, sentence-style with a leading area and colon, no trailing period: "Tests: portable file names and line endings for the Windows CI jobs", "TUI: hub tabs keep focus across Left/Right; clear page size controls", "R2 typed not-found: KeyNotFoundException, route and MCP mapping by type", "docs/HARBOR.md: settings keys are PascalCase; link URL is on the Admiral port 7890", "Version 1.0.0; ASCII logo on the TUI login screen". Bodies explain the root cause and the change, wrapped near 72-80 columns, frequently as "- " bullet lists naming classes and tests. Agent commits end with a blank line and the trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" (all recent agent commits; use the trailer your session specifies). Feature work lands on work/<topic> branches merged with "Merge work/<topic>: <summary> (<workstream ids>)". Commit doc and plan updates (CHANGELOG, FRAGILITY_REMEDIATION.md, TUI_APP_PLAN.md) with or right after the change. Commit or push only when asked; never push failing merges.
