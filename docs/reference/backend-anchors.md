# Dock Git-anchor evidence

Dock provisioning records its actual full HEAD commit in a versioned typed
snapshot. This happens after worktree creation and before dock insertion. The
existing start-ref and stage-base checks remain the landing authority. The
snapshot is context evidence, not a new acceptance gate.

Prompt enrichment reads that commit, not current HEAD. Path lookup, history and
prior-art searches address the same immutable revision. Target tip is a separate
observation. A completed or incomplete snapshot is reused. Missing, malformed or
unsupported old snapshots remain unavailable. Direct prompt generation without a
dock retains the existing transient context behavior.

Storage is limited to 32 KiB, eight paths, five commits per path, six terms and
three sample locations per term. Stored paths are repository relative. Commit
subjects use the shared secret redactor. Query failures use fixed error codes;
raw command output and exception messages are not stored. Omitted or invalid
details make the evidence incomplete. New history, tree and search commands cap
each output stream at 1,048,576 characters and use the existing Git timeout.
Overflow is an error, never a verified negative search result.

The dedicated completion operation changes only snapshot JSON. It compares the
exact seed, current captain, vessel and active state. Concurrent completion has
one winner. Generic dock updates preserve evidence only while tenant, user,
vessel, captain, worktree, branch and active state remain identical. A change
clears the snapshot. Late enrichment cannot restore reclaimed evidence or process
ownership. PostgreSQL and MySQL dock reads now retain UserId; the old omission
made an unrelated update appear to change ownership.

New migration versions are SQLite 85, PostgreSQL 86, MySQL 77 and SQL Server 80.
Each adds nullable snapshot text. MySQL requires full Unicode LONGTEXT. Applied
migration declarations remain unchanged. The migration fixture rejects wrong
types, nullability, defaults and restricted MySQL encoding, and checks interrupted
restart, equivalent existing data and unchanged history.

## Validation status

The initial persistence case failed on all four providers before the mappings and
migrations. After the storage and dock ownership repair, ordinary provider runs
passed 63 SQLite, 63 PostgreSQL, 64 MySQL and 63 SQL Server cases on the same
failed databases. These include concurrent completion, branch reuse, reclaim and
create/update/clear/reopen ownership checks.

The final focused service run passed 177 cases, including malformed optional JSON,
actual provisioning capture, stable complete/incomplete reuse, missing old data,
Unicode/colon paths, moved HEAD, dirty files, ambiguous suffixes, secret-bearing
sample paths, cancellation, missing commits and oversized query output.

The expanded matrix passed 45 provider/scenario combinations: 10 SQLite, 11
PostgreSQL, 12 MySQL and 12 SQL Server. The two new scenarios separately test an
equivalent populated column and failure immediately after the new column
statement, followed by restart against that database. All 274 protected migration
declarations and all 10 guard controls pass.

The first attempts exposed test fixture errors: missing command registration and
a second-statement checkpoint for a one-statement migration. Those attempts are
retained as failures. The corrected scenarios pass on every provider; no history
or acceptance check was weakened.

The combined run passed 4,045 unit, 954 automated API and 183 runtime tests, with
no failures or skips, in 307 seconds. A final seed-consistency guard was then
added: changing a loaded seed cannot alter the original provisioning identity.
Its four-provider rerun passed 63/63/64/63 ordinary cases on the original failed
databases, and all 177 focused service cases passed. The final solution build
reported 106 warnings and zero errors. This is an incremental warning count,
not a clean-build comparison with the original 212-warning baseline.

Dashboard display remains a separate objective. This source change is not a
production deployment or proof of the current running image.
