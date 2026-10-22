# History Archive

Armada keeps its history (mission logs, briefs, diffs, stage records, operator
evidence, captain session transcripts, and database rows past their retention)
in an S3-compatible bucket before it frees the local copy. Raw history is kept
in the bucket for a bounded time; a compact trace record of each objective,
voyage, mission, check, incident, merge and deploy is kept longer, so an
orchestrator can still trace an item after its raw history is gone.

## Settings

The `archive` section of the settings file configures the archive. It
hot-reloads: the sink is built from the current values at each use.

| Key | Default | Meaning |
| --- | --- | --- |
| `archive.enabled` | `false` | Upload history before it is freed locally. |
| `archive.endpoint` | empty | Base URL of the S3-compatible endpoint, for example `https://s3.example.com`. |
| `archive.region` | `us-east-1` | Signing region of the endpoint. |
| `archive.bucket` | empty | Bucket that holds the archive. |
| `archive.forcePathStyle` | `true` | Address the bucket as `endpoint/bucket/key` instead of a bucket host name. |
| `archive.credentialsFile` | empty | Absolute path of a file with `accessKeyId=...` and `secretAccessKey=...`, one per line. Read at each use, never logged. Keep it readable by the admiral only. |
| `archive.rawRetentionDays` | `90` | Days a raw object stays in the bucket before Armada deletes it (1-3650). |
| `archive.traceRetentionDays` | `365` | Days a trace record stays in the bucket and in the local trace table (1-3650). |
| `archive.keyPrefix` | `armada` | Prefix of every object key; empty writes at the bucket root. |
| `archive.keepHistoryWithoutArchive` | `true` | While the archive is disabled or not configured, history past its retention is kept instead of deleted. |
| `archive.sessionRoots` | `[]` | Folders of captain session transcripts to archive: `path`, `name`, `pattern` (default `*`), `retentionDays` (default 30). |
| `archive.denyPatterns` | `[]` | Extra file-name patterns that are never uploaded, beside the built-in deny list. |

The access key never belongs in the settings file.

## Object keys

Every key starts with `archive.keyPrefix`.

| Key | Holds |
| --- | --- |
| `raw/<category>/<yyyy>/<MM>/<relative path>` | One archived file, dated by its last write. |
| `raw/db/<table>/<yyyy-MM-ddTHHmmssZ>-<run>.jsonl.gz` | The rows of one table that one expiry run removed, one JSON object per line. |
| `raw/manifests/<yyyy>/<MM>/<yyyy-MM-ddTHHmmssZ>-<run>.jsonl` | One run's manifest: path, size, SHA-256, key and time of each archived file. |
| `trace/<yyyy>/<MM>/<id>.json` | One trace record. |

## Contracts

- `IArchiveSink` uploads a file or in-memory content and reports success only
  when the stored object's size and SHA-256 match the local content
  (`ArchivePutResult.Verified`). A failure carries a `FailureCode` such as
  `upload_failed`, `size_mismatch`, `sha256_mismatch` or `credentials_missing`.
  A caller frees its local copy only on a verified result.
- `IDataExpiryObserver` receives each database row that data expiry is about to
  delete, after the row is in the export and before its table's rows are
  deleted. An observer that throws stops the expiry of that table.
- `ArchiveKeys` builds every key above, so writers and the expiry job agree on
  the layout.
