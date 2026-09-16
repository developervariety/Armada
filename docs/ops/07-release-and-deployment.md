---
topic: "Release And Deployment Workflow"
summary: "The release-to-deployment record flow, the self-deploy preflight and supervised cutover, and how to deploy a new Admiral image."
read_when: "Shipping a release, verifying or rolling back a deployment, or upgrading the running Admiral."
applies_to: orchestrator
tier: leaf
---
# Release And Deployment Workflow

Use a workflow profile for repeatable commands. Use named environments for
rollout targets.

1. Create a release and link its objective, voyages, missions, artifacts, and
   Checks.
2. Move the release through its supported states only when evidence permits.
3. When a release transitions to Shipped and the `cdWebhook` setting is
   configured, the admiral POSTs a `release.shipped` evidence payload to the
   configured endpoint; delivery or failure is recorded as a
   `release.webhook.*` event on the release. A webhook failure never blocks the
   release update.
4. Create a deployment against a named environment.
5. Approve it when the environment requires approval.
6. Run deployment verification.
7. Use rollback on the same deployment record if verification fails.
8. Link the related incident and runbook execution.

See [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for the detailed procedure.

### Deploying a new Admiral image

In production the Admiral runs as a container, so a code deploy rebuilds and
swaps the image; the self-deploy path below restarts only a process-owned
Admiral. Deploy an image this way:

1. Engage the dispatch hold and confirm no active voyages
   ([Dispatch Hold](09-mcp-tool-catalog.md)). A rebuild interrupts running work.
2. Fetch first, then fast-forward the deploy checkout (`git fetch origin`, then
   `git merge --ff-only origin/main`); a peer may have landed since the last
   sync.
3. Rebuild the image with `scripts/common/rebuild-local-image.sh`, which retains
   the running image and the current tag under rollback references before it
   builds. An inspection, retention, or build failure stops the deploy.
4. Prove the candidate before the swap: take a fresh database backup, restore it
   into an isolated copy, and boot the candidate against that copy so migration
   compatibility and restart safety are verified without touching production.
   Run the candidate the way the container does -- as its non-root runtime user
   with `HOME` set to that user's home -- so it reads the mounted settings
   rather than validating a fresh default database.
5. Recreate the container. Verify that health `StartUtc` postdates the restart
   and that the live schema version matches the newest migration.
6. A restart clears the dispatch hold, so re-engage it when automatic dispatch
   must stay held, and refresh the host Helm CLI to match the new image.

The dashboard build is separate: the image does not bake it. The Admiral serves
the React dashboard from its data directory, so deploy dashboard changes by
syncing the built assets (`scripts/common/deploy-dashboard.sh`) -- no image
rebuild or restart. `docs/DOCKER.md` ("Upgrading a running deployment",
"Database Client Tools") and `DELIVERY_OPERATIONS.md` have the detailed steps.

### Self-deploy preflight

Self-deploy is opt-in (`selfDeploy.enabled` defaults to `false`). After a
successful Release build, the service runs an
injected safety preflight before it prepares the supervised cutover. The preflight
must prove all three conditions: a recoverable backup was created and
validated, restore verification passed, and the candidate server was validated.
If any condition fails, or if the provider returns no result or throws, the
service records the failure, opens an incident, and keeps the current admiral
running.

The native backup provider now supports SQLite, MySQL, PostgreSQL, and SQL
Server. It creates a unique artifact and restores it into a unique isolated
file or database before it reports backup and restore proof. Native utility
passwords are passed through environment variables and are never command
arguments. SQL Server backup paths must be visible to the SQL Server host.
The isolated target stays available for the candidate check and is removed
through the provider cleanup operation after that check. Cleanup requires the
opaque ownership token returned by the provider, so an arbitrary file or
database name cannot be deleted. The backup artifact is kept for rollback.
MySQL requires every source table to use InnoDB for the single-transaction
snapshot contract. MySQL, PostgreSQL, and SQL Server also require their native
client utilities (`mysqldump`/`mysql`, `pg_dump`/`createdb`/`pg_restore`/`psql`,
and ODBC 18 `sqlcmd`) on the executing host. SQL Server utility calls set `-Nm`
or `-No` from `DatabaseSettings.RequireEncryption` and pass `-C` explicitly to
match the typed server-certificate trust contract; a wrapper must not weaken
TLS checks.

The native preflight is the default for every cutover. It runs against the
running admiral's database and writes disposable backups under
`<dataDirectory>/self-deploy/backups`. SQL Server also needs
`selfDeploy.sqlServerBackupDirectory`, a path visible to the SQL Server host;
without it the preflight fails with
`sqlserver_server_backup_directory_not_configured`. Any failed step, missing
native utility or unverifiable private storage refuses the cutover before any
process starts or restart record is written. `SelfDeployCandidateProcessValidator`
writes temporary settings with the effective connection fields and the owned
isolated target, then runs the candidate DLL with `--validate-database`. It
requires both a zero process exit and the validation pass marker before it
reports candidate proof. Candidate validation inspects the isolated restored
copy only; the running database and migration history are not modified by this
provider. A build success or a backup proof alone never permits cutover.
On Unix, the operation directory and settings file are created with owner-only
permissions (`0700` and `0600`); an existing directory with public permission
bits or a symlink is rejected. On Windows, private storage uses owner-only ACLs: each new directory and file
gets a protected descriptor (inheritance removed) owned by the current user,
with one rule granting that user full control, inherited by children. SYSTEM
and Administrators are not granted. The descriptor is read back after every
change; anything else fails closed with `private_storage_acl_unverified`
(`private_storage_acl_apply_failed` when it cannot be applied). An existing
directory is verified, never modified. A path name or temp-root location is
not treated as proof. The rollback artifact remains
inside that private directory. The native runner bounds each captured output
stream and observes every pipe task after a bounded timeout. It closes standard
input only when the request redirected it. A truncation marker fails candidate
proof, so a noisy command cannot hide its validation result; an inherited child
pipe returns the stable `native_command_io_drain_timeout` failure.
The Release build uses this same runner with an argument list and a configured
build timeout. Caller cancellation and timeout both terminate the process tree
and observe the redirected pipes before the build result is returned; a build
that cannot be terminated or drained fails closed.

### Self-deploy supervised cutover

Self-deploy restarts only a process-owned admiral. Inside a container the
container runtime owns the admiral process, so self-deploy fails closed with
`container_host_requires_external_deploy` and the host-side image deployment
remains the only deploy path there.

The running admiral performs these steps, and any failure opens an incident
and keeps it as the owner:

1. Capture the running server directory as the rollback artifact before the
   build, because the build may overwrite that directory.
2. Build, then run the safety preflight.
3. Capture the candidate build directory as the candidate artifact.
4. Read the schema version and its own identity (process id and start time).
5. Create the restart record in `Prepared`. A new record is refused while an
   unresolved or unreadable record exists (`restart_in_progress`).
6. Start the supervisor from the rollback artifact with
   `--self-deploy-supervise <operation>` and wait up to
   `selfDeploy.handshakeTimeoutSeconds` for `Armed`. If it does not arm, the
   admiral aborts the record and stops the supervisor by identity.
7. Write `ExitRequested` and exit.

Artifacts live under `<dataDirectory>/self-deploy/releases/<sha256>`. The
digest covers every relative path, size and content hash. Files are read-only,
symlinks are refused, and each artifact is re-verified before every launch.
The restart record is `<dataDirectory>/self-deploy/restart-record.json`. Every
change is a compare-and-swap under an exclusive record lock, written to a
flushed temporary file and renamed into place. A separate supervisor lock
allows one supervisor or recovery run at a time.

The supervisor verifies both artifacts and the recorded admiral identity, then
writes `Armed`. It waits for `ExitRequested`, then waits up to
`selfDeploy.oldProcessExitTimeoutSeconds` for that exact process to exit. An
admiral that does not exit is terminated by identity; its descendants are not.
A reused process id counts as exited and is never signalled. If exit cannot be
confirmed, or the process state cannot be verified, the record fails and
nothing is launched.

Launches are recorded before and after they happen (`CandidateStarting` with
and without the process identity). Health requires
`GET http://127.0.0.1:<admiralPort>/api/v1/status/health` to report `healthy`
with a `StartUtc` no earlier than the launched process, within
`selfDeploy.healthTimeoutSeconds`. The result is one of:

- The candidate is healthy: `Committed`.
- The candidate exits or stays unhealthy: the supervisor stops it, confirms
  the exit, rereads the schema version and launches the rollback artifact,
  giving `RolledBack` or `Failed`.
- The candidate advanced the schema, or the schema version cannot be read: the
  previous binary is not started, giving `RollbackBlocked`. Restore the
  retained preflight backup before starting the previous binary; writes made
  after the cutover are lost by that restore.

While a record is non-terminal, a normal admiral start exits with code 3 and
names the record. Only the process the supervisor launched for that operation
may start (`ARMADA_SELF_DEPLOY_OPERATION_ID`). After a supervisor or host
interruption, run the server with `--self-deploy-recover`:

- If the recorded admiral still runs before any stop, the record aborts and
  that admiral stays the owner.
- If the interruption came before a candidate launch, the rollback artifact is
  launched. Recovery never launches the candidate.
- A running candidate is committed only if it proves health; otherwise it is
  stopped and rolled back.
- A launch whose identity was not recorded, two running recorded processes, or
  an unverifiable process state all fail without starting anything.

Exit code 0 means the record proves a healthy owner or no record exists.

The release store is bounded on every cutover, after the candidate capture. It
keeps:

- the running release and the rollback release;
- every release named by an unresolved restart record;
- the newest `selfDeploy.retainedPreviousReleases` other releases (default 2,
  range 0 to 20).

Pruning holds the record lock and removes nothing when the record is
unreadable. It ignores entries that are not digest directories and never
follows a symlink. A pruning failure is reported as
`self_deploy.release_prune_failed`, and it does not block the cutover.

Current limits: supervised processes inherit the supervisor's standard streams.
The Windows ACL path has a Windows-only test that has not yet been run on a
Windows host.

### Self-deploy rehearsal

`scripts/common/rehearse-self-deploy-cutover.sh` rehearses the supervised cutover
on an isolated process host. It uses real server binaries and disposable
SQLite copies. Run it only on a workstation or disposable host, never against a
production data directory, database or container host. It refuses to run
inside a container.

```bash
scripts/common/rehearse-self-deploy-cutover.sh \
  --rollback-dll <build>/Armada.Server.dll \
  --candidate-dll <candidate build>/Armada.Server.dll \
  --sqlite-source <copy of a real database>.db
```

Each scenario gets its own private data directory, database copy and free
ports. The script starts the rollback binary with
`--self-deploy-rehearse <candidate dll>`. That mode is refused unless
`ARMADA_SELF_DEPLOY_REHEARSAL=isolated-disposable` and `ARMADA_DATA_DIRECTORY`
(or its alias `ARMADA_DATA_DIR`) are both set. It runs the real cutover (container check, rollback capture,
native preflight, candidate capture, retention, restart record and supervisor
handshake) and skips only the git sync and the Release build. The script
requires `dotnet`, `python3` and `curl`.

1. **Preflight refusal.** The candidate is not an assembly. The rehearsing
   admiral prints `candidate_database_validation_failed` and exits 1, and no
   restart record exists.
2. **Commit.** The record ends `Committed`, the previous admiral exits 0, and
   the candidate answers health.
3. **Supervisor kill.** `ARMADA_SELF_DEPLOY_REHEARSAL_HOLD_SECONDS` holds the
   supervisor after the candidate launch is recorded. The script sends
   `kill -9` to the supervisor while the record reads `CandidateStarting`. A
   normal start then exits 3 with `restart_in_progress`. `--self-deploy-recover`
   exits 0 at `Committed` or `RolledBack`, with exactly one recorded owner
   running and healthy.

The script stops every recorded process on exit. It removes the work
directory after success and keeps it after a failure. Scenarios the unit suite
covers with real processes, but not with the server binary, are not rehearsed
here:

- an unhealthy candidate rolled back after health;
- a schema advance blocking rollback;
- a hung admiral stopped by identity.

Rehearse a server database provider on a disposable database separately before
enabling self-deploy there.

The real utility checks are separate and disabled by default; the default guard
performs no database work. To run them against disposable provider databases, set
`ARMADA_SELF_DEPLOY_INTEGRATION=1`,
`ARMADA_SELF_DEPLOY_INTEGRATION_SCOPE=isolated-test-databases`,
`ARMADA_SELF_DEPLOY_CANDIDATE_DLL`, and
`ARMADA_SELF_DEPLOY_BACKUP_DIRECTORY`. Set typed provider variables with the
`ARMADA_SELF_DEPLOY_SQLITE_*`, `ARMADA_SELF_DEPLOY_MYSQL_*`,
`ARMADA_SELF_DEPLOY_POSTGRESQL_*`, and `ARMADA_SELF_DEPLOY_SQLSERVER_*`
prefixes (`FILENAME` for SQLite; `HOSTNAME`, `PORT`, `USERNAME`, `PASSWORD`,
and `DATABASE_NAME` for server providers). SQL Server also requires
`ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY`, which must be visible to the
SQL Server host. The suite uses the injected native runner, so PATH wrappers
can route utilities into isolated provider containers without changing the
application database configuration.
