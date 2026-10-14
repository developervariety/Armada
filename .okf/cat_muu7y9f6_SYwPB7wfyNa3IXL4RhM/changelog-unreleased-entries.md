---
type: "Project"
title: "CHANGELOG.md: record every user-visible change under ## Unreleased"
description: "Add an entry under ## Unreleased (grouped by topic, naming classes, settings, migrations, and deferred items) in the same change; the release step renames it; never invent the version heading."
timestamp: "2026-10-05T05:44:09.1130120Z"
created: "2026-10-05T05:44:08.8982290Z"
slug: "changelog-unreleased-entries"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["release-checklist", "no-version-changes-without-approval", "commit-message-format"]
version: "1"
salience: "0.5"
---
CHANGELOG.md keeps a ## Unreleased section at the top (currently grouped, e.g. "### Fragility remediation") with detailed bullets naming the classes, settings, migrations, tests, and explicitly deferred items. docs/RELEASING.md: every user-visible change, new or changed setting, migration, and breaking change belongs there; a release with an incomplete CHANGELOG is not ready. Only at release time, after the owner approves the version, is ## Unreleased renamed to the release heading and a fresh ## Unreleased started. Agents never pick the version heading themselves.
