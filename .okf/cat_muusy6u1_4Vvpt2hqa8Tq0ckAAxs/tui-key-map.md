---
type: "Reference"
title: "TUI key map (global, go-to, lists, forms, Ask, approvals)"
description: "Ctrl+K palette, F10 menu, ? or F1 help, Ctrl+A approvals, Ctrl+N notifications, Ctrl+J Ask dock, Alt+A ask about this, Ctrl+B sidebar, g+letter go-to, F5 refresh, F12 mouse to terminal, Ctrl+Q quit; rebind in tui.json KeyBindings."
timestamp: "2026-10-05T05:38:43.0060390Z"
created: "2026-10-05T05:38:42.0078170Z"
slug: "tui-key-map"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-shell-layout", "tui-ask-armada", "tui-approvals-center", "tui-preferences-and-profiles"]
version: "1"
salience: "0.5"
---
Global: Ctrl+K command palette (fuzzy; destinations, hub tabs, commands such as "theme light"; typing an entity id opens it); F10 menu bar; ? or F1 help overlay (bindings for the current screen); Ctrl+A Approvals center; Ctrl+N notification center; Ctrl+O run newest toast action; Ctrl+J Ask dock; Alt+A Ask about this; Ctrl+B sidebar; Tab/Shift+Tab/F6 next pane; Alt+Left/Alt+Right back/forward (Backspace outside text fields); F5 refresh; F12 hand the mouse to the terminal for text selection; Ctrl+Q quit.
Go-to: g h Dashboard, g a Ask, g i Needs You, g f Fleet Actions, g m Missions, g d Delivery, g v Vessels, g c Captains, g o Configuration, g y Activity, g j Jobs, g x API Explorer, g s Settings.
Hubs: [ ] tabs, Alt+1..9. Lists: Enter open, . or Shift+F10 row menu, n new, e edit, j View JSON, y copy id, Del delete (marked rows if any), d diff, l log, r restart, t transition, x cancel, / filter row, s/S sort, c columns, < > pages, z/Z page size. Forms: Tab fields, Enter/Space opens select or toggles, Ctrl+S save, Esc cancel (second Esc discards changes). Text: Ctrl+U clear, Ctrl+W delete word, Ctrl+R reveal masked value, Ctrl+E $EDITOR, Ctrl+Z/Ctrl+Y undo/redo. Detail pages: [ ] or Ctrl+PgUp/PgDn panels. Copy uses OSC 52 (works over SSH; dialog fallback). Rebind any command id in the KeyBindings section of ~/.armada/tui.json, e.g. "help.palette": "ctrl+p". Commands are registered once in Input/CommandService and feed menu, palette, and help.
