# Request limits

One request must stay inside these limits. A refusal names the field, the limit and the count the request asked for. A successful response keeps its present shape.

| Limit | Value | Fields |
|---|---|---|
| Request items | 1000 | Bulk-delete `ids` (merge purge uses `entryIds`), dispatch `missions`, captain chat `history` |
| Configuration items | 256 | Dispatch `selectedPlaybooks` on the voyage and on each mission, and an endpoint `allowedTools` list |
| Summary buckets | 1000 | Token-usage, request-history and mission-history windows |

The summary check uses the same bucket fill the response would build. Token-usage and request-history include the bucket that contains the window end. Mission history stops before a bucket that starts at the window end. A default window (24 hours at 15 minutes, or 7 days at 60 minutes) is inside the limit. A window with no start or end is not filled and is not refused.

## Error

REST returns HTTP 400. `data.code` is `CollectionLimitExceeded`. `data.field`, `data.limit` and `data.actual` name the field, the limit and the count.

MCP returns a tool error with `errorCode` `CollectionLimitExceeded` and the same field, limit and count. HTTP sets `isError`. Stdio returns that tool result and does not replace it with a protocol error.

A WebSocket command that throws the refusal sends `command.error` with `code` `CollectionLimitExceeded` plus `field`, `limit` and `actual`. A voyage dispatch refusal uses the same `code` on `command.error` and carries the full body in `detail`.
