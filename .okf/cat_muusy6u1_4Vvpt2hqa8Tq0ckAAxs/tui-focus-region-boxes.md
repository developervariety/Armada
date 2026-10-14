---
type: "Project"
title: "TUI focus treatment: every focus region boxed, one whole focused box (RegionFrames, RegionStack)"
description: "Each Tab stop of a FocusScope.RegionHost is boxed by the shell; layouts must leave the box rows via RegionStack; Tui.FocusSweep checks every route."
timestamp: "2026-10-05T21:38:45.8357580Z"
created: "2026-10-05T21:38:42.6247830Z"
slug: "tui-focus-region-boxes"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-theming-glyphs-and-80x24", "tui-shell-layout", "tui-headless-testing"]
version: "1"
salience: "0.5"
---
As of 2026-10-05 (branch work/tui-focus-frames, commit 82cc2aa8): the TUI has one focus treatment. ShellView.Compose resets placements (FocusScope.ResetPlacements), renders the screen, runs FocusScope.RepairFocus (moves focus off hidden controls), then RegionFrames.Collect walks the screen's FocusScope: every placed Tab stop (FocusScope.IsFocusStop) in a RegionHost scope is a region (nested RegionHost children are descended: HubScreen content, StackPanel, BacklogStackPanel, FleetRunBody, ApiExplorerWorkspace, PlanningTranscriptPanel, SetupWizardPanel; a host that drew no regions is boxed itself). FocusFrame.DrawNested draws all boxes light with tee junctions and the focused one whole and heavy in ArmadaTheme.FocusBorder (ASCII: # and =, AsciiGlyphs maps heavy lines to =/#). Box = region inflated by 1 cell, so LAYOUTS MUST LEAVE the row/column around each region free: use Widgets/RegionStack (Content/Region/Place/Fill/Spacer) for vertical stacks; side-by-side panes share one column. ShellView exposes LastRegions and LastFocusedBox for tests. Drawers: implement IRegionOverlayHost (RequestHistoryScreen, FleetActionRunScreen). Dialogs: ArmadaDialog redraws its box focused when topmost (ModalService sets IsTopmost); shell draws no pane focused while a modal or the menu is open. ArmadaWidget.BoxTitle draws text on a region's top line (Ask composer stop hint). TabStrip shows >Tab< when focused. Empty ButtonRow/ActionBar/OpsFilterBar and containers whose children are all hidden are not Tab stops. Tests: Tui.FocusSweep (TuiFocusSweep.Check/Sweep/CheckModal: typed cell inspection at every Tab stop of every route at 80x24/120x40/160x50 plus DataRoutes with feature-suite stubs) and Tui.FocusBorder. A new screen whose layout overlaps a box fails the sweep with "box of X covers content".
