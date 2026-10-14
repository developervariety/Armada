---
type: "Reference"
title: "Message templates: commit trailers, PR body, merge commit text"
description: "settings.messageTemplates renders {MissionId}, {VoyageId}, {CaptainId}, {VesselId}... into commit-trailer instructions (best effort, via prompt), PR descriptions and local merge commits (deterministic, server-side)."
timestamp: "2026-10-05T05:33:35.8326270Z"
created: "2026-10-05T05:33:35.5736140Z"
slug: "term-message-templates"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-prompt-template", "arch-landing-handler"]
version: "1"
salience: "0.5"
---
Term: Message templates (settings MessageTemplateSettings: EnableCommitMetadata, EnablePrMetadata, CommitMessageTemplate, PrDescriptionTemplate, MergeCommitTemplate; MessageTemplateService; docs/MESSAGE_TEMPLATES.md). Distinct from prompt templates.

Placeholders: {MissionId}, {MissionTitle}, {VoyageId}, {VoyageTitle}, {CaptainId}, {CaptainName}, {VesselId}, {VesselName}, {FleetId}, {DockId}, {BranchName}, {Timestamp}; unresolvable ones become empty, unknown ones stay literal.
Commit metadata: default trailers Armada-Mission-Id / Armada-Voyage-Id / Armada-Captain-Id / Armada-Vessel-Id are rendered into the captain's launch prompt as instructions (AgentLifecycleHandler appends RenderCommitInstructionsAsync output), so compliance is best effort. PR descriptions (server appends to the PR body) and local merge commits (passed to git merge) are deterministic. Trailers are queryable with git log --format='%(trailers)'.
