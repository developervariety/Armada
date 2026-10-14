---
type: "Feedback"
title: "TUI keys that depend on focus must be discoverable from the current focus"
description: "Status bar hints follow focus, text fields say how to leave them, pending actions show inline keys, focused pane has a non-color marker, mouse buttons work regardless of focus."
timestamp: "2026-10-14T20:57:00.6069410Z"
created: "2026-10-14T20:57:00.3619860Z"
slug: "pref-tui-focus-discoverability"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["pref-tui-ux-expectations", "tui-focus-region-boxes", "tui-key-map", "tui-ask-armada", "tui-approvals-center"]
version: "1"
salience: "0.5"
---
In the Armada TUI, the owner found it unclear that approving an Ask proposal needs Esc (leave the composer) before a/r works, and more generally whenever Esc or Tab is needed before a single-key shortcut. Users cannot tell that a keystroke went into a text box instead of acting, so approvals looked broken (2026-10-05).

How to apply: for any TUI screen or feature, make focus-dependent keys discoverable from the current focus:
- status bar hints follow the focused control, and a text field's first hint says how to leave it;
- pending actions show inline key hints;
- the focused pane has a marker that is not color alone ([[tui-focus-region-boxes]]);
- mouse buttons work regardless of focus.
Review new TUI work against this rule.
