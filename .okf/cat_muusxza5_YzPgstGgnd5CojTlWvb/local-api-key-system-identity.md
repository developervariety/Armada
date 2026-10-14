---
type: "Reference"
title: "Local API key: system@armada global admin that cannot log in with a password"
description: "settings.json apiKey (generated ak_<64 hex> on first start, saved) authenticates X-Api-Key as synthetic global admin system@armada in tenant ten_system; password login for it is refused (F-08)."
timestamp: "2026-10-05T05:34:47.2579880Z"
created: "2026-10-05T05:34:46.8341760Z"
slug: "local-api-key-system-identity"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["security-identities", "helm-cli-server-commands", "secrets-redaction-and-request-history"]
version: "1"
salience: "0.5"
---
ArmadaServer.EnsureApiKeyAsync: when settings.apiKey is empty it generates "ak_" + two GUIDs (N format) and saves settings.json. Requests with header X-Api-Key equal to it act as the synthetic global admin system@armada in tenant ten_system. The Helm CLI reads the key from ~/.armada/settings.json to call server control routes (stop, restart). F-08: that identity used to be seeded with the password "system" and POST /api/v1/authenticate accepted it; now password login is refused for the system tenant and user, and the stored password is random. Treat the key as a secret (it is full admin); it is never returned by read endpoints; factory reset regenerates it, so MCP clients and the CLI must be reconfigured afterward. The TUI's API Key Login on a local profile is prefilled with it.
