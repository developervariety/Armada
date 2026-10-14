---
type: "Reference"
title: "TUI shell: header, menu bar, sidebar, main region, Ask dock, status bar"
description: "Header shows tenant, user, role, health, Live/Offline, running jobs, approvals, bell; menu File/Go/View/Actions/Ask/Help; sidebar mirrors navConfig and collapses below 110 cols, hides below 90."
timestamp: "2026-10-05T05:38:04.6340050Z"
created: "2026-10-05T05:38:03.9025550Z"
slug: "tui-shell-layout"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-routing-and-hubs", "tui-key-map", "tui-theming-glyphs-and-80x24", "tui-notifications"]
version: "1"
salience: "0.5"
---
Shell/ShellView composes: HeaderBar (product, tenant, email, role Global Admin/Tenant Admin, server health polled every 30 s, WebSocket Live/Offline, background job count polled 5 s while running and 30 s idle, pending approvals, notification bell with unread count; a second line for Armada.Proxy instance with Switch Deployment and Proxy Logout; an admin warning line while default credentials exist), MenuBarView (F10: File = switch profile, sign out, quit; Go; View = theme, icons, sidebar, Ask dock, language, auto-refresh; Actions = current screen's actions; Ask; Help), SidebarView (same sections, labels, and order as the dashboard's navConfig.tsx via NavCatalog; Needs You count, ! when critical; sections fold with Left/Right; icons-only from 90-109 columns, hidden below 90, Ctrl+B toggles), the main region (current screen), AskDockView (Ctrl+J), StatusBarView (key hints, always keeps "? Help", refresh state, approvals count), ToastLayer (top right; Ctrl+O runs the newest toast's action). Every state is spelled out in text so nothing depends on color. Layout breakpoints live in ShellLayout/LayoutModeEnum.
