---
type: "Reference"
title: "Orchestrator guides: using Armada from Claude Code, Codex, Gemini, Cursor, Mux, OpenCode"
description: "docs/*_AS_ORCHESTRATOR.md cover client setup; docs/INSTRUCTIONS_FOR_*.md are paste-in system prompts (Research -> Decompose -> Dispatch -> Monitor -> Adapt; one file per mission; small enumerate pages); `armada mcp install` writes a managed block (<!-- armada:mcp:begin/end -->) into AGENTS.md-style files."
timestamp: "2026-10-05T05:51:30.7309450Z"
created: "2026-10-05T05:51:30.1088410Z"
slug: "api-orchestrator-instructions"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-mcp-tool-families", "api-mcp-enumerate", "rt-mcp-config-generation"]
version: "1"
salience: "0.5"
---
Files: docs/CLAUDE_CODE_AS_ORCHESTRATOR.md, CODEX_AS_ORCHESTRATOR.md, GEMINI_AS_ORCHESTRATOR.md, CURSOR_AS_ORCHESTRATOR.md, MUX_AS_ORCHESTRATOR.md, OPENCODE_AS_ORCHESTRATOR.md (setup, launch, permission mode, verify with /mcp, project-scoped orchestration, manual config appendix); docs/INSTRUCTIONS_FOR_CLAUDE_CODE.md, _CODEX, _GEMINI, _CURSOR, _MUX, _OPENCODE (content to paste into CLAUDE.md / system prompt). The repo's own AGENTS.md is such a managed block (written by Armada.Helm McpConfigHelper / ManagedBlockEditor between <!-- armada:mcp:begin --> and <!-- armada:mcp:end -->).
Guidance in the instructions: workflow Research -> Decompose -> Dispatch -> Monitor -> Adapt; start with status, use enumerate with pageSize 10-25 and filters, include flags only when needed, voyage_status/mission_status, get_mission_log/get_captain_log/get_mission_diff for failures; merge-conflict rules (one file one mission; monolithic files -> single mission or sequential voyages; never dispatch overlapping voyages touching the same files; scope files explicitly in descriptions; watch lock files, barrels, generated files); 2-6 missions per voyage typical; confirm destructive actions; emergency controls stop_captain, stop_all, cancel_voyage, cancel_mission; captains must commit but never push, switch branches, or edit git config (Armada lands the work). ID cheat sheet flt_ vsl_ cpt_ msn_ vyg_ dck_ sig_ mrg_.
