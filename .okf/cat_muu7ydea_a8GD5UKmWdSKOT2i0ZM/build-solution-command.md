---
type: "Reference"
title: "Build the Armada solution (net8.0 and net10.0)"
description: "dotnet build src/Armada.sln builds every project for both net8.0 and net10.0 per src/Directory.Build.props."
timestamp: "2026-10-05T05:25:54.3436280Z"
created: "2026-10-05T05:25:53.7917790Z"
slug: "build-solution-command"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["ci-warnings-as-errors", "test-console-runner-touchstone", "no-version-changes-without-approval"]
version: "1"
salience: "0.5"
---
Command (repo root): `dotnet build src/Armada.sln`. CI uses `dotnet build src/Armada.sln -c Release --no-restore -warnaserror -warnnotaserror:NU1900,NU1901,NU1902,NU1903,NU1904` after `dotnet restore src/Armada.sln`.

src/Directory.Build.props applies to every project: TargetFrameworks net8.0;net10.0, ImplicitUsings enable, Nullable enable, GenerateDocumentationFile true (so missing XML docs surface as warnings), Version 1.0.0, Authors jchristn, and a NoWarn list for Debug and Release: 1701;1702;CS8600;CS8602;CS8603;CS8618;CS8625;CS0108;CS8601;CS0618 (most nullable warnings are suppressed). Armada.Tui sets ImplicitUsings disable in its own csproj.

Pick a single framework with `--framework net10.0` (or `-f net8.0`) for dotnet run/test; scripts resolve the framework through scripts/common/resolve-framework.sh (default net10.0). Do not change Version in Directory.Build.props without the owner's approval.
