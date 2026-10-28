# Storage Inventory

The storage inventory is a cached, read-only measurement of what Armada keeps on
disk and in its database. It answers "where did the space go" without deleting,
moving or archiving anything. Reclaiming space is the job of the
[disk lifecycle](ARCHIVE.md) service; the inventory only reports.

Both routes and the Dashboard **Storage** page require a global administrator.

## What it measures

Each category is a location that owns its directory subtree except the subtrees of
the more specific locations beside it, so no file is counted twice. Sizes are
logical file lengths; file contents are never read.

| Category | Location |
| --- | --- |
| `DatabaseFiles` | The SQLite database file and its `-wal` and `-shm` files (SQLite only) |
| `MissionLogs` | `<logDirectory>/missions` |
| `Logs` | The rest of `logDirectory` |
| `Docks` | `docksDirectory` |
| `Repositories` | `reposDirectory` |
| `Backups` | `<dataDirectory>/backups` |
| `ArchiveStaging` | `<dataDirectory>/archive-staging` |
| `CodeIndex` | The code index directory |
| `OperatorEvidence` | Each root in `captainEvidenceRoots` |
| `Other` | The rest of `dataDirectory` |

Each location reports its bytes, file count, oldest and newest write time, the
largest files, and the volume capacity. The database section reports the
provider, allocated bytes, unused bytes where the provider reports them, and the
tables with their rows and, where the provider reports them, data and index
bytes. Row counts come from provider statistics (`Estimated`); a refresh with
`ExactCounts` runs a bounded `COUNT(*)` per table (`Exact`). SQLite reports no
per-table sizes and says so with `TableSizesUnavailable`.

Providers: SQLite, PostgreSQL, MySQL and SQL Server. The database measurement
opens its own short-lived connection, runs read-only statistics queries, and
bounds each command to five seconds.

## Bounds and partial results

A scan is bounded by entry count, directory depth and elapsed time. When a bound
is reached the scan stops, the figures are a lower bound, and the report names the
reason instead of failing silently.

| Code | Meaning |
| --- | --- |
| `EntryLimit` | `storage.maxEntries` files and directories were examined |
| `DepthLimit` | A directory deeper than `storage.maxDepth` was not entered |
| `TimeLimit` | The scan ran for `storage.timeoutSeconds` |
| `NotReached` | An earlier bound ended the scan before this location |
| `AccessDenied` | The server process may not read an entry |
| `Unreadable` | An entry failed with an I/O error other than disappearing |
| `InvalidPath` | A configured root is not a valid path |

Symbolic links and reparse points below a root are never followed; each one is
counted in the location's `LinksSkipped`. A file or directory that disappears
while the scan runs is tolerated and counted in `VanishedEntries`. Neither makes
the figure partial. A root that is missing reports `Missing`.

A database or history problem appears in the matching `Warnings` list:
`DatabaseUnavailable`, `DatabaseTimedOut`, `AllocationUnavailable`, `TableLimit`,
`ExactCountUnavailable`, `HistoryUnreadable`, `HistoryNotSaved`. A collection that
fails keeps the previous figures and sets `LastError` to `CollectionFailed`; the
Admiral log has the cause.

## Cache and refresh

The Admiral collects at start and then every `storage.refreshIntervalSeconds`
while `storage.enabled` is true. `GET /api/v1/storage` returns the cache and never
scans. `POST /api/v1/storage/refresh` starts a collection in the background and
returns `202` with the current cache; a request made while a collection runs joins
it. Poll the read route until `Collecting` is `false`. With `storage.enabled` set
to `false` only a manual refresh collects, and `RefreshIntervalSeconds` reads `0`.

## Growth history

Each collection appends one sample: observed file bytes, database allocation and
bytes per category. The newest `storage.historyCapacity` samples are kept in
`storage-history.json` in the data directory, replaced atomically on every write,
so the history survives a restart without a database table. A history file that
cannot be read restarts the history empty and sets `HistoryUnreadable`.

## Settings

```json
{
  "storage": {
    "enabled": true,
    "refreshIntervalSeconds": 900,
    "timeoutSeconds": 30,
    "maxEntries": 250000,
    "maxDepth": 32,
    "largestFileLimit": 20,
    "historyCapacity": 288
  }
}
```

| Setting | Default | Range |
| --- | --- | --- |
| `enabled` | `true` | Background refresh on or off |
| `refreshIntervalSeconds` | `900` | 30 to 86400 |
| `timeoutSeconds` | `30` | 1 to 300 |
| `maxEntries` | `250000` | 100 to 2000000 |
| `maxDepth` | `32` | 1 to 256 |
| `largestFileLimit` | `20` | 1 to 100 |
| `historyCapacity` | `288` | 2 to 2016 |

The section hot-reloads with the settings file.

## Not included

Harbor runners do not report storage; the inventory covers the Admiral's own data
directory and database. The Harbor heartbeat carries only the live job list.
