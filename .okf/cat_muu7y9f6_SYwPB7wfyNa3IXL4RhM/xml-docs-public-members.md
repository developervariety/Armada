---
type: "Project"
title: "C#: XML <summary> docs on every public member"
description: "All public types, properties, methods, constructors, and enum values need /// <summary> (plus param/returns/exception tags); GenerateDocumentationFile is on, so a missing doc becomes a warning and fails CI's -warnaserror."
timestamp: "2026-10-05T05:42:47.1280690Z"
created: "2026-10-05T05:42:41.9436270Z"
slug: "xml-docs-public-members"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["ci-warnings-as-errors", "build-solution-command"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md): every public member has a <summary> XML doc comment; add <param>, <returns>, and <exception> tags (the TUI plan's CODE_STYLE list asks for specific exceptions with <exception> tags). src/Directory.Build.props sets GenerateDocumentationFile true, and CS1591-style missing-doc warnings are not in NoWarn, so CI's `-warnaserror` build fails on an undocumented public member. Use <inheritdoc /> for interface implementations. Tests (Test.Shared) follow the same rule.
