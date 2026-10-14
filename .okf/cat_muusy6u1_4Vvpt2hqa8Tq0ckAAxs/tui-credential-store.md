---
type: "Reference"
title: "TUI credential store: OS keychain with 0600 file fallback"
description: "Tokens per profile go to macOS Keychain (/usr/bin/security), Windows Credential Manager, or libsecret (secret-tool), else ~/.armada/tui-credentials.json (0600); ARMADA_TUI_CREDENTIAL_STORE forces file|keychain|wincred|secret-tool."
timestamp: "2026-10-05T05:39:05.5391980Z"
created: "2026-10-05T05:39:05.0404920Z"
slug: "tui-credential-store"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-preferences-and-profiles", "decision-tui-plan-d1-d5", "test-temp-profile-sandbox"]
version: "1"
salience: "0.5"
---
Services/Credentials: ICredentialStore, CredentialStoreFactory, MacKeychainCredentialStore (shells to /usr/bin/security), WindowsCredentialStore (+ WindowsCredentialNative P/Invoke), SecretToolCredentialStore (libsecret secret-tool), FileCredentialStore (JSON, mode 0600), FallbackCredentialStore (falls back to the file when the OS store is unavailable or a write fails, e.g. a locked keychain over SSH), ProcessRunner. Decision D4 (TUI plan): OS keychain with a 0600 file fallback. Environment: ARMADA_TUI_CREDENTIAL_STORE forces a store (file, keychain, wincred, secret-tool); ARMADA_TUI_CREDENTIALS moves the file (default ~/.armada/tui-credentials.json). Tests force file storage in a sandbox (TestTemp sets ARMADA_TUI_CREDENTIAL_STORE=file) and TuiTestHost passes a FileCredentialStore in its temp dir, so tests never touch the real keychain. Sign out deletes the profile's stored token.
