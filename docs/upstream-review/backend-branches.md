# Vessel branch inspection and guarded writes

`GET /api/v1/vessels/{id}/branches` reads local branch refs from the vessel
repository and returns the configured default branch, current branch, tip
metadata, and ahead/behind counts. It does not fetch, checkout, create, update,
or delete refs.

Repository resolution uses the existing vessel local path first, then its
working directory. It does not guess a repository from a vessel name or scan
unrelated configured paths.
A missing vessel is a 404. A missing repository is a successful response with
an explicit error field. Git failures are returned as an explicit error field.
The response states whether the source was the vessel local path or working
directory, and reports attached, detached, bare, or unknown HEAD state.
The route applies the same administrator, tenant, and user ownership checks as
the vessel detail route.

The read contract is separate from `IGitService` through `IBranchInventory`,
so existing write-oriented test doubles do not acquire no-op branch controls.
The Git implementation uses `for-each-ref` and `rev-list --left-right
--count`; it never calls fetch or changes repository state.

## Guarded push and merge

Upstream exposes vessel branch push and merge. The fork adapts them to its
managed layout: a landing repository at the vessel local path plus a separate
working checkout, with landings serialized per vessel.
`VesselBranchWriteService` owns both writes. `POST .../branches/push` and
`POST .../branches/merge` call it after checking for a global or tenant
administrator scoped to the vessel's tenant. The listing's `WriteControls`
tells clients which actions to offer.

- **Explicit intent.** A push names source, target and remote. A merge names
  source, target and strategy. A merge never pushes. Upstream's merge-then-push
  default is not imported.
- **Ref validation.** Branch names are short names checked with
  `git check-ref-format`. Leading dashes, `refs/` prefixes and `HEAD` are
  rejected.
- **Destination.** Only `origin` is accepted. Its URL and any push URL must match
  the vessel `RepoUrl`.
- **History preservation.** A push checks remote ancestry, never forces and
  never deletes. A merge fast-forwards or writes an explicit merge commit, moves
  the target by compare-and-swap, and verifies that the new tip contains both the
  previous target and the source.
- **Clean state.** The working checkout must be attached and clean. A target
  checked out in any landing-repository worktree is refused.
- **Serialization.** `VesselRepositoryLock` is the single per-vessel slot. Mission
  landing and merge-queue entry processing hold it too. A write refuses as
  `vessel_busy` rather than queueing behind a landing.
- **Gates.** Branches of missions that are not `Complete`, branches with an active
  merge-queue entry, protected targets, and release branches that must use the
  merge queue are refused. Protected-branch and release classification use the
  same predicates as landing preview. Manual completion remains guarded by the
  manual-completion proof path, and these routes never change mission status.

Every refusal carries a reason code and changes no refs. Unit fixtures use real
origin, bare landing and working repositories for push, non-fast-forward
rejection, remote restriction, invalid refs, dirty, detached and missing
checkouts, fast-forward and merge-commit merges, conflicts, checked-out
targets, mission, queue and policy gates, the busy slot, and merge-queue
processing waiting on the shared slot. API fixtures cover anonymous,
same-tenant non-administrator and cross-tenant callers, remote refusal,
invalid refs, and a verified push.
