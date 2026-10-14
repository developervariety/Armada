---
type: "Feedback"
title: "String matching on text or exception messages is unacceptable"
description: "Owner rejects decisions based on substring/regex over text (e.g. StartsWith(\"EXCEPTION\")); use typed exceptions, enums, error codes, deserialization."
timestamp: "2026-10-05T05:22:55.3877320Z"
created: "2026-10-05T05:22:55.0083990Z"
slug: "pref-structural-not-textual-logic"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["decision-mcp-typed-error-codes", "decision-typed-not-found", "open-fragility-remediation"]
version: "1"
salience: "0.5"
---
On 2026-10-05 the owner called a test check `if (text.StartsWith("EXCEPTION", ...)) return true;` "frail/fragile and simply unacceptable" and asked for a codebase-wide scan for similar situations. The scan found about 430 sites; the resulting rules are in FRAGILITY_REMEDIATION.md (rules 1-7): never branch on exception message text (throw/catch typed exceptions, KeyNotFoundException for missing entities); never classify by substring/regex where a structured form exists or can be created (return enums/error codes, persist kinds as columns); no sentinel strings; read external processes with machine formats (git -z, porcelain=v2, --numstat, --json, exit codes) and LC_ALL=C; deserialize JSON into typed classes; path containment via Path.GetRelativePath; tests assert on deserialized models/status codes/error codes, select stub requests by route, and never treat a failed call as a denial. Apply this to every new change and in code review.
