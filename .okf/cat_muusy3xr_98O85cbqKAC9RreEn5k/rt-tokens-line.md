---
type: "Reference"
title: "[ARMADA:TOKENS] line: agent-reported token counts"
description: "`[ARMADA:TOKENS] input=1234 output=567 cached=0` as a whole line outside fenced code; last such line wins; preferred over text-length estimates when the runtime reports no real usage."
timestamp: "2026-10-05T05:37:49.4135970Z"
created: "2026-10-05T05:37:13.5171100Z"
slug: "rt-tokens-line"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["term-token-usage", "rt-armada-protocol-lines"]
version: "1"
salience: "0.5"
---
Code: src/Armada.Core/Services/TokenUsageCapture.cs, regex ^\[ARMADA:TOKENS\](?<fields>(?:[ \t]+[A-Za-z]+=\d+)+)[ \t]*$ (case-insensitive). The mission/captain instructions ask runtimes to emit this line. It counts only as a whole line of key=value fields, outside fenced code blocks; the last such line wins; a marker quoted in prose or an echoed prompt legend ("- `[ARMADA:TOKENS] input=1234 ...`") is not a report.

Priority when recording a TokenUsageRecord for a mission: real usage from the runtime's structured stream > [ARMADA:TOKENS] counts > estimate from text length (3.5 chars/token, Estimated=true). Mission output for plain-text runtimes is human-readable without a provider usage report, which is why the marker exists. See [[term-token-usage]].
