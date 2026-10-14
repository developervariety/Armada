---
type: "Reference"
title: "Supply chain: dependency scanning, pinned non-root images (F-21, F-22)"
description: "security.yml fails on vulnerable NuGet (incl. transitive) and npm high/critical advisories; Docker images pin exact base patch tags and run non-root (1654 / 101)."
timestamp: "2026-10-05T05:35:47.7084330Z"
created: "2026-10-05T05:35:47.3805820Z"
slug: "dependency-scanning-control"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["ci-workflow-security", "docker-images-and-nonroot"]
version: "1"
salience: "0.5"
---
F-22: there was no dependency scanning and the dashboard had a high npm advisory (undici, dev dependency). Now .github/workflows/security.yml runs `dotnet list src/Armada.sln package --vulnerable --include-transitive` and `npm audit --audit-level=high` in src/Armada.Dashboard on every push, PR, and weekly. F-21: images ran as root on floating tags; now Admiral and proxy run as APP_UID 1654 and the dashboard as nginx-unprivileged UID 101 on 8080, with base images pinned to exact patch versions (bump deliberately). When a scan fails, upgrade the package or lockfile; do not suppress. CI's -warnaserror leaves NU1900-NU1904 audit advisories as warnings precisely because security.yml owns that signal.
