---
type: "Project"
title: "`armada go` splits missions only with explicit --task options"
description: "Prompts are never auto-split on \"1.\" or \";\" anymore; use repeatable --task for multiple missions (R1c, 2026-10-05)."
timestamp: "2026-10-05T05:24:47.4281300Z"
created: "2026-10-05T05:24:46.9312160Z"
slug: "decision-armada-go-explicit-tasks"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
version: "1"
salience: "0.5"
---
Before 2026-10-05 GoCommand.DetectMultipleTasks split free-text prompts into several missions on numbered lists and semicolons (code snippets and sentences like "Upgrade to .NET 8. Then bump to 9." were split). R1c replaced it: a prompt is always one mission; multiple missions come from a repeatable --task option. README and the API surface (CLI) were updated. Also in R1c: Helm --runtime values parse to AgentRuntimeEnum via AgentRuntimeParser (aliases claude, opencode, api...), unknown values are errors (before, `--runtime opencode` silently created a Claude Code captain).
