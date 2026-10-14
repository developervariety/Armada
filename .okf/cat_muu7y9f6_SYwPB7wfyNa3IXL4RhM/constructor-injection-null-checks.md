---
type: "Project"
title: "C#: constructor injection with ArgumentNullException null checks"
description: "Dependencies arrive through the constructor and are guarded with `_X = x ?? throw new ArgumentNullException(nameof(x));`."
timestamp: "2026-10-05T05:43:26.5249290Z"
created: "2026-10-05T05:43:26.2835590Z"
slug: "constructor-injection-null-checks"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["private-field-naming", "logging-header-pattern"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md Patterns): use constructor injection and null-check each dependency: `_Database = database ?? throw new ArgumentNullException(nameof(database)); _Logging = logging ?? throw new ArgumentNullException(nameof(logging));`. Public methods likewise throw ArgumentNullException for null required arguments (e.g. McpToolResultProbe documents <exception cref="ArgumentNullException">). Prefer specific exception types documented with <exception> tags. Services take interfaces where tests need fakes (IGitService, IHostCommandExecutor, IRuntimeToolDiscoverySource, IClock).
