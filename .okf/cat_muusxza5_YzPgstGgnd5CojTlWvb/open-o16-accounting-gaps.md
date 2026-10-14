---
type: "Project"
title: "O-16 (Low, open): operational events lack tenant/actor; auth failures not persisted"
description: "Events from EmitEventAsync carry no tenant or actor, and authentication failures and authorization denials are not persisted events. Planned: AUTHENTICATION.md accounting."
timestamp: "2026-10-05T05:37:10.9795760Z"
created: "2026-10-05T05:37:10.8190710Z"
slug: "open-o16-accounting-gaps"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["audit-command-events"]
version: "1"
salience: "0.5"
---
Accounting gaps: operational events written through EmitEventAsync have no tenant id or actor; authentication successes and failures and authorization denials are only logged, not persisted as events. audit.command events (F-16) and request history do carry tenant and user. Planned W1 follow-up per AUTHENTICATION.md accounting. Status 2026-10-04: open. New event types should carry tenant and actor.
