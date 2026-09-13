# Effective landing configuration

The vessel and mission landing-preview REST responses expose `Configuration`.
It uses the same pure resolver as `MissionLandingHandler`. Reading a preview
runs no Git command, Check, merge, recovery action or dispatch.

Explicit landing mode precedence is voyage, vessel, then global settings.
`None` is an explicit override. If all three modes are absent, each legacy
boolean uses its voyage override or global value. The pull-request path takes
precedence over the push flag, including `AutoCreatePullRequests=true` with
`AutoPush=false`. Cleanup uses vessel policy, then global policy.

`Configuration.Source` identifies Voyage, Vessel, Global or Legacy resolution.
Its flags are current configuration, not evidence that a past landing ran.
A vessel preview has no voyage override. A mission preview reads the linked
voyage in the caller's scope. An unreadable linked voyage returns null
configuration and a `voyage_configuration_unavailable` error; it does not
silently present a lower-priority mode as the effective mode.

The existing latest Check summary is redacted with the shared secret filter
and bounded to 1,000 characters plus a truncation marker. A Failed Check with
exit code zero is no longer treated as Passed.

## Limits

This preview is advisory. Its legacy Check summary reads at most 1,000 scoped
runs, filters the branch, and reports whether any has Passed. It does not prove
that every required Check passed for the immutable work commit. It does not
certify Definition of Done or a completed landing. The actual Judge, Check,
landing and protected-path gates remain the execution authority. Auto-land,
DoD and recovery outcome projections remain separate backend work.

## Validation

The new voyage override and Failed-with-zero-exit cases both failed before
the repair. A further conflicting-legacy-flags case reproduced a preview of
Manual while the handler selects pull-request execution. It passed after
correcting preview precedence. The 36 focused workflow, landing, calibration
and auto-land safety cases passed with no failures or skips. Tests also cover
explicit None, vessel/global fallback, cleanup and an unreadable linked voyage.
The combined tree passed 4,115 unit, 967 API and 183 runtime tests with no
failures or skips. The solution build reported 105 warnings and zero errors.
No migration or deployment is included.

After integration with the separate learned-facts/Reflections removal, the
combined tree passed 3,809 unit, 967 API and 183 runtime tests with no failures
or skips. The unit manifest has 306 fewer cases, all from paths changed by
that removal; no registered suite was left unselected. The solution build
reported 105 warnings and zero errors. All 274 protected migration declarations
and the ten migration-guard controls still passed. This does not claim a new
four-provider matrix or deployment on the merged tree.
