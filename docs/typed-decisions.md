# Typed Decisions

An index of where the typed-decision system is documented. This page holds no
rules; each link owns its subject.

- [Configuration and administration](ops/08-configuration-and-administration.md#typed-decisions)
  — the `typedDecisions` settings, per-decision modes and thresholds, events,
  and the global kill switch.
- [MCP API: Captain Typed Decisions](MCP_API.md#captain-typed-decisions) — the
  mission-scoped captain tools and their arguments.
- [Personas](PERSONAS.md) — the persona and pipeline seams that consult a
  decision, such as Linter finding routing.
- [D7 `leak_hunk` design](design/typed-decision-leak-hunk.md) — the per-hunk
  leak classifier behind the boundary hook.
- [D8 `log_watch` design](design/typed-decision-log-watch.md) — running-captain
  log screening that posts a course flag.
