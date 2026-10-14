---
type: "Project"
title: "C#: never use var; always explicit types"
description: "Write List<Fleet> fleets = ... not var fleets = ...; applies to all C# in the repo, including tests and loops."
timestamp: "2026-10-05T05:42:25.8913620Z"
created: "2026-10-05T05:42:25.7136810Z"
slug: "no-var-keyword"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-tuples", "async-naming-cancellation-configureawait"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md Naming and Language Restrictions): no `var` keyword anywhere. Example: `List<Fleet> fleets = await _Database.Fleets.EnumerateAsync(token).ConfigureAwait(false);`, `foreach (Mission mission in missions)`, `out int parsed`. Exception: none (also applies to tests, scripts compiled as C#, and pattern variables should use explicit types). Enforced by review (no analyzer); the TUI plan's CODE_STYLE compliance list repeats it.
