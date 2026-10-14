---
type: "Reference"
title: "[ARMADA:PAPERCUT] line format and tolerant parsing"
description: "Preferred form: `[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"...\",\"detail\":\"...\",\"path\":\"...\"}`; PapercutParser also accepts pipe-delimited and bare text so slightly off-format reports still count."
timestamp: "2026-10-05T05:37:07.7124710Z"
created: "2026-10-05T05:37:07.5136260Z"
slug: "rt-papercut-line"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["term-papercut", "rt-armada-protocol-lines"]
version: "1"
salience: "0.5"
---
Parser: src/Armada.Core/Services/PapercutParser.cs (TryParseValue -> Papercut). Preferred: one line of JSON after the marker with category (PapercutCategoryEnum), severity (Low/Medium/High), title, detail, path. Also accepted: a pipe-delimited form and bare text, on purpose: captains run on six runtimes and many model families, and rejecting a slightly malformed report teaches the captain that reporting does not work.

Handling: ProgressParser gives PAPERCUT priority when a record contains several markers; AgentLifecycleHandler.HandlePapercutSignal stores it asynchronously as an ArmadaEvent of type papercut with mission/captain/vessel/voyage/runtime context from the Admiral (never from the captain); failures never propagate into the mission's output path. Judge missions are instructed not to file papercuts (they report through the verdict). Read back with MCP papercut_summary. See [[term-papercut]].
