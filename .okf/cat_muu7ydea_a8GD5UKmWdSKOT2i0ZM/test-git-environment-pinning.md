---
type: "Reference"
title: "TestGitEnvironment pins core.autocrlf=false and core.eol=lf for every git process"
description: "Tests set GIT_CONFIG_COUNT/KEY/VALUE so host git settings (Windows runners' autocrlf=true) cannot change checked-out file contents."
timestamp: "2026-10-05T05:27:06.6009410Z"
created: "2026-10-05T05:27:06.2156720Z"
slug: "test-git-environment-pinning"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-temp-profile-sandbox", "lesson-ci-new-test-portability", "ci-workflow-ci-yml"]
version: "1"
salience: "0.5"
---
src/Test.Shared/Infrastructure/TestGitEnvironment.cs, called from the TestTemp module initializer, appends core.autocrlf=false and core.eol=lf to the process environment via GIT_CONFIG_COUNT / GIT_CONFIG_KEY_n / GIT_CONFIG_VALUE_n (git 2.31+; applied after system, global, and repo config), after any GIT_CONFIG_* entries already present. Child processes (test helpers and product code such as GitService and DockService) inherit it.

Why: GitHub's Windows runners set core.autocrlf=true system-wide, so "base\nworker change\n" checked out as CRLF and every content assertion failed on Windows only. CI also configures git identity (Armada CI / ci@armada.invalid), init.defaultBranch main, and core.longpaths true before tests. When writing tests: use LF in fixtures, avoid characters illegal in Windows file names (for example '>'), and expect the platform line ending only where the product documents it.
