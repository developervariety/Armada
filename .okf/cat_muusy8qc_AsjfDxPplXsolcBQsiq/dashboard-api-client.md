---
type: "Reference"
title: "Dashboard API client (src/api/client.ts) and ApiError"
description: "One exported async function per server call; request() sends X-Token, 30 s default timeout, AbortSignal support, camelizes PascalCase responses, calls onUnauthorized on 401, throws ApiError(message, status, data) with apiErrorCode() for data.code."
timestamp: "2026-10-05T05:41:13.4849760Z"
created: "2026-10-05T05:41:13.1226070Z"
slug: "dashboard-api-client"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["no-branching-on-exception-text-rule-1", "tui-parity-generator-and-suite", "client-contract-suites"]
version: "1"
salience: "0.5"
---
src/Armada.Dashboard/src/api/client.ts (~1,400 lines; about 320 server-calling exports, mirrored one-to-one by Armada.Client for the TUI). BASE_URL = import.meta.env.VITE_ARMADA_SERVER_URL or '' (same origin under /dashboard). setAuthToken(token) and setOnUnauthorized(cb) are wired by AuthContext. request<T>(method, path, body, opts): JSON content type, X-Token header when signed in, AbortController timeout (default 30000 ms; planning create 5 min, summarize 3 min), optional external AbortSignal (chat Stop cancels the in-flight request and the server cancels the captain), acceptStatuses (e.g. 409 with a payload returned like success), rawText. On 401 it calls onUnauthorized and throws. On other non-2xx it throws ApiError extends Error with message from Message/message/Error/error, status, and data = camelizeKeys(e.Data ?? e.data). apiErrorCode(err) reads data.code. Responses are deep-camelized (camelizeKeys) because the server returns PascalCase. Fragility rule (R4): callers should branch on ApiError.status and code, never on message text. Adding a function here requires the matching ArmadaClient method (generate-client-methods.py --missing) and parity entry.
