---
type: "Reference"
title: "Prompt template: every agent prompt is an editable template (ptpl_)"
description: "PromptTemplate (ptpl_) holds named, user-editable prompt text with {Placeholder} parameters (persona.*, mission sections, import.fleet_categorization); resolution DB -> embedded default -> hardcoded fallback."
timestamp: "2026-10-05T05:29:58.2002770Z"
created: "2026-10-05T05:29:57.9454950Z"
slug: "term-prompt-template"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-persona", "arch-prompt-and-instructions-file", "term-message-templates"]
version: "1"
salience: "0.5"
---
Term: Prompt Template (src/Armada.Core/Models/PromptTemplate.cs, prefix ptpl_; service PromptTemplateService). Every prompt Armada sends is backed by a named template with {Placeholder} parameters such as {MissionId}, {VesselName}. Names include persona.<persona> (persona.worker, persona.architect, persona.judge...), mission instruction sections, and import.fleet_categorization (vessel import fleet recommendations).

Resolution order: database row (user customization) -> embedded default shipped with the code -> hardcoded fallback. PromptTemplateService.SeedDefaultsAsync seeds built-ins at startup; built-ins you never edited pick up new defaults on upgrade, edited ones keep yours. Admiral-owned output contracts (e.g. the Judge verdict contract, the categorization output contract) are appended in code so editing a template cannot break parsing. Manage via dashboard Configuration > Prompts, REST /api/v1/prompt-templates/{name} (PUT, POST .../reset), MCP get/update/reset_prompt_template, list_prompt_templates. Message templates for commits/PRs/merges are a separate settings-based mechanism ([[term-message-templates]]).
