# Readable runtime log responses

Captain and mission log routes accept `formatted=true`. The existing `Log`,
`Lines` and `TotalLines` fields remain. Formatted responses also return `Entries`
and `EntriesTruncated`. Missing logs return an empty entries list in this mode.
The legacy text mode leaves Entries null.

Each entry contains Text, Kind, IsToolCall, ToolName, Redacted and Truncated.
Kind distinguishes text, thinking, proposed tools, tool results and status. These
are observations from runtime output; they do not establish a mission outcome.
Claude content blocks remain separate. Codex items and OpenCode parts are
recognized from their event shapes, so a historical mission does not depend on
its captain's current runtime setting. Unknown or malformed events use redacted
text instead of throwing or inventing a successful tool result.

The formatter parses at most 65,536 characters per event, returns at most 16
entries per event and 500 entries per page, and caps each entry's text at 2,000
characters plus its truncation marker. Tool names have a 200-character cap plus
a marker. An omitted event block marks its final retained entry; an omitted page
entry sets EntriesTruncated. Log joins the returned entry text. Lines counts
returned entries; TotalLines and offset retain the route's input pagination
semantics. The mission route retains its existing noise filtering before paging.
These display limits do not bound the existing whole-file log reader's memory.

SecretRedactor is the shared implementation. RuntimeLogFormatter.RedactSecrets
remains its public compatibility entry point for stored artifacts and other
callers. Display text and tool names both pass through it. REST and MCP raw log
responses also apply redaction, so old log files do not bypass this protection.
Quoted credentials, JSON escapes, nested encoded strings, arrays, root strings
and hyphenated provider keys have regression cases. Redaction preserves ordinary
text and is tested for repeated application. Deeply nested or malformed escaped
strings can be conservatively replaced.

No log file is rewritten by these GET routes. No recovery, landing, DoD or
scheduling action is invoked. Existing authentication and owner lookup remain in
place. Cross-tenant and unauthenticated formatted reads are tested separately.
MCP typed-entry options and dashboard chips remain later consumer work.

## Validation status

Six initial formatter cases produced five failures: unredacted tool names,
malformed nested-event exceptions, and missing Claude, Codex and OpenCode
handling. Two initial REST cases failed because Entries was absent. After repair,
focused formatter, log-route, tenant-scope and MCP cases passed. Extra escaped
JSON and page-limit regressions were then added. The final formatter run passed
10 cases, and the final log-route and tenant-scope run passed 107 cases. A prior
combined log, tenant-scope and MCP run passed 222 cases.

The full combined run passed 4,055 unit, 960 API and 183 runtime tests, with no
failures or skips, in 318 seconds. The solution build passed with 106 warnings
and zero errors (incremental build; not a clean warning census). No schema or
dashboard files changed in this slice. Provider validation remains the preceding
45-scenario matrix; the dashboard was not tested again for this change.
