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
each output stream at 1,048,576 UTF-8 bytes and use the existing Git timeout.
Overflow is an error, never a verified negative search result.

The dedicated completion operation changes only snapshot JSON. It compares the
exact seed, current captain, vessel and active state. Concurrent completion has
one winner. Generic dock updates preserve evidence only while tenant, user,
vessel, captain, worktree, branch and active state remain identical. A change
clears the snapshot. Late enrichment cannot restore reclaimed evidence or process
ownership. Provider dock reads retain UserId so an unrelated update preserves ownership.

New migration versions are SQLite 85, PostgreSQL 86, MySQL 77 and SQL Server 80.
Each adds nullable snapshot text. MySQL requires full Unicode LONGTEXT. Applied
migration declarations remain unchanged. The migration fixture rejects wrong
types, nullability, defaults and restricted MySQL encoding, and checks interrupted
restart, equivalent existing data and unchanged history.

## Validation

Provider scenarios verify populated upgrades, equivalent and incompatible columns,
interrupted startup, Unicode storage, and unchanged migration history. Service
cases cover actual provisioning, immutable seed identity, bounded query output,
redaction, missing objects, moved HEAD, and ownership changes. Re-run the current
[database scenarios](../../test/Armada.Test.Database/README.md) and
[test gate](../TESTING.md) for the tree being reviewed.
