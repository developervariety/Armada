---
type: "Reference"
title: "Supported install channels for 1.0 (Armada.Publisher, publisher.json)"
description: "NuGet tool (CLI+TUI), Windows Inno (Harbor) + WiX msi (server), macOS dmg (Harbor) + pkg (server), Linux deb/rpm for CLI/Harbor/server, Docker images; Homebrew/Scoop/Chocolatey/winget/AppImage disabled."
timestamp: "2026-10-05T05:30:23.0959140Z"
created: "2026-10-05T05:30:22.5782810Z"
slug: "install-channels-1-0"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["decision-v1-install-paths-d1", "ci-workflow-release", "service-registration-flags", "release-checklist"]
version: "1"
salience: "0.5"
---
Decision D1 (V1_READINESS): only these install paths for 1.0, built by src/Armada.Publisher from publisher.json:
- Any OS: CLI as a .NET global tool, channel nuget-cli (`dotnet tool install -g Armada.Helm`); includes `armada tui`.
- Windows: inno-harbor (Harbor .exe, runs --install-startup as the installing user), wix-server (Admiral .msi; runs --install-service as a deferred custom action, fails the install on non-zero; WiX v5+).
- macOS: dmg-harbor (Armada Harbor.app in a .dmg, no installer step), pkg-server (Admiral .pkg; postinstall runs --install-service; /usr/local/lib/armada-server/uninstall.sh runs --uninstall-service).
- Linux: linux-cli, linux-harbor, linux-server via fpm (.deb and .rpm; server after-install runs --install-service as root when systemd runs, try-restart on upgrade).
- Docker: jchristn77/armada-server, armada-dashboard, armada-proxy images via docker/ compose files.
Homebrew, Scoop, Chocolatey, winget, AppImage channels stay in publisher.json with "enabled": false; --all skips them. Build locally: `dotnet run --project src/Armada.Publisher -- doctor` then `./build-installers.sh <version>` (or .bat); output installers/<version>/ with .sha256 sidecars and SHA256SUMS. Each OS builds only its own formats; CI release.yml fans out. Only the CLI packages carry the TUI; pkg/msi/Inno/dmg/Docker carry server or Harbor.
