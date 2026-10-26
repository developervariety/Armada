# Saved Ask conversations

Temporary chat keeps the current page-state conversation and direct captain
chat route. Saved conversations add history on the Admiral. The Dashboard can create,
resume, rename, pin and archive a thread. Archive hides a thread from the normal
list; it does not delete history.

Each thread belongs to one user in one tenant. The Dashboard offers these as separate modes. Saved-thread routes retain the
Ask administrator access rule. A mission credential cannot use these human
conversation routes. A global administrator must still own a thread to read or
change it. Streamed saved-turn events reach only that exact interactive owner,
including after replay. Other administrators and mission sessions do not receive
them. The selected captain must be visible to the caller.

The existing `POST /api/v1/ask` read-only assistant and
`POST /api/v1/captains/{id}/chat` routes remain available. Saved-thread turns use
the same captain chat service and runtime isolation. They build context from
stored successful turns rather than accepting client-supplied history.

## Turn requests and restart

Each send has a client request identifier. The database stores that identifier
and claims the thread before the runtime starts. A second request with the same
identifier and payload returns the stored result or its current state. A changed
payload with the same identifier is a conflict. A new request cannot overtake an
active turn.

A completed result retains the reply, error, timing, token metrics and available
thinking output. A failed runtime response is also retained. Successful turns
supply bounded context for the next send. Older turns stay in storage and can be
read in pages.

A process interruption can leave a request with an unknown outcome. That request
stays fenced. A new service instance reports it as uncertain and does not launch
the runtime again. This prevents a duplicate launch; it does not guarantee that
an interrupted request produced a reply. Use a new conversation to continue
while the old request is investigated. There is no automatic retry or history
purge.

## API contracts

The saved-thread API uses these routes:

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/v1/ask/threads` | Create a conversation. |
| GET | `/api/v1/ask/threads` | List owned conversations. |
| GET | `/api/v1/ask/threads/{id}` | Read owned metadata. |
| PUT | `/api/v1/ask/threads/{id}` | Change title, captain, pin or archive state. |
| GET | `/api/v1/ask/threads/{id}/turns` | Read stored turns in pages. |
| POST | `/api/v1/ask/threads/{id}/turns` | Send or replay one request. |
| GET | `/api/v1/ask/threads/{id}/turns/{turnId}` | Read a stored request. |

Core request and response models define the saved-thread API. The Dashboard uses
matching typed contracts. Clients must retain the request identifier until they
know the result. Transport failure is not permission to submit the same text
under a new identifier.

The fork does not import upstream CLI permission overrides, automatic approval,
retention pruning or bulk thread deletion with this feature. Temporary chat MCP access uses the captain chat service's current caller scope.
Saved turns use a separate turn-bound MCP credential and the manual approval gate. Saved threads
do not change dispatch, landing, rescue or deployment authority.

A send body has `requestId`, `content` and `showThinking`. It does not accept
history or a captain identifier. A new request pins the thread's selected captain.
A replay uses the accepted request and its pinned captain even if the thread
metadata has since changed.

The send result has `statusCode`, `turn` and `message`. HTTP 200 includes either
a completed response or a saved runtime failure; inspect `turn.response.success`.
HTTP 202 means the same request is running in this service. HTTP 409 names a
conflict, an active different request, an archived thread, or an uncertain turn.
Read the request before taking another action after a transport error.

Thread updates use presence flags (`setTitle`, `setCaptainId`, `setPinned`,
`setArchived`) beside the corresponding values. Setting `setCaptainId` with a
null captain clears it. Absent flags leave fields unchanged. Thread lists return
an enumeration result. Turn lists return a bounded array in ascending sequence
order, using the exclusive `beforeSequence` cursor for older pages.

## Proposed actions and work

A saved turn uses an authenticated, encrypted credential bound to its owner,
thread and active request. Ordinary REST and WebSocket authentication reject this
credential. MCP rechecks the active user, tenant, administrator role and running
turn on every request. Its explicit catalog contains read tools and supported
mission and objective actions. Unsupported tools are unavailable in saved chat.
Temporary chat keeps its current caller access.

Supported changes become proposals. The stored proposal includes the exact tool,
normalized full record identifiers, canonical arguments and an owner-bound
fingerprint. Arguments are limited to 32,000 characters. Protected values are
refused before proposal storage. The service stores bounded outcome summaries
and typed work identifiers, not raw tool results.

The human owner reviews the tool and arguments, then approves or rejects the
proposal. Approval sends its fingerprint. The server rechecks current authority
and the fingerprint, claims the pending proposal, and invokes the original
registered handler under that human identity. The existing dispatch, check,
landing and rescue rules still apply. Proposals expire after 30 minutes. There
is no automatic approval or automatic follow-up captain turn.

An executing proposal cannot execute again. If a process stops or a handler
throws before a result is confirmed, the proposal remains fenced and reads as
uncertain. A repeated decision never invokes the handler again. The operator
must inspect the existing work before making a separate new proposal.

An accepted background job is stored as an exact work reference. Reading the
proposal observes the existing job journal and records its known terminal
result. Missing or lost jobs remain uncertain. Observation never starts another
job. This does not guarantee recovery of an unrecorded dispatch association.

Work reports read current records by the exact returned identifiers. They do not
search titles or infer work from model text. Produced work and failed landing
remain visible as separate outcomes. Mission delivery requires a current-attempt
`Landed` fact and a complete mission. Old landing facts do not prove a later
restart or retry delivered. Rescue children have their own cards and do not mark
their parent delivered. Voyage completion has no aggregate delivery claim. Recent
check statuses are observations, not a replacement for readiness.

The API lists the newest 200 proposals and bounds a work response to 200 cards.
These views retain stored history and do not purge it. The API routes are:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/v1/ask/threads/{id}/proposals` | Read owned proposals and observe accepted jobs. |
| POST | `/api/v1/ask/threads/{id}/proposals/{proposalId}/approve` | Approve the reviewed fingerprint. |
| POST | `/api/v1/ask/threads/{id}/proposals/{proposalId}/reject` | Reject the reviewed fingerprint. |
| GET | `/api/v1/ask/threads/{id}/work` | Read current work observations. |

Decision bodies contain `fingerprint`. Responses contain `statusCode`, `proposal`
and `message`. A successful response can have an informational message. HTTP 409
names an expired, conflicting or uncertain decision; it is not permission to
repeat a side effect. Global administrators still cannot decide another user's
conversation proposals.
