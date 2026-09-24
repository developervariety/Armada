# Banned-diff patterns

A configurable, deterministic diff guard. It fails a change whose ADDED lines match any
operator-configured pattern, so a deployment can forbid a code path without a code change —
and without naming its domain in this product's source.

## Why config, not code

An absolute "this code path is banned" rule is deployment-specific: what one operator must
forbid means nothing to another. So the product carries the mechanism and the deployment
carries the patterns. The list ships EMPTY: with no rules the guard is a no-op.

## Configure

Under `bannedDiffPatterns` in `settings.json`, a list of rules; each is a name, a .NET
regex, and a reason:

```json
"bannedDiffPatterns": [
  { "name": "no-example-path", "pattern": "\\bForbiddenApi\\b", "description": "ForbiddenApi is not allowed in this deployment." }
]
```

The list hot-reloads in place — a change takes effect without a restart.

## Behaviour

- Runs inside the Slop check (a required check), before the slop reading, and is **not
  suppressible**: unlike a slop finding, a banned-pattern match has no `slop-allow` override.
  A match fails the check, which blocks the mission's definition of done.
- Reads the whole reviewed diff. A diff larger than 64 MiB fails the Slop check with a
  reason that names the size, because a partial reading could miss a banned line.
- Reads only **added** lines; a context or removed line introduces nothing. A pure comment
  line is skipped — the guard bans the path, not the mention of it.
- A rule whose pattern is not a valid regex is skipped and named in the output, so one bad
  rule never blocks every change or throws into the check. A pathological pattern that times
  out on a line is treated as no match on that line.

## Note

The guard is domain-neutral by construction. A deployment's forbidden paths (for example a
banned device-provisioning service in a diagnostics fleet) are expressed entirely as
configuration; no such rule is compiled into this repository.
