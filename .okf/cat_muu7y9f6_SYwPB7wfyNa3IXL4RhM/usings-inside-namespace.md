---
type: "Project"
title: "C#: using directives inside the namespace block, System first"
description: "Place usings inside `namespace X { ... }`, ordered System.* first, then third-party (SyslogLogging, Watson, Voltaic, TUIKit, Touchstone), then project namespaces (Armada.*, Test.*)."
timestamp: "2026-10-05T05:42:57.3757550Z"
created: "2026-10-05T05:42:56.9930910Z"
slug: "usings-inside-namespace"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["region-blocks"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md): `using` statements go inside the namespace block, not above it; order: System namespaces first, then third-party, then project namespaces. Example (E2EServerFixture.cs): namespace Test.Shared.Infrastructure { using System; using System.Collections.Generic; ... using Armada.Core.Enums; using Armada.Core.Settings; using Armada.Runtimes; using Armada.Server; using SyslogLogging; ... } (block-scoped namespaces, not file-scoped). Most projects have ImplicitUsings enabled (Directory.Build.props) but files still list their usings; Armada.Tui disables ImplicitUsings.
