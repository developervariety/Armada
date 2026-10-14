---
type: "Reference"
title: "CI workflow release.yml: tag-driven multi-OS packaging"
description: "Pushing tag v<version> (or dispatch with version) runs Armada.Publisher per OS (Windows inno/wix, macOS dmg/pkg, Linux deb/rpm, NuGet tool), then one SHA256SUMS and a GitHub Release."
timestamp: "2026-10-05T05:28:28.6003310Z"
created: "2026-10-05T05:28:23.3966750Z"
slug: "ci-workflow-release"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["release-checklist", "install-channels-1-0", "no-version-changes-without-approval"]
version: "1"
salience: "0.5"
---
.github/workflows/release.yml (permissions contents: write). Triggers: push tags v*; workflow_dispatch input version (without leading v). Jobs: version (derives the string once); windows (choco innosetup, dotnet tool wix; publisher doctor; channels inno-harbor and wix-server; signs when WINDOWS_CERT_BASE64/WINDOWS_CERT_PASSWORD exist); macos (dmg-harbor, pkg-server; signs and notarizes when APPLE_CERT_* and APPLE_NOTARY_* exist, else ad-hoc); linux (fpm + rpm; linux-cli, linux-harbor, linux-server; repo metadata signed with GPG_PRIVATE_KEY); nuget (nuget-cli, NUGET_API_KEY); release (collects *.exe/*.msi/*.dmg/*.pkg/*.deb/*.rpm/*.nupkg from packages/, writes one sorted SHA256SUMS, creates or updates the GitHub Release v<version> with softprops/action-gh-release). Each publisher call is `dotnet run --project src/Armada.Publisher -c Release -- --channel <channel> --version <v>`. A release with one failed OS job is partial and must be fixed or pulled. Tagging requires the owner's approval of the version.
