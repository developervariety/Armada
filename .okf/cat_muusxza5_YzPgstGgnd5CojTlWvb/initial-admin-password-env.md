---
type: "Reference"
title: "ARMADA_INITIAL_ADMIN_PASSWORD for headless and Docker installs"
description: "Set ARMADA_INITIAL_ADMIN_PASSWORD (8+ chars) before first start to replace admin@armada's default password before binding; required by the Docker compose files."
timestamp: "2026-10-05T05:33:40.1591090Z"
created: "2026-10-05T05:33:39.9202000Z"
slug: "initial-admin-password-env"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["default-credentials-bind-guard", "docker-compose-stack", "default-bearer-token-retirement"]
version: "1"
salience: "0.5"
---
DefaultCredentialService.InitialAdminPasswordEnvironmentVariable = "ARMADA_INITIAL_ADMIN_PASSWORD" (src/Armada.Core/Services/DefaultCredentialService.cs). On start, if set and the admin still uses the default password, it replaces it before the server binds, which also retires the seeded "default" bearer token. This is how Docker and other headless installs pass the non-loopback bind guard. docker/armada/compose.yaml and compose.split.yaml require it with ${ARMADA_INITIAL_ADMIN_PASSWORD:?Set ... (8+ characters) before the first start}; ci.yml's compose job proves the files still refuse to render without it. Put it in docker/armada/.env or the shell environment; never commit a real value. In Docker the "default" bearer token therefore never works; create a credential under Server > Credentials (token shown once).
