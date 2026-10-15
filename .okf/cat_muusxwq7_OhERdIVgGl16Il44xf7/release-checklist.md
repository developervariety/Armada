---
type: "Reference"
title: "Release checklist (docs/RELEASING.md)"
description: "Version only on owner approval; CHANGELOG Unreleased complete; full.yml, db-parity, upgrade test, install-verify green (all manual); simulated user testing; build/sign/checksum; tag v<version> triggers release.yml."
timestamp: "2026-10-05T05:30:31.7782190Z"
created: "2026-10-05T05:30:31.1690700Z"
slug: "release-checklist"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["ci-workflow-release", "no-version-changes-without-approval", "changelog-unreleased-entries", "db-parity-script", "upgrade-test-script", "install-verify-scripts"]
version: "1"
salience: "0.5"
---
Steps, top to bottom, no skipping:
0. Version numbers change only on the maintainer's explicit instruction (Directory.Build.props, Helm csproj, Constants.ProductVersion, dashboard package.json/lock, build scripts, Postman collection, CHANGELOG heading). Propose and wait.
1. Pick the commit; CHANGELOG ## Unreleased must list every user-visible change, setting, migration, breaking change. Schema changes registered for all four providers, operator steps in UPGRADING.md.
2. full.yml (manual) fully green (3 OS x 2 frameworks, dashboard, mobile, dist drift); ci.yml is only the push smoke test. A red-then-green rerun is not a pass until explained.
3. db-parity (manual) green on the commit (RESULT: PARITY OK).
4. Upgrade test from the previous release on every provider (run-upgrade-test.sh).
5. Simulated user testing per SIMULATED_USER_TESTING.md on an isolated armada-usertest stack; S1/S2 findings block; RCs get a week of real use.
6. Build packages (CI via tag, or Publisher doctor + build-installers locally); install-verify green; hand smoke one artifact per platform.
7. Signing when secrets exist; unsigned releases carry the SmartScreen/Gatekeeper notice.
8. Checksums: SHA256SUMS; regenerate with `dotnet run --project src/Armada.Publisher -- checksums --dir <dir>` for hand builds.
9. Rename ## Unreleased to the release heading, start a new Unreleased, commit, `git tag v<version>` and push the tag (owner approval; public action).
10. Verify the GitHub Release artifacts, NuGet package, docker/update.sh; add a V1_READINESS progress row.
