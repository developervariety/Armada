# Database preservation tests

Run through `dotnet run`, not `dotnet test`. Use a dedicated test database.
The runner creates fixture data and normally deletes that fixture data after
checks. Check-run, release and deployment cases verify inclusive creation-time
bounds, empty ranges, and filtered totals across pages through the real database
methods on each provider. It does not represent deployment approval or a production backup test.

```sh
dotnet run --project test/Armada.Test.Database --framework net10.0 -- \
  --type sqlite --filename ./test-fresh.db --migration-scenario fresh
```

For a server provider, supply `--type postgresql|mysql|sqlserver`, `--hostname`,
`--port`, `--username`, `--password` and `--database`. Provision the database
before running the tests. Keep credentials outside source and saved logs.

### Native backup case

`Backup_Native_Verified_Archive_Provider_Manifest_And_Restore_Contract` takes a real provider-native backup through
the shared backup service. On a server provider it needs that provider's client tools on `PATH`: PostgreSQL
`pg_dump createdb pg_restore psql dropdb`, MySQL `mysql mysqldump`, SQL Server `sqlcmd`. SQL Server also needs
`ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY`, a directory the SQL Server process can write. If a prerequisite is
missing the case is a named skip (for example `native_client_missing_pg_dump`), listed under "Skipped Tests" and
counted in the summary; it never passes without running.

On a host whose databases run in containers without client packages, run the clients inside those containers:

```sh
scripts/common/install-database-client-wrappers.sh /tmp/armada-db-clients
export PATH=/tmp/armada-db-clients:$PATH
export ARMADA_CLIENT_POSTGRESQL_CONTAINER=<postgres-container> ARMADA_CLIENT_POSTGRESQL_PORTS=<host-port>:5432
export ARMADA_CLIENT_MYSQL_CONTAINER=<mysql-container> ARMADA_CLIENT_MYSQL_PORTS=<host-port>:3306
export ARMADA_CLIENT_SQLSERVER_CONTAINER=<sqlserver-container> ARMADA_CLIENT_SQLSERVER_PORTS=<host-port>:1433
export ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY=/var/opt/mssql/data
export ARMADA_DATA_DIRECTORY=$(mktemp -d)
dotnet Armada.Test.Database.dll --type postgresql --hostname 127.0.0.1 --port <host-port> \
  --username <user> --password <password> --database <empty-database>
```

The wrapper forwards passwords by environment name only and streams `pg_dump --file`, `mysqldump --result-file`
and the `pg_restore` input between the host and the container. Set `ARMADA_DATA_DIRECTORY` to a disposable directory
so the archive never includes a real `settings.json`. A SQL Server backup file stays in the container directory;
remove it after the run.

Every migration scenario refuses a nonempty database. Use a different empty
database for each invocation. A failed scenario leaves its database available
for diagnosis; do not rerun fixture setup against it. To check a saved database
without constructing a scenario, omit `--migration-scenario`.

Use `--migration-scenario harbor-enrollment-migration` to fault and restart the
Harbor enrollment migration. The scenario uses the selected provider and
checks the durable table after restart.

Use `--migration-scenario harbor-enrollment-guards` to stage malformed Harbor
enrollment tables for the selected provider. The driver must reject independent
nullability, type, default, and primary-key fixtures without changing applied
migration history. The scenario then replays the migration against an
equivalent preexisting table.

Use `--migration-scenario harbor-enrollment-combined` to run the captain
endpoint-link and Harbor enrollment migrations in their shipped order. The
scenario interrupts each migration, rejects an incompatible partial Harbor table
between restarts, and then restarts to completion. It checks applied history,
revocation persistence across a reopen, and compare-and-set enrollment and
revocation after restart.

A scenario that stops the schema below the newest version seeds every row with
SQL that names only the columns of the stop version, never through driver
create or update methods. Driver writes use the newest row shape, so they fail
as soon as a later migration adds a column to a seeded table.

| Scenario | Providers | Proof |
| --- | --- | --- |
| `fresh` | All four | Initial schema, repeat startup and ordinary persistence cases |
| `upgrade-51` | All four | Preserved migrations through v51, Unicode data, remaining migrations, unchanged old history |
| `partial-first` | All four | Failure after first DDL statement, restart and final schema |
| `partial-52` | All four | Populated objective normalization interrupted before commit, data rollback and successful restart |
| `concurrent-fresh` | All four | Two initializers; SQLite explicitly synchronizes both initial version reads |
| `partial-identity` | All four | Failure between default user and credential inserts; atomic retry and deliberate credential removal |
| `partial-anchor` | All four | Failure after the snapshot column statement; restart against the same database with unchanged old history |
| `postgres-legacy` | PostgreSQL | Historical operational types, unsafe-value rejection, rollback, unchanged history and repeated startup |
| `anchor-migration` | All four | Populated dock upgrade, incompatible and equivalent columns, interrupted restart and unchanged history |
| `backend-migration` | All four | Populated metadata upgrade, nullable fields, incompatible columns, exact commit history and interrupted restart |
| `preview-migration` | All four | Populated upgrade, equivalent values, incompatible type/null/default, interrupted restart; MySQL also rejects restricted text encodings |
| `memory-migration` | All four | Memory tables absent before the version, interrupted run uncommitted, committed once with unchanged old history, Unicode round trip, tenant key uniqueness, guarded update and cascade delete |
| `catalog-column-prune` | All four | Populated scope-named playbooks, voyage links, snapshots, default-playbook entries, named pipelines, the pruned persona and its templates, pack hints and threshold columns beside operator rows and native memory; interrupted restart, deletion of only the named rows and their references, dropped table and columns, unchanged history on repeat startup |
| `reviewer-persona-prune` | All four | Built-in reviewer personas and templates that are unreferenced, named by a pipeline stage, or named by a captain allow-list beside an operator persona and template; interrupted restart, deletion of only the unreferenced persona and its template, unchanged history on repeat startup |
| `mission-input-wait-cancel` | All four | Missions stored with the `WaitingForInput` status, with and without an earlier failure reason and completion time, beside an InProgress and a Cancelled mission; interrupted restart with the stored status and reason rolled back, cancellation of only the waiting missions with the cancel reason ahead of any earlier one, completion time taken from the last update time when absent, every last update time unchanged, unchanged history and repeated startup |
| `ownership-migration` | All four | Ownership columns absent before the version, interrupted run uncommitted, only that version committed with unchanged old history, a persona written before the migration reads back tenant-wide with no owner, Unicode owner round trip across a reopen and an idempotent restart |
| `skipped-version` | All four | Full install, then two known ledger rows below the maximum removed; startup refuses twice with `SkippedMigrationVersionsException` naming both versions, provider and maximum, and records nothing; the removed rows are restored exactly and startup succeeds with unchanged history |
| `catalog-guards` | Server providers | Wrong type, nullability, default and index rejection; corrected restart |
| `mysql-compat` | MySQL | Populated Unicode backfill, no repeat row update, damaged mapping, duplicate/orphan/FK/default rejection |
| `sqlserver-corrections` | SQL Server | Equivalent and incompatible pre-staged v59/v68 objects; separate correction evidence and complete model value |
| `model-endpoint-guards` | All four | Incompatible pre-existing model endpoint table and malformed captain link are rejected without advancing history; interrupted table and captain-link migrations restart successfully |
| `harbor-enrollment-migration` | All four | Interrupted Harbor enrollment table/index DDL restarts, preserves migration history, and supports provider-backed reads |
| `harbor-enrollment-guards` | All four | Independent malformed Harbor enrollment schemas are rejected without advancing history; an equivalent preexisting table replays successfully |
| `harbor-enrollment-combined` | All four | Endpoint-link then Harbor faults, incompatible partial table rejection, restart, unchanged history, persisted revocation and conditional writes |

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
Read the runner's current registered cases and fresh provider manifests for
field coverage. A scenario passing does not certify fields it does not assert.

The six added ordinary cases verify each vessel preview setting through create,
reopen, update and another reopen. They do not certify landing enforcement.
Unknown options, missing values and invalid ports fail before database work.

Set `ARMADA_TEST_RESULTS_DIRECTORY` to a new empty directory to write a JSON
case manifest. Keep the executable's portable PDB beside it: source checksums
come from the build, not from files read after the run. Each provider manifest
names the actual driver and selected migration scenario. Failed initialization
can produce no manifest; require both a zero process exit and a fresh manifest.

Eight backend field cases cover tier metadata, requested captain, scanner
preferences and voyage planning provenance. These storage cases do not prove dispatch or landing behavior.

The planning-session case covers session and transcript create, read, update,
enumeration order, tenant and user scoping, the per-session sequence uniqueness
and the message cascade on SQLite and PostgreSQL. On MySQL and SQL Server, which
do not store planning sessions, it checks that the refusal names the provider.

`anchor-migration` checks the populated dock snapshot upgrade, incompatible and
equivalent columns, partial-failure restart and unchanged applied history on all
four providers. See [backend anchor evidence](../../docs/reference/backend-anchors.md).

Admission storage has a dedicated `admission-migration` scenario. It starts with
a populated pre-admission schema, stops after the first new statement, restarts,
and checks retained data and prior migration history. Ordinary cases also cover
reopen, concurrent writes, a state cycle, heartbeat invalidation, summary reads,
redaction and invalid stored evidence.
