---
type: "Reference"
title: "Token usage record (tku_): normalized model token accounting"
description: "TokenUsageRecord (tku_) stores Input/Output/Cached tokens for one mission run, Ask turn, or planning turn; estimated from text (~3.5 chars/token) and flagged Estimated when the runtime reports nothing."
timestamp: "2026-10-05T05:33:29.6454490Z"
created: "2026-10-05T05:33:29.3301530Z"
slug: "term-token-usage"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["rt-tokens-line"]
version: "1"
salience: "0.5"
---
Term: Token usage (src/Armada.Core/Models/TokenUsageRecord.cs, prefix tku_; TokenUsageCapture, TokenUsageSummaryBuilder). One record per unit of work (mission run, Ask Armada turn, planning turn), written when the work completes. Counts normalized across providers: InputTokens (prompt), OutputTokens (completion), CachedTokens (cache reads), with model breakdown.

Source priority: real usage from the runtime's structured stream (e.g. Claude Code result event) > an agent-reported `[ARMADA:TOKENS] input=N output=N cached=N` protocol line ([[rt-tokens-line]]) > estimate from text length at ~3.5 characters per token, with Estimated=true so UIs can flag it. Aggregated by REST /api/v1/token-usage (summaries/buckets) and MCP token_usage_summary; dashboard charts.
