---
type: "Reference"
title: "Default credentials (admin@armada / password, Bearer default) and the non-loopback bind guard"
description: "Fresh installs seed admin@armada/password and bearer token \"default\"; the Admiral refuses a non-loopback rest.hostname while any default credential is active (UnsafeListenerConfigurationException) unless allowDefaultCredentialsOnNetwork."
timestamp: "2026-10-05T05:33:34.5885970Z"
created: "2026-10-05T05:33:34.2059280Z"
slug: "default-credentials-bind-guard"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["initial-admin-password-env", "default-bearer-token-retirement", "password-change-required-flag", "decision-default-password-warn-not-block", "open-o06-new-tenant-default-password"]
version: "1"
salience: "0.5"
---
First boot seeds a default tenant (id "default"), user admin@armada with the documented public default password "password", and a credential with bearer token "default". ArmadaServer.EnforceDefaultCredentialPolicyAsync (src/Armada.Server/ArmadaServer.cs) runs at start: DefaultCredentialService.ApplyInitialAdminPasswordAsync(ARMADA_INITIAL_ADMIN_PASSWORD), RetireDefaultBearerTokenAsync, then GetDefaultsInUseAsync. If defaults remain: on a loopback hostname (localhost, 127.0.0.1, ::1) it only logs a warning; on any other hostname it throws UnsafeListenerConfigurationException (Program prints the reason plainly and exits 1) unless allowDefaultCredentialsOnNetwork is true (then it warns). Fix by setting ARMADA_INITIAL_ADMIN_PASSWORD before first start, or start on localhost and change the password (dashboard or PUT /api/v1/account/password). The dashboard shows DefaultCredentialsBanner to admins while defaults are in use; the TUI shows a header warning. Gap: tenants created later get admin@armada with the default password, which the running server does not re-guard (O-06). Source finding F-07.
