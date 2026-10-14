# Armada Load Generator

Generate historical screenshot activity using an initialized Armada database or a global-admin API session. Preview is the default; `--apply` writes synthetic records without dispatching work.

```sh
dotnet run --project src/Armada.LoadGenerator -f net10.0 -- --help
```

Supports SQLite, MySQL, PostgreSQL, SQL Server, and REST targets, with configurable days, density, entropy, seed and reproducible profile files. Keep credentials outside the repository. For complete examples, chart coverage, retention and replay behavior, see [Synthetic Activity](../../docs/SYNTHETIC_ACTIVITY.md).
