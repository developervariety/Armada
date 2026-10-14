---
type: "Reference"
title: "From-source install, update, reinstall, remove, and MCP scripts"
description: "scripts/<os>/install.sh builds the solution, deploys the dashboard, packs and installs Armada.Helm as the global `armada` tool; update/reinstall/remove/install-mcp siblings; --insecure for TLS-inspecting proxies."
timestamp: "2026-10-05T05:45:43.6171160Z"
created: "2026-10-05T05:30:05.1119170Z"
slug: "source-install-scripts"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["dashboard-committed-dist", "run-on-startup-scripts", "helm-cli-server-commands"]
version: "2"
salience: "0.5"
---
Entry points: scripts/linux/*.sh, scripts/macos/*.sh, scripts/windows/*.bat; shared implementations in scripts/common/.
- install: deploy dashboard, `dotnet build src/Armada.sln`, `dotnet pack src/Armada.Helm -o src/nupkg`, `dotnet tool install --global --add-source src/nupkg Armada.Helm`.
- update: rebuild and update the tool from the checkout; reinstall: uninstall the global tool then install; remove: uninstall the tool and delete ~/.armada/dashboard (does not delete the data dir).
- install-mcp / remove-mcp: `armada mcp install --yes` / remove, configuring Armada MCP for Claude Code, Codex, Gemini, and Cursor.
- publish-server: dotnet publish Release into ~/.armada/bin and deploy the dashboard.
- Framework: default net10.0; pass net8.0 or --framework net8.0 (resolve-framework.sh). On Windows put the framework first, then flags.
- --insecure (-k, --no-strict-ssl, --ignore-cert-errors) anywhere sets NODE_TLS_REJECT_UNAUTHORIZED=0 and npm_config_strict_ssl=false for npm only (fixes SELF_SIGNED_CERT_IN_CHAIN behind TLS inspection); dotnet/NuGet use the OS trust store.
- Without Node the dashboard step deploys the committed dist/.
