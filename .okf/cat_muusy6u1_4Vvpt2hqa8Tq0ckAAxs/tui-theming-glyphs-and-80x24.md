---
type: "Reference"
title: "TUI themes, ASCII glyph mode, no color-only states, 80x24 layout"
description: "Themes Dark/Light/High contrast/Auto (COLORFGBG; NO_COLOR picks High contrast); Icons Auto/Unicode/ASCII (Glyphs) with AsciiGlyphs transliteration at the backend; statuses carry symbols (+ x ! ~ -); verified at 80x24."
timestamp: "2026-10-05T05:39:43.6345440Z"
created: "2026-10-05T05:39:43.1763070Z"
slug: "tui-theming-glyphs-and-80x24"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-shell-layout", "tui-widgets", "tui-i18n"]
version: "1"
salience: "0.5"
---
Theming/: ThemeService, ThemeModeEnum, ThemePalettes, ThemeApplicator (pushes styles into every widget, since TUIKit widgets do not read app.Theme yet; U5), ArmadaTheme, IThemeable, GlyphModeEnum, AsciiGlyphs, TerminalEncoding. View menu: Dark, Light, High contrast (black/white, bright accents, ASCII borders, reverse video or underline for every selected/focused item), Auto (COLORFGBG background; Dark when unknown; High contrast when NO_COLOR is set). Icons: Auto, Unicode, ASCII (saved as Glyphs in tui.json); ASCII draws borders with + - | and transliterates box drawing, blocks, Braille, arrows, bullets (charts use # = +); user and server text (names, CJK) is never changed. AsciiGlyphs is a width-preserving map applied by TerminalBackendAdapter to everything written to the terminal and by TuiSnapshot. Auto picks ASCII when not UTF-8: TERM=dumb, LC_ALL/LC_CTYPE/LANG not naming UTF-8 (C, POSIX), or legacy conhost. No state by color alone: + Complete, x Failed, ! Stalled, ~ InProgress, - other; active tab in brackets; current sidebar item >; completed wizard steps +; header spells Live/Offline/Healthy/Unreachable. At 80x24 the sidebar hides, ? Help stays, the header prioritizes approvals and bell; every route and tab checked at 80, 100, 120, 160 columns (Tui.Display). Widths use terminal cells (TextCells) so CJK aligns.
