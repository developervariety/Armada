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
| `raw/<category>/<yyyy>/<MM>/<relative folder>.tar.gz` | One expired history folder of at least `diskLifecycle.archiveBundleMinFiles` files, packed as a gzip-compressed tar whose entry names are relative to the folder; dated by the newest write among its files. |
| `raw/db/<table>/<yyyy-MM-ddTHHmmssZ>-<run>-<NN>.jsonl.gz` | The rows one delete statement of one expiry run removed, one JSON object per line; `<NN>` numbers the statements of the run. |
| `raw/manifests/<yyyy>/<MM>/<yyyy-MM-ddTHHmmssZ>-<run>.jsonl` | One run's manifest: path, size, SHA-256, key and time of each archived file or bundle; a bundle's line names its folder and carries `bundledFiles`. |
| `trace/<yyyy>/<MM>/<id>.json` | One trace record. |

## Contracts

- `IArchiveSink` uploads a file or in-memory content and reports success only
  when the stored object's size and SHA-256 match the local content
  (`ArchivePutResult.Verified`). A failure carries a `FailureCode` such as
  `upload_failed`, `size_mismatch`, `sha256_mismatch` or `credentials_missing`.
  A caller frees its local copy only on a verified result. A read-back refused
  with HTTP 403 whose body names a download or transaction cap carries the code
  `cap_exceeded`; a 403 that names an auth, permission or signature error stays
  a per-object `verify_failed`.
- `IArchiveSink.VerifyStoredAsync` checks whether a key already holds content
  of the given size and SHA-256, without uploading. A history pass asks it
  before each upload and sends no upload for a file that is already stored, so
  a pass that retries an archived item uploads nothing twice.
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
instruction snapshots, each mission's brief-file folders beside its snapshot
(`<mission>.mission` and `<mission>.memory`, one item per folder, aged by its
newest file) and kept handoff texts (`logs/handoffs`, bounded by the
instruction retention), dock metadata and stage-start records, final messages
(`logs/final-messages`, bounded by the mission-log retention), whole
operator-evidence folders idle past `diskLifecycle.operatorEvidenceRetentionDays`,
and the files of each `archive.sessionRoots` entry older than its
`retentionDays`. Docks, worktrees, temp artifacts, backups, extra retention
roots and the `.git`, `bin`, `obj` and `node_modules` payload of operator
evidence are not history: they are freed as before, with no upload.

An archived operator-evidence folder uploads every file outside its payload
directories, and is removed only when every upload is verified. A brief-file
folder uploads every file. Before a verified folder is removed, each directory
inside it is given owner read, write and execute, because a read-only
directory blocks removing its entries. The folder that holds the item is never
changed, symlinks are neither followed nor changed, and the operating system
refuses the change on a directory the admiral does not own. A session root
is an absolute folder; its files are uploaded under
`raw/captainSessions-<name>/` with their path relative to the root, and are
freed only after a verified upload, whatever the mode.

Data expiry treats every expired row as history. In `Archive` mode it reads the
rows each delete statement selects, writes them as gzip JSON lines to a staging
file under `<dataDirectory>/archive-staging`, uploads and verifies the file,
and only then runs the delete. A failed export stops the run with the failure
named, and no row of that statement or any later statement is deleted. Whenever
an exporter is attached, the delete removes only the rows it exported: the
identity of every selected row is read before the export, and the delete matches
each row by that identity and, for voyages, missions, docks and merge entries,
by the exported `last_update_utc`, in one transaction. A row updated after its
export, or one that starts matching after the snapshot, stays and expires on a
later pass with its newer state exported then. A voyage that still has a mission
waits for the pass that deletes that mission. In `Keep` mode data expiry deletes nothing. The server sets the archive and
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
history pass; the report records it `kept`. Inside an expired history folder a
deny-listed file keeps only itself: the folder's other files are archived and
freed, emptied folders are removed, and the deny-listed file stays in place. A
folder that holds nothing but deny-listed files is reported `kept` and is not
uploaded. The built-in list is `auth.json`,
`*credential*`, `*secret*`, `settings.json`, `settings.json.*`,
`settings.*.json`, `settings-*.json`, `.env`, `.env.*`, `*.env`,
`git-credentials`, `.git-credentials`, `mcp-auth-header`, `*.pem`, `*.key`,
`*.p12`, `*.pfx`, `id_rsa*`, `id_ed25519*` and `id_ecdsa*`, plus any file under
a `secrets` or `.ssh` folder. `archive.denyPatterns` adds patterns; matching
ignores case.

## Failures

The S3 sink makes at most three attempts for a PUT or HEAD after HTTP 429,
500, 502, 503 or 504, or a transport failure. Backoff is one second, then
two seconds. A longer `Retry-After` is honored up to 30 seconds; a longer
cooldown leaves the object for a later pass. The whole operation shares the
shorter of the HTTP client timeout and 30 minutes. Caller cancellation stops
the operation. Authentication, cap, size and checksum failures are not retried.

Each attempt has fresh content and a fresh signature. After a failed PUT,
HEAD must show that the key is absent before another PUT is sent. Matching
stored size and SHA-256 complete an ambiguous upload without another version.
An unknown verification result keeps the local file. After a successful PUT,
verification retries only HEAD. Exhaustion retains the bounded provider support
receipt; response message bodies remain private.

A history pass that fails to verify any upload records one
`disk_lifecycle.archive_failed` event. A pass that verified every file of an
item but could not remove the item records one
`disk_lifecycle.archive_free_failed` event and names each such item in the
report's `Errors`; the item stays, and the next pass retries the removal
without uploading its files again. Each event names the run, the count and up
to five items with their failures, and the pass posts one board note that
carries both. The report carries `ArchivedItems` (files verified in the
archive by the pass, uploaded now or already stored), `ArchiveReusedItems`
(those already stored, so not uploaded), `ArchivedBytes`, `ArchiveFailures`,
`ArchiveFreeFailures` and `ArchiveManifestKey`.

When a read-back verify is refused because the object store's download or
transaction cap is exceeded (HTTP 403 with a cap body), the pass stops verifying
after the first such refusal rather than sending one more capped request per
remaining file. It records one `disk_lifecycle.archive_cap_reached` event and
posts one board note naming the run and the number of history items kept, keeps
every remaining history item locally, and sets `ArchiveTransactionCapReached`
and `ArchiveSkippedForCap` on the report. The cap must be raised before uploads
resume. A 403 that is not a cap (auth, permission or signature) is still
reported per item as a verify failure.

A pass runs inside the admiral's health loop, so it archives at most
`diskLifecycle.archiveMaxFilesPerPass` files (default 1000). Once it reaches
that bound it starts no further history item: each one is counted reclaimable,
recorded with the disposition `deferred`, and left for the next pass. A folder
already started is finished. An expired history folder that holds at least
`diskLifecycle.archiveBundleMinFiles` archivable files (default 50) is packed
into one gzip-compressed tar in the system temp directory and archived as one
object, which counts as one file against the bound; each object costs an
existence check, an upload and a read-back, so a folder of thousands of small
files archives in one round trip. The temporary bundle is removed whether or not
the upload verifies, and a bundle left by a crash expires with the other Armada
temp artifacts. Operator evidence is archived before the other
history categories, so a backlog of small files cannot hold its few large
folders back. The report sets `ArchiveCapped` and counts the
deferred items in `ArchiveDeferredItems`, and the pass's report event says how
many items it deferred. Every other scan step has no item bound. The report
keeps the first 2000 per-item actions and the first 200 errors; the category
counts and `ErrorCount` cover every item.

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
