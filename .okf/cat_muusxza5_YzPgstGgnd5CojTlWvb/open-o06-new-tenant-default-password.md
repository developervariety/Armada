---
type: "Project"
title: "O-06 (Medium, open): new tenants seed admin@armada with the default password"
description: "POST /api/v1/tenants seeds admin@armada/password in the new tenant; the banner and change prompt catch it but the startup bind guard does not once the server is running. Planned: random seed password."
timestamp: "2026-10-05T05:36:19.8558760Z"
created: "2026-10-05T05:36:19.2810960Z"
slug: "open-o06-new-tenant-default-password"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["default-credentials-bind-guard", "password-change-required-flag"]
version: "1"
salience: "0.5"
---
Creating a tenant (POST /api/v1/tenants, AdminOnly) seeds an admin@armada user with the default password in that tenant. The dashboard banner and PasswordChangeRequired flag catch it, but the non-loopback bind guard only runs at startup, so a running networked Admiral accepts the new default account until restart. Planned W1 follow-up: seed a random password and require the creator to set one. Status 2026-10-04: open. Mitigation: change the new tenant admin's password immediately after creating a tenant.
