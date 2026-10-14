---
type: "Reference"
title: "Project profile (ppf_) and Skill (skl_)"
description: "ProjectProfile (ppf_) aggregates a project's pipeline, workflow profile, per-persona prompt overrides, and attached skills, resolved Global -> Fleet -> Vessel; Skill (skl_) is a small reusable instruction snippet injected into mission prompts."
timestamp: "2026-10-05T05:32:05.7130150Z"
created: "2026-10-05T05:32:05.4550980Z"
slug: "term-project-profile-and-skill"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-workflow-profile", "term-playbook", "arch-prompt-and-instructions-file"]
version: "1"
salience: "0.5"
---
Terms:
- ProjectProfile (src/Armada.Core/Models/ProjectProfile.cs, prefix ppf_; ProjectProfileService): Scope (ProjectProfileScopeEnum Global/Fleet/Vessel) + FleetId/VesselId, IsDefault, OwnershipScope, DefaultPipelineId, WorkflowProfileId, PersonaOverrides (PersonaOverride: per-persona prompt customizations), Skills (list of skill ids). It references other entities rather than copying them. MissionService.ResolvePersonaOverrideAsync and ResolveSkillsMarkdownAsync use the resolved profile when building mission instructions. REST /api/v1/project-profiles (validate, resolve/vessels/{vesselId}, {id}/persona-preview/{persona}).
- Skill (src/Armada.Core/Models/Skill.cs, prefix skl_): tenant-scoped, categorized, editable instruction snippet for cross-cutting habits ("write ADRs", "TDD", "conventional commits"); lighter than a playbook. REST /api/v1/skills. Both are Category B config with TenantWide/UserSpecific ownership.
