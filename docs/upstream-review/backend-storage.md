# Backend metadata persistence

The storage repair preserves model metadata across restart. It adds no routing,
scanner enforcement, planning service or deployment policy.

| Field group | Before repair | Storage repair |
| --- | --- | --- |
| Captain.Tier | NULL after non-default create on all four providers | Nullable enum name; create, update and clear survive reopen |
| Mission.RequestedCaptainId and Tier | NULL after non-default create on all four providers | Map existing requested-captain column; add nullable tier column |
| Vessel.SecretScanEnabled, ProtectedPathPatterns, PrivateIdentifierDenylist | Defaults after non-default create on all four providers | Map existing columns; retain PostgreSQL INTEGER storage; legacy NULL lists become empty lists |
| Voyage.SourcePlanningSessionId and SourcePlanningMessageId | Server providers lose values; SQLite already preserves them | Add server provenance columns and mappings; retain SQLite behavior |

Eight named cases were added before changing provider code. SQLite reported six
failures and each server provider reported eight. The same failed databases then
passed after applying the repair: 61 ordinary cases on SQLite, PostgreSQL and SQL
Server, and 62 on MySQL. Each nullable field is created, updated, cleared and read
through separate driver instances. The scanner cases preserve non-default lists
and Unicode content across create, update and reopen.

## Migration contract

New versions are SQLite 84, PostgreSQL 85, MySQL 76 and SQL Server 79. Existing
migration declarations remain unchanged, including the prior preview migrations.
New columns default to NULL. MySQL planning identifiers retain full Unicode and
450-character storage. Incompatible restricted character sets are rejected.

The additive column validator is shared with the prior preview migration. It
checks type, nullability, default and generated-column state before accepting an
existing column. The metadata fixture seeds an old captain, rejects incompatible
columns without recording the version, retains an equivalent value, interrupts
the new migration and restarts it. Exact history checks occur at the migration's
commit checkpoint, before later migrations can run. The prior preview fixture
uses the same scoped checkpoint; later migrations cannot change its expected count.

The ordinary runner still includes the full-value MySQL Unicode uniqueness test.
No prefix index or hash-only identity comparison replaces that contract.

## Behavior limits

- NULL captain tier retains model-based inference. Explicit tier and requested
  captain metadata remain subject to existing persona/model and routing rules.
- The dedicated process-alive update is unchanged; general captain updates still
  cannot overwrite a newer liveness observation.
- Vessel scanner values are stored preferences. Actual landing enforcement uses
  global dock-boundary settings and ProtectedPaths. Combining per-vessel rules
  with those protections needs a separate policy decision.
- Voyage planning provenance does not enable unsupported server planning methods.
- Mission summaries, voyage vessel associations, dock anchor snapshots and service
  outcome DTO enrichment remain separate backend work.

## Validation

The final matrix passed all 37 provider/scenario combinations: eight SQLite,
nine PostgreSQL, ten MySQL and ten SQL Server. Each scenario also passed the
ordinary persistence cases (61, 61, 62 and 61 respectively). Coverage includes
fresh installation, partial-failure restart, historical upgrades, concurrent
initialization, identity rollback and provider catalog guards. The new metadata
scenario also proves that explicit SQL DEFAULT NULL is equivalent to an absent
default. Quoted text defaults remain distinct. Incompatible definitions still fail.

The full fork run passed 4,042 Unit, 950 automated API and 183 runtime cases,
with no skips, in 296 seconds. The final default-normalization change then passed
the complete provider matrix and solution build. The incremental solution build
emitted 106 warnings and zero errors; this is not a clean-build warning census.
All 274 original migration declarations remain unchanged and all ten source-guard
controls pass. Dashboard source was unchanged; its prior 77-case result belongs
to the discovery checkpoint. No image was built or deployed.
