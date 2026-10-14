---
type: "Project"
title: "Adopt each TUIKit release and replace Armada's private TUI primitives (on 1.5.0)"
description: "TUIKit upstream work is released and Armada uses 1.5.0; remaining work is deleting Armada's own copies (FocusFrame, ToastLayer, FocusScope, ButtonRow, StatusBadge, RemoveClosed, FrameGovernor/TuiRunLoop, ThemeApplicator) in favor of TUIKit's."
timestamp: "2026-10-14T20:57:14.9732070Z"
created: "2026-10-05T05:26:47.4813570Z"
slug: "open-tuikit-upstream-release"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["tuikit-upstream-status", "pref-owner-libraries", "pref-tui-focus-discoverability", "decision-tui-plan-d1-d5", "tui-modal-closed-workaround"]
version: "2"
salience: "0.5"
---
Owner direction (2026-10-05): bump Armada to each new TUIKit release, and wherever Armada has custom code built on top of TUIKit, migrate to TUIKit's version so there is one implementation in TUIKit rather than private copies in Armada. Changing the TUIKit dependency version is fine; changing Armada's own version still needs approval. The owner keeps adding TUIKit features from ~/Code/TUIKit/IMPROVEMENTS_FROM_ARMADA.md (items A1-A8).

Status (checked 2026-10-14): the work/armada-upstream branch shipped (TUIKit 1.3.0 includes U1-U10). src/Armada.Tui/Armada.Tui.csproj references TUIKit 1.5.0. Armada still contains its own: FocusFrame (Widgets/FocusFrame.cs), ToastLayer (Shell/ToastLayer.cs), FocusScope, ButtonRow, StatusBadge, Modals.RemoveClosed workarounds (6 files), FrameGovernor/TuiRunLoop, and ThemeApplicator, plus plan item X1 (focus hints, text entry, transcript follow, click buttons, TuiTestHost.Click).

Next step, per release: update the PackageReference, replace the matching Armada code with TUIKit's, and keep the TUI suites and focus sweep green.
