---
type: "Reference"
title: "The seeded \"default\" bearer token is retired when the admin password changes"
description: "Bearer default works until its owner's default password is changed; then AuthenticationService refuses it and DefaultCredentialService deactivates it."
timestamp: "2026-10-05T05:33:45.2543000Z"
created: "2026-10-05T05:33:44.9416740Z"
slug: "default-bearer-token-retirement"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["default-credentials-bind-guard", "initial-admin-password-env", "password-change-required-flag"]
version: "1"
salience: "0.5"
---
The README's local workflow (`Authorization: Bearer default`) keeps working on a fresh local install. Once the owning user (admin@armada) no longer uses the default password, AuthenticationService (src/Armada.Core/Services/AuthenticationService.cs: IsSeededDefaultToken(credential) && !user.UsesDefaultPassword()) refuses it and DefaultCredentialService.RetireDefaultBearerTokenAsync deactivates it at startup. Chosen over "once another credential exists" because the password change is the explicit "this install is now secured" step (decision recorded in SECURITY_REVIEW Safe defaults). After changing the password, switch scripts to a new credential (Server > Credentials; token shown once) or the local API key. Verified by E2E.SecurityDefaults.
