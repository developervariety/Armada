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

## Tool-call cards, unread replies and browser state

A response keeps the tool calls the captain made (`response.toolCalls`: `id`, `name`,
`status` of `running`, `success` or `failed`, `arguments`, `result`, `elapsedMs`).
A failed response keeps the calls made before it failed. Arguments are kept to 4,000
characters and results to 8,000 characters, and a turn keeps at most 100 calls.
While a turn runs in this service, `turn.liveToolCalls` lists the calls made so far,
so a conversation reopened or reloaded mid-turn shows its cards. A turn this service
did not run, and a turn whose runtime was cancelled, report no live calls. The
Dashboard replaces a card by its call `id`, so a card never appears twice.

Each thread has an `unreadCount`. The same transaction that stores a turn's outcome
adds one to it, and a completion the request fence rejects adds nothing.
`POST .../read` sets it to zero and `POST .../unread` sets it to one. Both routes
return the thread and apply the same owner scope as the other thread routes. The
Dashboard reads a conversation when it opens or when a reply finishes in it, marks
the Ask sidebar entry with the number of conversations that hold unread replies,
and keeps a conversation marked unread while it stays open.

The Dashboard keeps three more things in the browser only: one unsent draft per
conversation and the last open conversation (both keyed by the signed-in user), the
width of the conversation list (drag the splitter, or focus it and use the arrow
keys, Home, End; Enter or a double click restores the default), and the prompts
sent from this browser. In the message box, ArrowUp with the caret at the start of
the text recalls earlier prompts, ArrowDown with the caret at the end walks forward
to the draft, and modifiers, a selection and IME composition leave the keys alone.
Work cards link to the mission or voyage page, and a parent or rescue mission id
links to its mission.

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
| POST | `/api/v1/ask/threads/{id}/read` | Clear the thread's unread count. |
| POST | `/api/v1/ask/threads/{id}/unread` | Set the thread's unread count to one. |

The MCP tools `armada_list_ask_threads` and `armada_get_ask_thread` read a human
owner's saved conversations. They are read-only: no MCP tool sends a message,
approves or rejects a proposal, changes a conversation or deletes one. See
[Saved Ask Conversations](MCP_API.md#saved-ask-conversations).

Core request and response models define the saved-thread API. The Dashboard uses
matching typed contracts. Clients must retain the request identifier until they
know the result. Transport failure is not permission to submit the same text
under a new identifier.

The fork does not import upstream's central CLI permission requests and rules,
automatic approval, retention pruning or bulk thread deletion with this feature.
Temporary chat MCP access uses the captain chat service's current caller scope.
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

## Native tool access

The Armada approval gate below covers Armada MCP tools. A CLI runtime also has
shell and file tools of its own, and a mission launch runs those without a prompt.
An Ask turn does not inherit that authority. Every Ask turn, saved or temporary,
resolves its native tool access on a copy made for that launch. The copy lives on
the turn's launch plan, so the server settings and the mission launch defaults
stay as they are.

The default is `ReadOnly`: no native shell execution and no file writes or edits.
Read tools and the turn's scoped Armada MCP tools remain. The `ask.nativeToolAccess`
setting widens an Ask launch. `Unrestricted` gives Ask the same native tool defaults as a
mission launch. The setting applies to every Ask turn on the server. It hot-reloads
in place, and the Dashboard Server page and `PUT /api/v1/settings` edit it.

Each runtime expresses `ReadOnly` through its own controls:

| Runtime | Read-only launch |
| --- | --- |
| Claude Code | No permission bypass. The model is offered only `Read`, `Grep` and `Glob`; `Bash`, `Edit`, `MultiEdit`, `Write` and `NotebookEdit` are also refused by name. The `armada` MCP server is allowed. |
| Codex | `--sandbox read-only` with the shell tool and unified exec features off. No bypass or full-auto flag. |
| Gemini | `--approval-mode plan`, Gemini's read-only mode. |
| Cursor | `--mode ask` with the sandbox enabled and no `--force`. |
| OpenCode | No permission bypass; the `plan` agent; the inline configuration denies `edit`, `bash`, `webfetch` and `external_directory`, replacing any grant already in that configuration. |
| Mux | `--approval-policy deny`, whatever policy the captain's own options select for missions. |
| API endpoint | Chat never offers `run_command`; the runtime has no other shell. |

A CLI runtime that cannot express read-only native tools is refused for Ask with
a message that names the runtime. It never launches with its permissive defaults.
A new CLI runtime refuses Ask until it declares a read-only mapping.

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
