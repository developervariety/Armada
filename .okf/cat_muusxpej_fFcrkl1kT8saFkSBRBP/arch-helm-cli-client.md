---
type: "Project"
title: "Helm CLI architecture: thin HTTP client with embedded-server fallback"
description: "`armada` (Armada.Helm, Spectre.Console CommandApp) calls the REST API at http://127.0.0.1:<AdmiralPort> with X-Api-Key from ~/.armada/settings.json via ArmadaApiClient; if the Admiral is not healthy, EnsureServerAsync starts an embedded in-process Admiral."
timestamp: "2026-10-05T05:43:52.8747240Z"
created: "2026-10-05T05:43:52.4555400Z"
slug: "arch-helm-cli-client"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["api-cli-command-groups", "arch-armada-client-sdk", "arch-solution-projects"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Helm/Program.cs (config.SetApplicationName("armada"); AddCommand/AddBranch registrations), Commands/BaseCommand.cs, Commands/*Command.cs + *Settings.cs pairs, Infrastructure/EmbeddedServer.cs, TypeRegistrar/TypeResolver, HelmHttpError, Rendering/TableRenderer.cs; ArmadaApiClient lives in src/Armada.Core/Client.

BaseCommand: loads ArmadaSettings; GetBaseUrl() = "http://127.0.0.1:" + AdmiralPort (IPv4 loopback avoids slow IPv6 localhost fallback; Watson binds "localhost" to IPv4); adds X-Api-Key = settings.ApiKey (the local API key the Admiral generates on first start; maps to the synthetic global admin); CreateAdminHttpClient for server stop/restart. EnsureServerAsync: health check; when down, EmbeddedServer.StartAsync runs a full Admiral in the CLI process (Helm references Armada.Server), then reloads settings to pick up a newly generated API key, and ensures the default fleet. Common options on most commands: --json, --page, --page-size, --verbose; exit code 0/non-zero is part of the 1.0 contract. `armada tui` hosts Armada.Tui (which uses Armada.Client instead). `armada mcp stdio` runs the MCP tool set over stdio (anonymous default tenant-admin context). Command catalog: [[api-cli-command-groups]].
