---
type: "Reference"
title: "API surface baseline: generate-api-surface.sh and E2E.ApiContract"
description: "E2E.ApiContract fails on removals or incompatible changes vs docs/api-surface-1.0.json; after an intended addition run scripts/common/generate-api-surface.sh and commit both files."
timestamp: "2026-10-05T05:27:32.3053400Z"
created: "2026-10-05T05:27:31.9071830Z"
slug: "api-surface-generator-and-contract-test"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-console-runner-touchstone", "tui-parity-generator-and-suite"]
version: "1"
salience: "0.5"
---
E2E.ApiContract compares the live public surface (REST routes with OpenAPI metadata and declared authorization, MCP tools with input schemas, WebSocket commands and events, Helm CLI command model, ArmadaSettings keys) to the frozen baseline docs/api-surface-1.0.json and fails on removals and incompatible changes, printing a diff.

Check: `dotnet run --project src/Test.Automated --framework net10.0 -- --suites E2E.ApiContract`
Regenerate after an intended addition: `scripts/common/generate-api-surface.sh [-f net10.0] [--out <dir>]` (Windows: scripts/windows/generate-api-surface.bat). It runs Test.Automated with `--generate-api-surface <dir>`, which boots a throwaway in-process Admiral (temp data dir, random ports, never ~/.armada) and rewrites api-surface-1.0.json and API_SURFACE_1.0.md in docs/. Commit both with the change. Only regenerate for additions, or for a breaking change in a major release (docs/COMPATIBILITY.md). Related: Client.RouteSurface checks Armada.Client against the same JSON.
