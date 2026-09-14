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
