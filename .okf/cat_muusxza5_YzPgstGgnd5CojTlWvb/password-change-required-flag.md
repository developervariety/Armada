---
type: "Reference"
title: "PasswordChangeRequired: default-password sessions are flagged, not blocked"
description: "authenticate and whoami return PasswordChangeRequired for admin@armada with the default password; dashboard prompts for a change, TUI only warns; the server-side block was removed at the owner's request."
timestamp: "2026-10-05T05:33:52.7478970Z"
created: "2026-10-05T05:33:52.3213810Z"
slug: "password-change-required-flag"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["decision-default-password-warn-not-block", "default-credentials-bind-guard", "open-o17-user-update-no-current-password", "tui-login-and-localhost-prefill"]
version: "1"
salience: "0.5"
---
AuthenticationService sets PasswordChangeRequired = user.UsesDefaultPassword() on the authenticate and whoami contexts. The session is flagged but has full access: the server-side block was removed at the owner's request so the TUI and API work on a fresh local install (decision-default-password-warn-not-block). Clients: the dashboard shows PasswordChangeRequired.tsx (password change screen) plus DefaultCredentialsBanner for admins; the TUI goes straight to Ask Armada and shows a header warning line while defaults are in use. Note README "Default Credentials" still says the session "can only change the password"; SECURITY_REVIEW.md and the code say flagged-not-blocked. Self-service change: PUT /api/v1/account/password (Authenticated, asks for the current password). PUT /api/v1/users/{id} does not require the current password (O-17).
