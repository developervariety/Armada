---
type: "Project"
title: "C#: enums end in Enum and serialize as strings"
description: "Enum types are PascalCase with an Enum suffix (MissionStatusEnum, AgentRuntimeEnum) and decorated [JsonConverter(typeof(JsonStringEnumConverter))]; persist kinds as enum columns, not text prefixes."
timestamp: "2026-10-05T05:43:14.1995670Z"
created: "2026-10-05T05:43:13.8628610Z"
slug: "enum-naming-jsonconverter"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-substring-classification-rule-2", "no-sentinel-strings-rule-3", "one-class-per-file"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md): enums use PascalCase names with the Enum suffix and carry [JsonConverter(typeof(JsonStringEnumConverter))] so APIs emit names, not numbers. Examples: AgentRuntimeEnum, DeploymentModeEnum, LandingModeEnum, BranchCleanupPolicyEnum, McpToolErrorCodeEnum, MissionFailureKindEnum, ThemeModeEnum, GlyphModeEnum. Each enum in its own file. Fragility rules 2 and 3 add: classify with enums or typed results instead of strings, and persist kinds as enum columns rather than prefixes in free text (e.g. Mission.FailureKind, migration 77).
