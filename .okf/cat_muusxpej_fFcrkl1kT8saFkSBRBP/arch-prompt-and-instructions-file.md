---
type: "Project"
title: "Mission prompt and per-runtime instructions file (MissionPromptBuilder)"
description: "Each dock gets a generated instructions file (CLAUDE.md, CODEX.md, CURSOR.md, GEMINI.md, MUX.md; others CLAUDE.md) built from templates: persona prompt, project profile overrides and skills, vessel context/style guide/model context, playbooks, mode/judge/architect contracts; marked skip-worktree so it never lands."
timestamp: "2026-10-05T05:41:23.1317190Z"
created: "2026-10-05T05:41:22.7000810Z"
slug: "arch-prompt-and-instructions-file"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-prompt-template", "term-playbook", "term-project-profile-and-skill", "arch-mission-assignment"]
version: "1"
salience: "0.5"
---
Code: src/Armada.Core/Services/MissionPromptBuilder.cs (shared by CLAUDE.md generation and the launch prompt; GetInstructionsFileName maps ClaudeCode->CLAUDE.md, Codex->CODEX.md, Cursor->CURSOR.md, Gemini->GEMINI.md, Mux->MUX.md, default CLAUDE.md), MissionService.GenerateClaudeMdAsync, EnsureMissionInstructionsPresentAsync, HideTrackedInstructionsFileAsync.

Content: template parameters from mission/vessel/captain/dock; persona prompt "persona.<persona>" (PromptTemplateService: DB -> embedded default -> hardcoded fallback) possibly overridden by the resolved ProjectProfile PersonaOverrides; skills markdown from the project profile; sections via ResolveSectionAsync (rules, context conservation, memory recall via search_memory, papercut and token reporting); vessel ProjectContext, StyleGuide, ModelContext; selected playbooks rendered per delivery mode (inline, reference, attached file); read-only mode contract (MissionModeContract), Judge contract, Architect output contract; review feedback and "Prior Stage Output" from handoffs live in the mission description. If the repo already tracks a file of that name, the dock marks it skip-worktree so the generated content is never committed. The launch prompt (stdin) is built by BuildLaunchPromptAsync plus commit-trailer instructions.
