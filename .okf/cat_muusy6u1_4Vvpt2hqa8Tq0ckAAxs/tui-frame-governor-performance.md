---
type: "Reference"
title: "TUI performance: FrameGovernor and TuiRunLoop instead of TUIKit RunAsync"
description: "armada tui runs its own loop: compose only after input, posted work, resize, or theme change (plus 150 ms), else every 250 ms (1 s unfocused); idle about 1% CPU; AdaptiveFrames=false restores per-tick frames; Tui.Performance measures it."
timestamp: "2026-10-05T05:39:52.0545070Z"
created: "2026-10-05T05:39:51.4383390Z"
slug: "tui-frame-governor-performance"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tuikit-upstream-status", "tui-headless-testing", "flaky-test-handling"]
version: "1"
salience: "0.5"
---
docs/TUI_PERFORMANCE.md (W8.5). TUIKit's RunAsync composes and diffs every frame at 60 fps and has no frame-skip hook (TUIKit gap U8), so Shell/TuiRunLoop.cs runs the same steps and Shell/FrameGovernor.cs decides per tick whether to compose: after input (TerminalBackendAdapter reports bytes read), after posted work (the UI dispatcher notifies), on resize, on theme change, for 150 ms after any of those, otherwise every 250 ms (1 s while the terminal is unfocused). Idle input polling is 25 ms (worst-case added key latency). TuiStartOptions.AdaptiveFrames=false restores a frame every tick. Ctrl+Q goes through TuiContext.Quit (no public stop flag in RunAsync). Results (M5 Max, Debug): idle CPU Ask with 5,000 messages 10.2% to 1.1% of a core, Jobs list 4.1% to 0.6%; idle frames 60/s to about 4/s; streaming chunk at 5,000 messages 51 ms to 4.4 ms (AskTranscriptBuilder per-message block cache keyed by text, kind, tool calls, relative time, expansion, captain, metrics, width, theme, locale; confirm and live work cards always rebuilt); grids 10,000 rows ~1 ms per frame. Caps: AskConversation.MaxMessages 10,000, logs 20,000 lines, router history 100. Measure: ARMADA_TUI_PERF_REPORT=/tmp/tui-perf.txt ARMADA_TEST_SUITES=Tui.Performance dotnet run --project src/Test.Automated --framework net10.0 (lines prefixed [tui-perf]). Assertions compare governed CPU to the ungoverned loop measured in the same run, not absolute limits.
