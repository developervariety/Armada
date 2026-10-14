---
type: "Reference"
title: "Armada.Tui project layout and dependencies"
description: "src/Armada.Tui (TUIKit 1.2.1 + Armada.Client): ArmadaTuiApp/TuiContext root; folders Shell, Routing, Screens, Widgets, Modals, Services, Ask, Approvals, Input, Theming, Text; parity.json embedded."
timestamp: "2026-10-05T05:37:56.7179090Z"
created: "2026-10-05T05:37:56.0453340Z"
slug: "tui-project-structure"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-shell-layout", "tui-routing-and-hubs", "tui-screen-base-classes", "tui-widgets"]
version: "1"
salience: "0.5"
---
src/Armada.Tui/Armada.Tui.csproj: PackageReference TUIKit 1.2.1 (owner's library; source at ~/Code/Tuikit), ProjectReference Armada.Client (typed REST + ArmadaSocket client, one method per dashboard api/client.ts function), ImplicitUsings disabled, InternalsVisibleTo Test.Shared, parity.json embedded as Armada.Tui.parity.json. Targets net8.0 and net10.0.
- Root: ArmadaTuiApp.cs (composition root; takes TuiApplication, TerminalBackendAdapter, TuiStartOptions, client factory, ICredentialStore), TuiContext.cs (services bag, Quit, KeepHubTabFocus), TuiStartOptions.cs (ServerUrl, PreferencesPath, Live, Utf8Probe, AdaptiveFrames, TelemetryHostFactory), GlobalCommands.cs, TuiSnapshot.cs.
- Shell/: ShellView, ShellLayout, HeaderBar, MenuBarView, SidebarView, StatusBarView, ToastLayer, AskDockView, FrameGovernor, TuiRunLoop.
- Routing/: Router, RouteTable, NavCatalog, HubDefinition/HubTab, RouteMatch.
- Screens/: ScreenBase, ScreenFactory, HubScreen, PlaceholderScreen, LoginView, Kit/, Operations/, Entities/, Build/, Delivery/, Configuration/, Activity/, Admin/, Ask/.
- Widgets/ (ArmadaGrid, SelectField, FormView, viewers...), Modals/ (PickerModal, FormDialog, ConfirmDialog, ErrorDialog, CommandPaletteModal, HelpOverlayModal, NotificationCenterModal, ViewerModal), Services/ (Session, Preferences, Credentials/, EventPump, Refresh, Notification, Approval, Localization, Theme, Clipboard, External, TuiTelemetry), Ask/ (AskController, AskConversation...), Approvals/, Input/ (CommandService, KeyGesture), Theming/, Text/ (TextCells, FuzzyMatcher).
