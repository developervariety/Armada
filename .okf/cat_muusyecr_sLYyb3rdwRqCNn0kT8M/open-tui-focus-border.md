---
type: "Project"
title: "TUI: noticeable border around the focused section (in progress)"
description: "Owner asked that the highlighted section have a noticeable border; agent on work/tui-focus adds a theme FocusBorder, ASCII and high-contrast variants, 80x24 fallback."
timestamp: "2026-10-05T05:26:35.3269110Z"
created: "2026-10-05T05:26:35.0915850Z"
slug: "open-tui-focus-border"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["pref-tui-ux-expectations"]
version: "1"
salience: "0.5"
---
Status 2026-10-05: requested by the owner while testing. Branch work/tui-focus: the region holding keyboard focus (sidebar, grid, filter row, Ask thread list/transcript/composer, workspace tree/editor, detail panel) draws a border in a theme FocusBorder color (Dark, Light, High contrast; high contrast uses a heavier line or reverse title), unfocused regions draw none or a dim border with reserved cells so layout does not shift, ASCII mode uses + - |, and 80x24 falls back to a focused title bar or gutter marker. Headless tests assert the border follows Tab/Shift+Tab/Ctrl+J/Esc.
