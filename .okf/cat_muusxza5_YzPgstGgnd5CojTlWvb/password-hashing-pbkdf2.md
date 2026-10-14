---
type: "Reference"
title: "Password hashing: salted PBKDF2-HMAC-SHA256 (600,000 iterations)"
description: "PasswordHasher stores pbkdf2-sha256$600000$salt$hash over the client SHA-256; legacy unsalted SHA-256 hashes upgrade at startup and on login; verification is constant-time."
timestamp: "2026-10-05T05:33:58.4867980Z"
created: "2026-10-05T05:33:58.1284180Z"
slug: "password-hashing-pbkdf2"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["login-rate-limiting", "upgrade-migrations-and-backups"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/PasswordHasher.cs: Scheme "pbkdf2-sha256", Iterations 600000, SaltBytes 16, HashBytes 32; stored format "pbkdf2-sha256$<iterations>$<base64 salt>$<base64 hash>". The input is the SHA-256 hex the API already accepts (PasswordSha256 unchanged), so clients did not change. Accepted iteration range when parsing 1,000 to 10,000,000. Verification is constant-time in both the new and legacy formats; API key comparison is constant-time too (F-18, F-26). Legacy unsalted SHA-256 hashes are rewritten at Admiral startup (stretching the stored SHA-256, so every password keeps working), and any not rewritten are upgraded on that user's next successful login. Consequence: an upgraded database cannot be used for password login by an older Admiral (restore the pre-upgrade backup to roll back). A small cache speeds default-password detection. Tests: Services.PasswordHashing.
