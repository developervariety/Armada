---
type: "Project"
title: "API surface generator wrote sandbox paths as defaults"
description: "generate-api-surface.sh runs inside the test sandbox; defaults derived from the data dir became temp paths until ApiSurfaceBuilder mapped DefaultDataDirectory to ~/.armada."
timestamp: "2026-10-05T05:26:00.7929960Z"
created: "2026-10-05T05:26:00.2314410Z"
slug: "lesson-api-surface-generator-sandbox"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
version: "1"
salience: "0.5"
---
After TestTemp started sandboxing ARMADA_DATA_DIR, scripts/common/generate-api-surface.sh (which runs through Test.Shared) produced docs/api-surface-1.0.json with settings defaults like "/var/folders/.../armada_profile_x/armada.db" because ApiSurfaceBuilder only text-replaced the home directory. Fixed 2026-10-05: DefaultJson rewrites values starting with Constants.DefaultDataDirectory to "~/.armada..." before serializing. Always inspect `git diff docs/api-surface-1.0.json` after regenerating; it must contain only intended additions (E2E.ApiContract fails on removals or changes).
