---
type: "Reference"
title: "TUI widgets: ArmadaGrid, SelectField, PickerModal, FormDialog, viewers"
description: "ArmadaGrid<T> is the shared virtualized table (sized columns, server sort with ^/v, multi-select, column chooser, paging 10/25/50/100/250, ID columns elided then dropped first); SelectField/PickerModal for dropdowns; FormDialog/FormView for forms."
timestamp: "2026-10-05T05:38:32.9852870Z"
created: "2026-10-05T05:38:32.4535440Z"
slug: "tui-widgets"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-screen-base-classes", "tui-key-map", "tuikit-upstream-status"]
version: "1"
salience: "0.5"
---
Widgets/ArmadaGrid.cs: custom widget on TUIKit surfaces with TextWidth math; GridColumn (Identifier, Primary flags), GridQuery, GridPage; sized and proportional columns; server-side sort with ^/v indicators (s sort focused column, S reverse); multi-select (Space, Shift+Up/Down, Ctrl+A page, Esc clear); column chooser (c); paging bar "Showing 1-25 of 248. Page 1 of 10." with < > pages, z next page size and Z picker (10/25/50/100/250, remembered per table); empty/loading/error states; row virtualization (only visible rows drawn; 10,000 rows ~1 ms per frame); background refresh never moves the cursor or clears marks; passes Alt+arrows through for history. Narrow tables give the Primary column room, middle-elide Identifier columns (msn_0...WXYZ4, TextCells.ElideMiddle) and drop them first.
Others: SelectField / MultiSelectField / TriStateField / DateField / CheckField / InputField / TextAreaField (word wrap, undo, Ctrl+E $EDITOR) / RecordListField (a add, Enter edit, Del remove, Alt+Up/Down reorder); FilterBar; FormView + FormRow; ActionBar/ActionMenu (row menus via . or Shift+F10); Button/ButtonRow; DetailView/LinkDetailView; JsonViewer, DiffViewer (] [ files), LogViewer (+/- 100/200/500/1000 lines, f follow, r readable, 20,000 line cap), MarkdownView; ChartView; KpiStrip; StatusBadge (symbol + name, never color only); TabStrip; Wizard; Drawer; FocusScope (local copy pending TUIKit U1). Modals: PickerModal (fuzzy filter; Server picker opens wider), MultiPickerModal, FormDialog, ConfirmDialog (typed delete variant), ErrorDialog (code, message, request id), ViewerModal.
