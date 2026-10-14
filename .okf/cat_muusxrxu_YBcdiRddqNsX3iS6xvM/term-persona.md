---
type: "Reference"
title: "Persona: a named agent role backed by a prompt template (prs_)"
description: "Persona (prs_) = role like Worker, Architect, Judge; built-ins: Worker, Architect, Product Manager, Usability Engineer, Test Engineer, Linter, Judge, Recorder; may set DefaultCaptainId."
timestamp: "2026-10-05T05:29:43.8304110Z"
created: "2026-10-05T05:29:43.4464940Z"
slug: "term-persona"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-pipeline", "term-prompt-template", "arch-captain-routing-persona-tier", "rt-verdict-line"]
version: "1"
salience: "0.5"
---
Term: Persona (src/Armada.Core/Models/Persona.cs, prefix prs_). The role a captain plays in a mission. Each persona references a prompt template (persona.<name>, e.g. persona.worker, persona.judge) and has Scope (TenantWide/UserSpecific), IsBuiltIn, Active, DefaultCaptainId (per-step captain selection, migration 55).

Built-ins (PersonaCatalog constants, seeded by PersonaSeedService): Worker (implements), Architect (plans, emits armada-plan JSON / [ARMADA:MISSION]), Product Manager, Usability Engineer, Test Engineer (legacy name "TestEngineer" still matched), Linter, Judge (reviews, emits [ARMADA:VERDICT]), Recorder (distills memories, non-gating final stage). Built-ins cannot be deleted; custom personas are created with create_persona after creating a prompt template. Mission.Persona carries the stage persona; captains filter with AllowedPersonas / PreferredPersona. Every working persona is told to recall memory via search_memory before starting. Example: `create_persona name=SecurityAuditor promptTemplateName=persona.security_auditor`.
