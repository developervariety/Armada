---
type: "Project"
title: "C#: Async suffix, CancellationToken token = default, ConfigureAwait(false) in libraries"
description: "Async methods end in Async and take `CancellationToken token = default`; library code (Core, Runtimes, Client) awaits with .ConfigureAwait(false); no Console.WriteLine in library code."
timestamp: "2026-10-05T05:43:06.9155100Z"
created: "2026-10-05T05:43:06.4985240Z"
slug: "async-naming-cancellation-configureawait"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["no-var-keyword", "logging-header-pattern"]
version: "1"
salience: "0.5"
---
Rule (CLAUDE.md): async methods are suffixed Async and include `CancellationToken token = default` as the last parameter; use `.ConfigureAwait(false)` on awaits in library code (Armada.Core, Armada.Runtimes; the TUI plan extends this to Armada.Client). Example: `public async Task<List<Fleet>> EnumerateAsync(CancellationToken token = default)` with `await ... .ConfigureAwait(false)`. Pass the token through to downstream calls. Library code must not write to Console (use the logger). Server and test code commonly also use ConfigureAwait(false).
