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
| `raw/db/<table>/<yyyy-MM-ddTHHmmssZ>-<run>-<NN>.jsonl.gz` | The rows one delete statement of one expiry run removed, one JSON object per line; `<NN>` numbers the statements of the run. |
| `raw/manifests/<yyyy>/<MM>/<yyyy-MM-ddTHHmmssZ>-<run>.jsonl` | One run's manifest: path, size, SHA-256, key and time of each archived file. |
| `trace/<yyyy>/<MM>/<id>.json` | One trace record. |

## Contracts

- `IArchiveSink` uploads a file or in-memory content and reports success only
  when the stored object's size and SHA-256 match the local content
  (`ArchivePutResult.Verified`). A failure carries a `FailureCode` such as
  `upload_failed`, `size_mismatch`, `sha256_mismatch` or `credentials_missing`.
  A caller frees its local copy only on a verified result.
- `IArchiveSink.GetBytesAsync` reads one object's content, returns null for a
  missing key and refuses an object above the caller's size limit.
- `IDataExpiryObserver` receives each database row that data expiry is about to
  delete, before the delete runs. An observer that throws stops the run, so
  every row not yet deleted is kept.
- `ArchiveKeys` builds every key above, so writers and the expiry job agree on
  the layout.

## History modes

Each disk-lifecycle pass and each data-expiry run reads the `archive` section
and treats expired history in one of three modes. The disk-lifecycle report
names the mode in `HistoryMode`.

| Mode | When | Expired history |
| --- | --- | --- |
| `Archive` | `archive.enabled` is true and endpoint, bucket and credentials file are set. | Uploaded and verified, then freed. A failed upload keeps the item. |
| `Keep` | Otherwise, while `archive.keepHistoryWithoutArchive` is true (the default). | Kept. The report records each item `kept` with the reason. |
| `Delete` | Otherwise, with `archive.keepHistoryWithoutArchive` false. | Freed without a copy, except captain session transcripts, which are kept. |

An archive that is enabled but missing its endpoint, bucket or credentials file
runs as `Keep` or `Delete` and is named in the report's `Errors`.

## What is history

The disk-lifecycle pass treats these as history: mission logs, diff snapshots,
instruction snapshots and kept handoff texts (`logs/handoffs`, bounded by the
instruction retention), dock metadata and stage-start records, final messages
(`logs/final-messages`, bounded by the mission-log retention), whole
operator-evidence folders idle past `diskLifecycle.operatorEvidenceRetentionDays`,
and the files of each `archive.sessionRoots` entry older than its
`retentionDays`. Docks, worktrees, temp artifacts, backups, extra retention
roots and the `.git`, `bin`, `obj` and `node_modules` payload of operator
evidence are not history: they are freed as before, with no upload.

An archived operator-evidence folder uploads every file outside its payload
directories, and is removed only when every upload is verified. A session root
is an absolute folder; its files are uploaded under
`raw/captainSessions-<name>/` with their path relative to the root, and are
freed only after a verified upload, whatever the mode.

Data expiry treats every expired row as history. In `Archive` mode it reads the
rows each delete statement selects, writes them as gzip JSON lines to a staging
file under `<dataDirectory>/archive-staging`, uploads and verifies the file,
and only then runs the delete. A failed export stops the run with the failure
named, and no row of that statement or any later statement is deleted. In
`Keep` mode data expiry deletes nothing. The server sets the archive and
staging folder on its data-expiry service; the data-retention values
themselves are read at startup.

## Trace records and the archive

The server connects data expiry and the trace recorder to the same archive
settings and sink. Data expiry passes every expiring row to the trace expiry
observer, which writes the trace record of an expiring voyage, mission, merge
entry, check run or deployment before the row is deleted. The recorder uploads
ended records to `trace/` and keeps the verified key on the record. When a
record and its source row are both gone, `armada_trace` reads the record back
from the archive with `IArchiveSink.GetBytesAsync`.

## Deny list

A file whose name matches the deny list is never uploaded and never freed by a
history pass; the report records it `kept`. The built-in list is `auth.json`,
`*credential*`, `*secret*`, `settings.json`, `settings.json.*`,
`settings.*.json`, `settings-*.json`, `.env`, `.env.*`, `*.env`,
`git-credentials`, `.git-credentials`, `mcp-auth-header`, `*.pem`, `*.key`,
`*.p12`, `*.pfx`, `id_rsa*`, `id_ed25519*` and `id_ecdsa*`, plus any file under
a `secrets` or `.ssh` folder. `archive.denyPatterns` adds patterns; matching
ignores case.

## Failures

A history pass that fails to verify any upload records one
`disk_lifecycle.archive_failed` event and posts one board note. Both name the
run, the count and up to five kept files with their failure codes. The report
carries `ArchivedItems`, `ArchivedBytes`, `ArchiveFailures` and
`ArchiveManifestKey`.

## Archive expiry

The server's health loop runs the archive expiry every 1000 cycles. It lists
`raw/` and deletes each object last modified more than `archive.rawRetentionDays`
ago, then lists `trace/` and deletes each object older than
`archive.traceRetentionDays`. It touches only keys under `archive.keyPrefix`,
and does nothing while the archive is disabled or not configured.

## Deploy backups

Admiral backups under `<dataDirectory>/backups` keep the newest
`diskLifecycle.minBackupCount` archives and delete older ones past
`diskLifecycle.backupRetentionDays`. Set `backupRetentionDays` to `0` and
`minBackupCount` to `2` to keep exactly the last two. Backups are not history
and are never uploaded.
