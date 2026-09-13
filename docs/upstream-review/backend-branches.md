# Read-only vessel branch inspection

This slice adds `GET /api/v1/vessels/{id}/branches`. It reads local branch
refs from the vessel repository and returns the configured default branch,
current branch, tip metadata, and ahead/behind counts. It does not fetch,
checkout, create, update, or delete refs.

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

The implementation intentionally excludes upstream push and merge endpoints.
Those operations need separate proof for managed bare repositories, working
checkouts, ref validation, serialization, protected branches, remote
destination, and immutable landing checks.

Validation includes registered API fixtures for global admin, tenant admin,
ordinary owner, same-tenant other user, cross-tenant user, and anonymous
access. The fixtures cover working, bare, and detached repositories, a missing
default ref, a missing repository beside an unrelated repository, and a branch
name that requires JSON/URL encoding. They verify exact response metadata,
branch tip data, sanitized errors, and `git show-ref` equality before and
after inspection for working and bare repositories. Cancellation remains
propagated, and Git comparisons use fully qualified local refs.
