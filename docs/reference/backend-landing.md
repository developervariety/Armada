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
exit code zero remains Failed.

## Limits

This preview is advisory. Its legacy Check summary reads at most 1,000 scoped
runs, filters the branch, and reports whether any has Passed. It does not prove
that every required Check passed for the immutable work commit. It does not
certify Definition of Done or a completed landing. The actual Judge, Check,
landing and protected-path gates remain the execution authority. Use the separate [auto-land](backend-autoland.md),
[DoD](backend-dod.md), and [recovery](backend-recovery.md) reports for recorded outcomes.

When the newest scoped run did not pass but an older run did, the preview adds
a `latest_check_not_passed` warning. It is a warning, so `IsReadyToLand` does
not change: the preview does not become a smaller Check gate. The
`passing_checks_required` and `no_passing_checks` messages say that the result
comes from the preview scope. The dashboard renders every landing preview
through one card. That card labels a preview without errors "No blocking
preview issues" instead of "Ready To Land", and states that its Check evidence
is not the Check gate for the landed commit.

The vessel setting `RequirePassingChecksToLand` is read only by this preview.
No landing, merge-queue, Judge or Check execution path reads it. The accepted
disposition keeps it as an advisory preview signal. It does not enable or
disable actual Check, Judge or landing gates. Dashboard labels must state this
limit wherever they display or edit the setting.

## Validation

Behavioral tests cover voyage overrides, conflicting legacy flags, Failed Checks
with zero exit code, explicit None, vessel/global fallback, cleanup policy,
and unreadable linked voyages. Run the current gate described in
[Testing](../TESTING.md); past suite totals do not prove the current tree.
