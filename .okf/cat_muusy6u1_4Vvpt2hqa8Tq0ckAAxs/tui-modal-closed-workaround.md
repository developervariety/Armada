---
type: "Project"
title: "TUIKit gotchas worked around in Armada.Tui (closed modals, TextEditor, FormView)"
description: "A modal closed outside key handling stays on TUIKit's stack and swallows the next key: call RemoveClosed after programmatic closes; TextEditor swallows Ctrl chords and uses string.Length; FormView lacks hidden rows/checkbox."
timestamp: "2026-10-05T05:40:26.0298720Z"
created: "2026-10-05T05:40:25.3311230Z"
slug: "tui-modal-closed-workaround"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tuikit-upstream-status", "tui-screen-base-classes"]
version: "1"
salience: "0.5"
---
Known TUIKit 1.2.1 gaps hit while building the TUI (TUI_APP_PLAN progress log, 2026-10-04): (1) A modal closed from a posted callback or programmatically stays drawn and on the stack until the next input, and swallows that key. Workaround: drop closed modals at once (Modals.RemoveClosed / OpsScreen helper) after any programmatic close. (2) StreamingTranscript flattens newlines while streaming (U7); the live Ask reply re-renders Markdown per chunk. (3) TextEditor swallows every Ctrl chord and measures string.Length; it is wrapped so palette and global keys still work; CJK widths in the composer remain (U9). (4) FormView cannot hide or disable rows, has no checkbox field, and cannot turn off dirty tracking; scope-dependent fields are shown with hints and OpsCheckField/CheckField are Armada widgets. (5) No read-only text input or select. (6) RunAsync has no frame-skip hook or public stop flag (own run loop, TuiContext.Quit). (7) The terminal-too-small screen is not localizable upstream; there is no post-compose hook, so ASCII mode transliterates at the backend. (8) On editor pages that open in a text field, Alt+Left moves by word and Esc goes back. These go away when the U-branch TUIKit is released and adopted.
