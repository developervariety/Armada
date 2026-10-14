---
type: "Project"
title: "C#: #region blocks in fixed order"
description: "Organize classes with #region Public-Members, Private-Members, Constructors-and-Factories, Public-Methods, Private-Methods (omit empty ones)."
timestamp: "2026-10-05T05:42:55.0341550Z"
created: "2026-10-05T05:42:51.6936390Z"
slug: "region-blocks"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["one-class-per-file", "usings-inside-namespace"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md): use #region blocks named Public-Members, Private-Members, Constructors-and-Factories, Public-Methods, Private-Methods, in that order. Example: TestTemp.cs has Public-Members (Prefix const), Private-Members (_StaleAfter, _Tracked), Public-Methods (Initialize, NewDirectory...), Private-Methods (IsolateUserProfile, Sweep...). Some files also use Private-Static (ArmadaSettings). Omit regions that would be empty. Enforced by review.
