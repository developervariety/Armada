---
type: "Reference"
title: "Launching the TUI: armada tui options and environment"
description: "armada tui [--server URL] [--profile NAME] [--route PATH]; ARMADA_URL and ARMADA_TOKEN for scripted starts; hosted in-process by Helm; needs 80x24."
timestamp: "2026-10-05T05:37:47.1958010Z"
created: "2026-10-05T05:37:46.6454880Z"
slug: "tui-launch-and-options"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-project-structure", "tui-login-and-localhost-prefill", "tui-preferences-and-profiles", "decision-tui-plan-d1-d5"]
version: "1"
salience: "0.5"
---
The TUI (src/Armada.Tui, a library) is hosted by Helm as `armada tui` (decision D1), so every Helm package (NuGet tool, Linux deb/rpm CLI) carries it; server/Harbor installers and Docker do not. Examples: `armada tui`, `armada tui --server http://127.0.0.1:7890`, `armada tui --profile work`, `armada tui --server https://armada.example.com --profile prod --route "/missions?tab=voyages"`. From source: `dotnet run --project src/Armada.Helm -- tui ...`. With no options it connects to the last profile, or the local Admiral from settings.json. --server remembers the URL as a profile named after the host; --profile picks (or with --server creates/updates) a profile; --route picks the first screen after sign-in (else the last screen; first run lands in Ask Armada). ARMADA_URL supplies the server and ARMADA_TOKEN a session token or API key that skips login when accepted (TuiPaths constants). Minimum terminal 80x24; below that a localized "terminal too small" screen shows required vs actual size (Ctrl+Q still quits). Works in Windows Terminal, iTerm2, kitty, WezTerm, Ghostty, Alacritty, GNOME Terminal, tmux, SSH; Terminal.app and legacy conhost are degraded. User docs: docs/TUI.md; plan: TUI_APP_PLAN.md.
