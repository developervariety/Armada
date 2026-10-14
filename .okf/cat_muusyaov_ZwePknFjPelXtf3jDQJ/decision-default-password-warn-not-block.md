---
type: "Project"
title: "Default admin password: warn, do not block"
description: "2026-10-04 owner chose \"Warn, don't block\": server no longer 403s sessions on the default password; TUI signs in with a header warning; dashboard still prompts."
timestamp: "2026-10-05T05:24:14.0166150Z"
created: "2026-10-05T05:24:13.7123710Z"
slug: "decision-default-password-warn-not-block"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["pref-tui-ux-expectations"]
version: "1"
salience: "0.5"
---
Originally (security workstream W1) a session for admin@armada still on the default password could only call whoami, status, and PUT /api/v1/account/password (server returned 403 otherwise). On 2026-10-04 the owner said "TUI should NOT force a password change" and chose the option "Warn, don't block": the server-side gate in ArmadaServer was removed; authenticate/whoami still report PasswordChangeRequired and DefaultCredentialsInUse; the TUI signs in and shows the header warning "Default credentials are in use."; the dashboard keeps its own forced change screen. Other safeguards remain: the Admiral refuses a non-loopback bind while default credentials are in use (unless AllowDefaultCredentialsOnNetwork), and the seeded default bearer token is retired when the password changes. docs/SECURITY_REVIEW.md documents the change.
