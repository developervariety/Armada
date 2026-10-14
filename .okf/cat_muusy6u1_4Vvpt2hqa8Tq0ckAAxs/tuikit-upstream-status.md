---
type: "Project"
title: "TUIKit upstream workstream U1-U10: released; Armada on TUIKit 1.5.0"
description: "U1-U10 shipped in TUIKit releases (1.3.0+); Armada.Tui references TUIKit 1.5.0 but still carries its own copies of several primitives that should be replaced."
timestamp: "2026-10-14T20:57:07.8405690Z"
created: "2026-10-05T05:40:18.0295100Z"
slug: "tuikit-upstream-status"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["open-tuikit-upstream-release", "tui-modal-closed-workaround", "tui-frame-governor-performance", "decision-tui-plan-d1-d5"]
version: "2"
salience: "0.5"
---
TUI_APP_PLAN.md section U and commit 7f67490e. Items: U1 key/mouse forwarding into TabView/SplitView/ScrollView and hierarchical focus; U2 change events on ListView, DataTable, TextField, TextEditor, Checkbox, RadioGroup; U3 DataTable sizing, typed sort with indicators, multi-select, per-cell styles, paging hooks, CJK widths; U4 Dropdown/ComboBox, Button, ContextMenu, Tooltip, Badge; U5 theme-aware widgets and theme-driven Markdown/toast colors; U6 notification center history, actions, dismiss, wider toasts; U7 incremental Markdown in StreamingTranscript; U8 Pane wrap cache and idle frame throttling; U9 TextWidth-based width math; U10 built-in command palette and key-help overlay.

Status (checked 2026-10-14): released. TUIKit 1.3.0 on NuGet contains U1-U10. Armada moved 1.2.1 -> 1.4.0 (d434af6d) -> 1.5.0 (4dadfcf0, which aliases Armada's TabStrip because TUIKit now has its own). The migration away from Armada's private copies is not finished; see [[open-tuikit-upstream-release]]. Do not edit ~/Code/Tuikit from Armada work without the owner asking.
