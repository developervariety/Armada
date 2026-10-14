---
type: "Project"
title: "O-19 (Low, open): new security UI strings are English in every locale"
description: "Password change screen, defaults banner, auto-approve toggle, token-shown-once dialog, vessel auto-approve field, and the rate-limit message are not translated. Owner: W6.6."
timestamp: "2026-10-05T05:37:24.0873540Z"
created: "2026-10-05T05:37:23.8627740Z"
slug: "open-o19-security-strings-english-only"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["dashboard-i18n"]
version: "1"
salience: "0.5"
---
The dashboard security strings added in W1 (password change screen, default-credentials banner, Ask auto-approve toggle, token-shown-once dialog, vessel auto-approve field, login rate-limit message) have no entries in src/Armada.Server/wwwroot/i18n/armada.json, so they show in English for every locale. Owner: W6.6 (i18n). Status 2026-10-04: open. Add catalog phrases when touching these strings.
