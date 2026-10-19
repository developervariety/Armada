# Captain Routing and Persona Priorities

Armada assigns pending missions to available captains. Personas define the role and can also define an ordered hierarchy of captain classes. A role does not imply a provider, model, or deployment location: any persona can prefer Codex, Claude Code, Mux, or another runtime in any order.

## Define a hierarchy

Set `captainPriorities` on a persona through REST, MCP, or its configuration editor. Each entry matches all its supplied fields. Array order establishes priority; entries need at least one matching field.

| Field | Meaning |
|---|---|
| `runtime` | Exact Armada runtime: ClaudeCode, Codex, Gemini, Cursor, Mux, OpenCode, ApiEndpoint, or Custom |
| `model` | Exact captain model identifier, ignoring case (maximum 1024 characters); no wildcard or substring matching |
| `endpoint` | For `runtime: Mux`, an exact endpoint name ignoring case. For `runtime: ApiEndpoint`, the exact, case-sensitive managed inference endpoint ID (`mep_...`), matched against `Captain.ModelEndpointId`. Maximum 1024 characters. Other runtimes cannot set this field. |
| `minimumTier` | Minimum effective Economy, Standard, or Premium tier; explicit captain tier overrides model classification |

For example, a persona can prefer Codex, then Claude Code, then a Mux endpoint. Reverse the entries to prefer Mux. Model and endpoint fields select already-configured captains; routing does not modify their launch configuration. Configure the Mux captain's model and `RuntimeOptionsJson.endpoint` separately.

```json
{
  "captainPriorities": [
    { "runtime": "ApiEndpoint", "endpoint": "mep_your_grok_endpoint" },
    { "runtime": "Codex", "minimumTier": "Premium" },
    { "runtime": "ClaudeCode", "minimumTier": "Premium" },
    { "runtime": "Mux", "endpoint": "local", "model": "gpt-oss:120b" }
  ],
  "priorityAvailability": "FallThrough",
  "priorityExhaustion": "Wait"
}
```

## Select a specific API endpoint

Create an Inference model endpoint under Configuration > Endpoints, then configure an API Endpoint captain with that endpoint (`ModelEndpointId`). In persona create/edit, the priority target picker lists each visible inference endpoint by name, provider, and model alongside the CLI harnesses. Select the endpoint directly. Embedding endpoints are excluded. Dashboard, mobile (including the create/edit form's Captain priorities action), and TUI editors preserve its ID when saving, reordering, or duplicating priorities.

REST, MCP, and the TypeScript and .NET clients use the same contract:

```json
{ "runtime": "ApiEndpoint", "endpoint": "mep_your_grok_endpoint" }
```

This matches only captains bound to that endpoint, even if another endpoint uses the same provider and model. Renaming an endpoint does not change its identity. A deleted or unavailable endpoint remains an explicit criterion and follows the availability/exhaustion policy; it is never silently replaced by another API endpoint. Legacy `{ "runtime": "ApiEndpoint" }` entries still match any API captain. `Model` remains an optional constraint on the captain's explicit model override; it does not resolve the endpoint's default model. Routing selects configured captains and does not create a captain or change its launch configuration.

The .NET SDK uses the shared `PersonaCaptainPriority` model:

```csharp
persona.CaptainPriorities.Add(new PersonaCaptainPriority
{
    Runtime = AgentRuntimeEnum.ApiEndpoint,
    Endpoint = inferenceEndpoint.Id
});
await client.UpdatePersonaAsync(persona.Name, persona);
```

## Busy classes and exhausted hierarchies

`priorityAvailability` defaults to `FallThrough`: Armada selects an eligible idle captain from the first entry with an available match, trying lower entries when earlier ones are busy, missing, quarantined, or otherwise ineligible. `WaitForFirstPriority` considers only the first entry and waits for it, including when no configured captain matches. It never falls through and ignores the exhaustion setting.

With `FallThrough`, `priorityExhaustion: Wait` keeps unmatched work Pending until a later scheduling tick. `UseEligibleCaptain` explicitly permits normal persona/tier routing after every entry is exhausted. Empty priorities preserve legacy behavior regardless of these policies.

All hierarchy candidates must satisfy mission ownership/tenant boundaries, AllowedPersonas, the mission's tier floor, and availability. Within a matched class, PreferredPersona breaks ties, followed by stable captain ID order. Hierarchy choices are reevaluated on every assignment attempt and are not saved as RequestedCaptainId.

## Missions without a persona

A mission dispatched without a persona (a plain dispatch with no pipeline, `create_mission`, a fleet action without a persona) is routed as a **Worker**: the Worker persona's captain priorities, AllowedPersonas fences, and the voyage's Worker or wildcard captain assignment apply to it. Its stored Persona stays empty.

## When a captain cannot start

If launching the chosen captain fails (for example its CLI is not installed on the host the mission was placed on), the mission returns to Pending and assignment backs off: 30 s, 1 m, 2 m, 4 m, then every 5 minutes. The mission's assignment blocker shows `LaunchFailing` with the reason and the next attempt time. After six failures caused by a missing CLI the mission fails (Infra) with a reason saying to connect the Harbor and turn on Require Harbor for launch. A captain marked Stalled by a provider condition (usage limit, authentication, model unavailable) is returned to Idle after 15 minutes and tried again.

## When a model endpoint hiccups

A captain that exits because its model endpoint failed transiently (HTTP 500, 502, 503, or 504, a dropped connection, a timeout) or because a mux run reached its iteration or token budget is relaunched in the same dock, with its edits so far intact and a note in its instructions to inspect `git status` and `git diff` and continue, up to `maxRecoveryAttempts` times. The voyage keeps running; only when the attempts are used up does the mission fail. Iteration and budget stops do not count toward the captain's crash-loop quarantine.

## Checks and escalation for local models

A Mux or API-endpoint captain that finishes a work stage (any persona except Product Manager, Architect, Judge, and Recorder) is checked before the stage moves on: the vessel's workflow-profile `BuildCommand` and `UnitTestCommand` run in the dock (on its Harbor), with a 15-minute limit. A failure resumes the captain with the command output. When a local-model stage still cannot finish after its resumes, its work is committed on the branch and the mission is handed, with a note, to the captain that ran the voyage's Architect (else Product Manager) stage, once. Only after that does the mission fail.

## Explicit overrides and legacy defaults

An explicit mission RequestedCaptainId takes precedence, followed by the voyage's exact per-persona captain assignment, then its wildcard (`*`) assignment. Those choices retain legacy behavior: an available requested captain bypasses AllowedPersonas and tier checks; a busy or deleted one falls back through ordinary persona/tier routing. Ownership and quarantine checks still apply. These explicit overrides bypass the hierarchy.

A nonempty hierarchy supersedes Persona.DefaultCaptainId. Without a hierarchy, the default captain remains a preference: it is tried first and can fall back by tier. Dispatch pickers leave the captain on automatic routing when priorities are configured, even if a legacy default remains stored. An operator can still select an explicit override.

Pipelines and Architect-generated Worker missions resolve their personas through the same assignment path. Repeated stages using the same persona use the same hierarchy. Configure different personas when their routing should differ.

## Configuration and diagnostics

The dashboard's persona create/edit forms include ordered matching fields, add/remove/move controls, and availability policies. Mobile persona detail opens the same structured controls from Captain priorities. The terminal forms support add, edit, delete, and Alt+Up/Down ordering. Duplication copies the hierarchy and its policies.

Persona create accepts all routing fields. Partial REST/MCP updates preserve omitted routing fields; `captainPriorities: []` clears the hierarchy. Invalid classes or enum values are rejected. Legacy defaultCaptainId clearing remains unchanged: REST null/omitted clears it, while MCP uses an empty string.

A Pending mission's assignmentBlocker uses the hierarchy to decide whether any captain is eligible. Successful hierarchy assignment logs the persona, chosen captain ID, and matched priority number. No matches stay Pending rather than failing the pipeline. See [SCHEDULING.md](SCHEDULING.md) for other blockers.

Startup migration 83 adds priority_availability and priority_exhaustion columns to personas and an ordered persona_captain_priorities child table across SQLite, PostgreSQL, MySQL, and SQL Server. Parent and child writes are atomic; deleting a persona cascades to its priorities. Existing personas receive an empty hierarchy and retain their routing behavior.
