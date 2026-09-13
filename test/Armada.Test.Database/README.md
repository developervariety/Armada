# Database preservation tests

Run through `dotnet run`, not `dotnet test`. Use a dedicated test database.
The runner creates fixture data and normally deletes that fixture data after
checks. It does not represent deployment approval or a production backup test.

```sh
dotnet run --project test/Armada.Test.Database --framework net10.0 -- \
  --type sqlite --filename ./test-fresh.db --migration-scenario fresh
```

For a server provider, supply `--type postgresql|mysql|sqlserver`, `--hostname`,
`--port`, `--username`, `--password` and `--database`. Provision the database
before running the tests. Keep credentials outside source and saved logs.

Every migration scenario refuses a nonempty database. Use a different empty
database for each invocation. A failed scenario leaves its database available
for diagnosis; do not rerun fixture setup against it. To check a saved database
without constructing a scenario, omit `--migration-scenario`.

| Scenario | Providers | Proof |
| --- | --- | --- |
| `fresh` | All four | Initial schema, repeat startup and ordinary persistence cases |
| `upgrade-51` | All four | Preserved migrations through v51, Unicode data, remaining migrations, unchanged old history |
| `partial-first` | All four | Failure after first DDL statement, restart and final schema |
| `partial-52` | All four | Populated objective normalization interrupted before commit, data rollback and successful restart |
| `concurrent-fresh` | All four | Two initializers; SQLite explicitly synchronizes both initial version reads |
| `partial-identity` | All four | Failure between default user and credential inserts; atomic retry and deliberate credential removal |
| `partial-anchor` | All four | Failure after the snapshot column statement; restart against the same database with unchanged old history |
| `anchor-migration` | All four | Populated dock upgrade, incompatible and equivalent columns, interrupted restart and unchanged history |
| `backend-migration` | All four | Populated metadata upgrade, nullable fields, incompatible columns, exact commit history and interrupted restart |
| `preview-migration` | All four | Populated upgrade, equivalent values, incompatible type/null/default, interrupted restart; MySQL also rejects restricted text encodings |
| `catalog-guards` | Server providers | Wrong type, nullability, default and index rejection; corrected restart |
| `mysql-compat` | MySQL | Populated Unicode backfill, no repeat row update, damaged mapping, duplicate/orphan/FK/default rejection |
| `sqlserver-corrections` | SQL Server | Equivalent and incompatible pre-staged v59/v68 objects; separate correction evidence and complete model value |

The server catalog fixture also tests disabled primary-key and foreign-key
state where supported. PostgreSQL's historical UTC conversion runs with a
non-UTC session timezone and checks microsecond preservation.

The legacy MySQL compatibility fixture creates and removes only its own initial
fixture tables after the empty-database check, then runs a fresh installation.
It is a helper compatibility test, not a production migration-history fixture.
The v51 fixture executes real preserved declarations; it is a reconstruction,
not a production backup. Applied version, description and timestamp rows must
remain unchanged across restart and upgrade.

Scenario assertions fail the process before the ordinary runner if they fail.
They are separate from the 62 ordinary cases (63 on MySQL). Ordinary cases cover
selected fields and behavior, not every property or every provider capability.
See [the foundation field matrix](../../docs/upstream-review/foundation.md) and
[additional entity fields](../../docs/upstream-review/foundation-entities.md)
for known gaps and the boundary of this evidence.

The six added ordinary cases verify each vessel preview setting through create,
reopen, update and another reopen. They do not certify landing enforcement.
Unknown options, missing values and invalid ports fail before database work.

Set `ARMADA_TEST_RESULTS_DIRECTORY` to a new empty directory to write a JSON
case manifest. Keep the executable's portable PDB beside it: source checksums
come from the build, not from files read after the run. Each provider manifest
names the actual driver and selected migration scenario. Failed initialization
can produce no manifest; require both a zero process exit and a fresh manifest.

Eight backend field cases cover tier metadata, requested captain, scanner
preferences and voyage planning provenance. See [backend storage](../../docs/upstream-review/backend-storage.md) for behavior limits.

`anchor-migration` checks the populated dock snapshot upgrade, incompatible and
equivalent columns, partial-failure restart and unchanged applied history on all
four providers. See [backend anchor evidence](../../docs/upstream-review/backend-anchors.md).
