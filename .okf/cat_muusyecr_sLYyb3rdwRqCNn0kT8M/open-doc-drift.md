---
type: "Project"
title: "Docs that disagree with the code (found 2026-10-05)"
description: "TESTING.md flags, OPERATIONS.md Docker settings path, SCHEDULING.md heartbeat, README FullPipeline stages, TUNNEL_PROTOCOL.md methods, Vite dev proxy port; fold into the W7.1 docs audit."
timestamp: "2026-10-05T05:52:45.5688030Z"
created: "2026-10-05T05:52:45.1540200Z"
slug: "open-doc-drift"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["open-v1-readiness-remaining"]
version: "1"
salience: "0.5"
---
Found by the memory-building agents on 2026-10-05 (memories record what the code does): docs/TESTING.md documents --suite, --type -h -u -w -d and registering suites in Program.cs, but the runner uses --suites, --db-* flags, and reflection discovery; docs/OPERATIONS.md says the Docker settings file is mounted at /app/data/armada.json, compose mounts it as /app/data/settings.json; docs/SCHEDULING.md says a 30 s heartbeat, the code default is 10 s (so the 100-cycle cleanup runs about every 17 minutes); README's FullPipeline stage list is shorter than the real one (Product Manager, Architect, Worker, Usability Engineer, Test Engineer, Linter, Judge, Recorder); docs/TUNNEL_PROTOCOL.md still describes feature-specific methods, but the Admiral serves only armada.http.request and armada.ws.* (others 404 unsupported_method); the dashboard Vite dev proxy defaults to localhost:5000 instead of the Admiral's 7890. Already fixed: README and docs/HARBOR.md Harbor link URL (7890) and PascalCase Harbor settings keys; README default-password wording. Fold the rest into V1 W7.1 (docs audit).
