---
type: "Reference"
title: "TUI login: email and API key modes, localhost prefill, 401 handling"
description: "Email Login (tenant lookup, picker, password) or API Key Login (tried as X-Token, then Bearer, then X-Api-Key, validated by whoami); localhost profiles prefill admin@armada / password and the local apiKey; F2 switches modes; no forced password change."
timestamp: "2026-10-05T05:38:50.1432970Z"
created: "2026-10-05T05:38:49.6786000Z"
slug: "tui-login-and-localhost-prefill"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-credential-store", "tui-preferences-and-profiles", "password-change-required-flag", "pref-tui-ux-expectations"]
version: "1"
salience: "0.5"
---
Screens/LoginView.cs (LoginStepEnum) mirrors the dashboard login. Email Login: email, tenant lookup, tenant picker when several match, password. API Key Login: accepts a session token, a credential bearer token, or the Admiral API key, tried in that order as X-Token, Authorization Bearer, X-Api-Key, validated with whoami. F2 switches mode; Ctrl+R reveals a masked field; Tab/Shift+Tab cycle Server, mode, field, buttons, Language, Theme (language and theme work before sign-in). The screen shows the documented defaults admin@armada / password for a fresh install, and an ASCII Armada logo.
Localhost prefill (Services/LocalAdmiralDefaults.cs, read-only, never writes settings.json): when the profile URL host is localhost, 127.0.0.1, or ::1, email is admin@armada (or the last account for that profile), password is "password" until the profile has signed in once, and API Key Login holds the apiKey from the local settings.json when the profile points at the local admiralPort; a new install's default profile uses that port. Remote servers get no prefill. Server picker: e/E edits name and URL; saving an edit to the current server reconnects; "Add server...".
A stored token is reused at start; a later 401 returns to login with "Your session expired. Sign in again."; File > Sign out forgets the token. Default-password sign-in goes straight to Ask (or --route) with a header warning; the TUI does not force a change.
