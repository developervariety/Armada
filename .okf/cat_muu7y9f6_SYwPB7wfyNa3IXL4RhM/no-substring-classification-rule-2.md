---
type: "Project"
title: "Fragility rule 2: never classify by substring or regex over text that has a structured form"
description: "Return typed results (enums, error codes, result classes) and persist kinds as enum columns; e.g. runtime failures come from RuntimeExitInfo and structured events, not log lines containing \"403\"."
timestamp: "2026-10-05T05:44:56.6449940Z"
created: "2026-10-05T05:44:55.9005220Z"
slug: "no-substring-classification-rule-2"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-branching-on-exception-text-rule-1", "no-sentinel-strings-rule-3", "decision-structured-judge-and-failure-kind", "enum-naming-jsonconverter"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 2. Motivating bugs: quarantining a captain because a log line contained "403"; MissionFailureClassifier re-parsing FailureReason prefixes; judge verdict scraped from free text. Fixes (R1b, R3): typed RuntimeExitInfo from IAgentRuntime.OnProviderError, persisted Mission.FailureKind (MissionFailureKindEnum, migration 77), JudgeVerdictParser reading only the structured [ARMADA:VERDICT] protocol line, typed stream events in Armada.Core.Protocol for Claude stream-json, Codex --json, Mux, OpenCode. When something must be classified, give it a structured form first (enum, code, typed result, machine-readable output) and branch on that. Only genuinely unstructured human text may be searched, and never for security or state decisions.
