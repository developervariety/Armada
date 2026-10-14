---
type: "Project"
title: "C#: private fields are _PascalCase"
description: "Private fields use a leading underscore plus PascalCase (_Database, _Logging, _Header, _StaleAfter); public members PascalCase."
timestamp: "2026-10-05T05:43:01.7910470Z"
created: "2026-10-05T05:43:01.4012400Z"
slug: "private-field-naming"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["logging-header-pattern", "constructor-injection-null-checks"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md Naming): private fields `_PascalCase`, e.g. `private readonly DatabaseDriver _Database;`, `private readonly LoggingModule _Logging;`, `private readonly string _Header = "[DefaultCredentialService] ";`, `private static readonly TimeSpan _StaleAfter`. Private static and const fields follow the same style (`private const int _RangeStart = 20000;`). Not camelCase, not _camelCase.
