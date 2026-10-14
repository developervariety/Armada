---
type: "Reference"
title: "Secrets never returned; SecretRedactor scrubs request history and logs"
description: "Read endpoints never return API keys, tokens (except once at creation), passwords, hashes, encryption keys, GitHub/model/tunnel secrets; request history redacts secret-shaped keys and values (ghp_, sk-, AKIA) and query strings."
timestamp: "2026-10-05T05:34:54.7882730Z"
created: "2026-10-05T05:34:54.1850600Z"
slug: "secrets-redaction-and-request-history"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["audit-command-events", "open-o13-unscrubbed-broadcasts", "authorization-coverage-test"]
version: "1"
salience: "0.5"
---
Rules (docs/SECURITY_REVIEW.md Secrets): no read endpoint returns API keys, bearer tokens (only once in the create response; masked ****abcd afterward, F-12), passwords or hashes, session encryption keys, GitHub tokens, model endpoint API keys, remote tunnel passwords and enrollment tokens (GET/PUT /api/v1/settings return ******** and a masked value sent back keeps the stored secret, F-13). src/Armada.Core/Services/SecretRedactor.cs is the single definition of secret-shaped redaction shared by the runtime log formatter and request history: Authorization bearer tokens, OpenAI/GitHub/AWS key shapes (sk-..., ghp_..., AKIA...), labelled api-key/secret/token/password assignments. Request history redacts secret-bearing headers, any JSON or form key containing password or secret or ending with token, apikey, accesskey, secretkey, privatekey, encryptionkey, secret-shaped values under any key, the raw query string, and the same patterns in non-JSON text (F-14). Request logs omit query strings, including /ws?token= (F-15). Verified by E2E.SecretsAndAudit, which seeds secrets and greps responses, history, and the server log. Never store or log secrets in new code; never put them in memories or commits.
