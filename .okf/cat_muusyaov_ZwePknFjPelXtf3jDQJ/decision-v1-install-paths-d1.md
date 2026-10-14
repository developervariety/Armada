---
type: "Project"
title: "V1 D1: install paths at 1.0"
description: "1.0 ships Docker, NuGet tool CLI, Windows Inno/WiX, macOS dmg/pkg, Linux deb/rpm; Homebrew/Scoop/Chocolatey/Winget/AppImage disabled unless implemented."
timestamp: "2026-10-05T05:23:51.5379620Z"
created: "2026-10-05T05:23:51.1572820Z"
slug: "decision-v1-install-paths-d1"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["open-v1-readiness-remaining"]
version: "1"
salience: "0.5"
---
Decided 2026-10-04 (owner approved V1_READINESS.md recommendations): install paths at 1.0 are Docker (Admiral, dashboard, proxy); the NuGet global tool for the CLI (Helm, which also hosts `armada tui`); Windows (Inno for Harbor, WiX for the server); macOS (signed and notarized .app in a .dmg for Harbor, .pkg for the server); Linux Deb/Rpm. Homebrew, Scoop, Chocolatey, Winget, and AppImage ship only if implemented; otherwise they are disabled in publisher.json and removed from the README. Install verification scripts in scripts/common/install-verify/ cover Docker, deb, rpm, NuGet tool, and macOS pkg.
