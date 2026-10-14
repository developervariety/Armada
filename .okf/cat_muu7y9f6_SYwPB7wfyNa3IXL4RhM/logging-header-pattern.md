---
type: "Project"
title: "Logging: SyslogLogging with a per-class _Header prefix"
description: "Each class logs through SyslogLogging's LoggingModule with `private string _Header = \"[ClassName] \";` prepended to every message; never log secrets or query strings."
timestamp: "2026-10-05T05:43:31.4200560Z"
created: "2026-10-05T05:43:31.1580320Z"
slug: "logging-header-pattern"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["owner-libraries-usage", "secrets-redaction-and-request-history"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md Patterns): logging uses SyslogLogging (owner's NuGet library; LoggingModule) and each class defines `private string _Header = "[ClassName] ";` (often readonly), used as `_Logging.Warn(_Header + "message")`. Examples: "[DefaultCredentialService] ", "[Program] ". Library code never writes to Console. Do not log secrets, tokens, or full query strings (F-15); redact with SecretRedactor where output may contain secrets.
