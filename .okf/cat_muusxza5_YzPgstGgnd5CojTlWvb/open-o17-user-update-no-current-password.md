---
type: "Project"
title: "O-17 (Low, open): PUT /api/v1/users/{id} changes a password without the current one"
description: "Self-service password change via PUT /api/v1/users/{id} does not ask for the current password; PUT /api/v1/account/password does."
timestamp: "2026-10-05T05:37:16.0438230Z"
created: "2026-10-05T05:37:15.0238280Z"
slug: "open-o17-user-update-no-current-password"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["password-change-required-flag", "permission-levels-and-bypass"]
version: "1"
salience: "0.5"
---
A user editing their own record through PUT /api/v1/users/{id} can set a new password without proving the current one, so a stolen session can lock the owner out. PUT /api/v1/account/password (Authenticated) requires the current password and is what the dashboard and TUI forced-change flows use. Planned W1 follow-up. Status 2026-10-04: open. Tenant admins still cannot change a global admin's password (F-09).
