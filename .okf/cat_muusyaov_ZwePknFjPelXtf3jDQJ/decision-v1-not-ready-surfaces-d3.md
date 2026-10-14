---
type: "Project"
title: "V1 D3: remove keyword /api/v1/ask; Harbor split mode experimental"
description: "Keyword POST /api/v1/ask and `armada ask` removed (Ask threads replace them); create_voyage/retry_mission removed from Ask work linking; Harbor split mode and self-rebuild marked experimental."
timestamp: "2026-10-05T05:23:59.8514980Z"
created: "2026-10-05T05:23:59.6490920Z"
slug: "decision-v1-not-ready-surfaces-d3"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
version: "1"
salience: "0.5"
---
Decided 2026-10-04: remove the keyword POST /api/v1/ask responder (the dashboard did not call it; Ask threads replace it) and the `armada ask` CLI command; remove create_voyage / retry_mission from Ask work linking; mark the Harbor split-mode transport (and server self-rebuild) experimental in the API surface (ExperimentalSurface marks them in OpenAPI). Open question to the owner: keep self-rebuild experimental or only Harbor split mode.
