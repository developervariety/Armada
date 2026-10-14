---
type: "Project"
title: "O-13 (Low, open): planning and check-run output broadcast without secret scrubbing"
description: "Planning-session tool output and check-run output go to every WebSocket in the tenant without SecretRedactor; planned: scrub and deliver to the owner only."
timestamp: "2026-10-05T05:36:55.9918090Z"
created: "2026-10-05T05:36:55.5854690Z"
slug: "open-o13-unscrubbed-broadcasts"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["secrets-redaction-and-request-history", "websocket-authentication"]
version: "1"
salience: "0.5"
---
WebSocket broadcasts of planning-session tool output and check-run output reach every socket in the tenant and are not passed through SecretRedactor, so a secret printed by a tool or test run is visible to other tenant users' dashboards. Planned W1 follow-up: run these broadcasts through SecretRedactor and deliver owner-only where appropriate. Status 2026-10-04: open. New broadcast payloads containing process output should be redacted.
