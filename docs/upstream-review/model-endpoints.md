# Model endpoint persistence

This slice adds append-only model endpoint persistence to all four database
providers. It stores provider, kind, URL, model, timeout, enabled state, health
fields, and tenant-wide or user-specific ownership. API keys are stored for the
later service layer and are not exposed by this persistence slice.

Provider migration versions are SQLite 88, PostgreSQL 89, MySQL 80, and SQL
Server 83. They follow each provider's current fork maximum and do not replay
upstream migration numbers. IDs remain Unicode-capable and use full-value
primary-key storage; ASCII-only identifiers are not introduced.

New endpoints default to disabled in the model and database. Reads reject corrupt
provider, kind and scope values. The migration guard rejects incompatible partial
tables before recording the new version. MySQL uses the full Unicode tenant
lookup index required by the shared schema compatibility code.

Independent validation passed on SQLite, PostgreSQL, MySQL and SQL Server:
fresh installation, interrupted migration restart, incompatible schema rejection,
and upgrades from version 51. The persistence suite passed 71 tests per provider,
with 72 on MySQL. It includes 450-character Unicode IDs, full-value uniqueness,
scoped reads, field updates, deletion, reopen and corrupt enum rejection. A final
fresh run on each provider also verified the disabled model default.

This is persistence acceptance only. The service, provider request validation,
API captain integration and deployment are separate acceptance steps.

Health writes now use a conditional update on the observed timestamp. They
change health fields only and reject stale results after a configuration edit.
SQL Server reads preserve fractional timestamp precision. Independent fresh
database runs passed 72 tests each on SQLite, PostgreSQL and SQL Server, and
73 on MySQL. These runs include null health values, fractional timestamps and
stale-write rejection. No applied migration changed.

The service layer exposes authenticated CRUD and REST routes. Tenant-wide
records are visible to tenant members and editable by tenant administrators;
user-specific records are visible only to their owner and administrators. API
keys are write-only, and omitted update keys preserve the stored value while an
explicit null clears it. The global health sweep is administrator-only and
probes each enabled endpoint with its own bounded client and credentials.

Explicit validation sends the provider-specific embedding or completion
request and verifies the returned model output. The automatic health sweep
remains connectivity-only. Redirects are disabled, endpoint URLs are checked
before requests, and caller cancellation propagates.

The current provider registry covers the six registered provider values. Cloud
provider adapters from upstream remain deferred until a separate ownership and
runtime acceptance slice.

Captains can reference an enabled, inference-kind endpoint in their tenant.
Private endpoint ownership and the captain model are checked at admission.
The captain link is persisted by a new append-only migration after the endpoint
migration for each provider. A database foreign key is the atomic delete
backstop when a captain link races endpoint deletion; the service reports a
safe in-use conflict. API runtime execution remains a separate acceptance
step.

Foreign-key guards compare provider metadata with exact identifier and rule
values, including the target schema. They reject composite, unvalidated,
deferred, or differently targeted links before the migration version is
recorded. Deletion conflict handling uses provider error codes and does not
depend on localized database messages.

Independent service and linkage acceptance passed 55 shared service, HTTP and
lifecycle tests. A combined tree check passed 29 health-wiring, self-deploy gate
and Harbor session tests. All four providers passed fresh installation and
partial-migration restart. Incompatible-schema checks passed on all providers
after correcting the SQLite composite fixture and MySQL fixture cleanup. The
SQLite racing-delete error was reproduced and corrected without removing its
foreign key. Endpoint links use SQLite migration 89, PostgreSQL 90, MySQL 81 and
SQL Server 84. No applied migration was changed.

This source acceptance does not prove live provider access or deployment. API
runtime execution remains unavailable pending its separate tool-boundary proof.
