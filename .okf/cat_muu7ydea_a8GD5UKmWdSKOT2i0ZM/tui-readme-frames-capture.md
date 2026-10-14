---
type: "Reference"
title: "Regenerate README TUI captures (Tui.ReadmeFrames)"
description: "ARMADA_TUI_README_DIR=docs/tui-screens ARMADA_TEST_SUITES=Tui.ReadmeFrames dotnet run ... writes 120x40 text frames (login, ask, home, missions, approvals)."
timestamp: "2026-10-05T05:27:45.3820290Z"
created: "2026-10-05T05:27:45.0442930Z"
slug: "tui-readme-frames-capture"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["tui-headless-testing", "tui-parity-generator-and-suite"]
version: "1"
salience: "0.5"
---
Command (repo root):
  ARMADA_TUI_README_DIR=docs/tui-screens ARMADA_TEST_SUITES=Tui.ReadmeFrames dotnet run --project src/Test.Automated --framework net10.0
The suite (src/Test.Shared/Suites/Tui/TuiReadmeFramesSuite.cs, case readme_frames) renders frames headlessly at 120x40 from stub data and, only when ARMADA_TUI_README_DIR is set, writes them to that directory: docs/tui-screens/{login,ask,home,missions,approvals}-120x40.txt. These text captures are what the README Terminal UI section shows. Regenerate and commit them when a TUI change alters those screens (commit 5721b79b did so after a paging footer change).
