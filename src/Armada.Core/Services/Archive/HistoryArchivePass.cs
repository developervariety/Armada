namespace Armada.Core.Services.Archive
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// One pass of archive uploads: hashes each file, uploads it under its <see cref="ArchiveKeys.Raw"/>
    /// key, keeps a manifest line per file, and remembers the failures so the pass can report them.
    /// A file whose key already holds the same size and SHA-256 is not uploaded again, so a pass that
    /// retries an item an earlier pass archived but could not free sends nothing twice.
    /// The caller frees a local file only when <see cref="ArchiveFileAsync"/> returns a verified result.
    /// </summary>
    public sealed class HistoryArchivePass
    {
        #region Public-Members

        /// <summary>
        /// Identifier of the pass, used in the manifest key.
        /// </summary>
        public string RunId { get; }

        /// <summary>
        /// Files verified in the archive, whether uploaded by this pass or already stored.
        /// </summary>
        public int Uploaded { get; private set; } = 0;

        /// <summary>
        /// Bytes verified in the archive, whether uploaded by this pass or already stored.
        /// </summary>
        public long UploadedBytes { get; private set; } = 0;

        /// <summary>
        /// Files counted in <see cref="Uploaded"/> whose verified object already existed, so no upload was sent.
        /// </summary>
        public int Reused { get; private set; } = 0;

        /// <summary>
        /// Items whose every file was verified in the archive but whose local copy could not be freed,
        /// with the removal failure.
        /// </summary>
        public List<KeyValuePair<string, string>> FreeFailures { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Files this pass has handed to the archive, verified or not.
        /// </summary>
        public int Processed { get; private set; } = 0;

        /// <summary>
        /// Most files the pass archives before it defers further items.
        /// </summary>
        public int MaxFiles { get; }

        /// <summary>
        /// True once the pass has handed <see cref="MaxFiles"/> files to the archive; the caller defers
        /// every item it has not started.
        /// </summary>
        public bool Exhausted => Processed >= MaxFiles;

        /// <summary>
        /// Items deferred to the next pass because the pass was exhausted.
        /// </summary>
        public int Deferred { get; private set; } = 0;

        /// <summary>
        /// Uploads that were not verified, with their local paths.
        /// </summary>
        public List<KeyValuePair<string, ArchivePutResult>> Failures { get; } = new List<KeyValuePair<string, ArchivePutResult>>();

        #endregion

        #region Private-Members

        private readonly IArchiveSink _Sink;
        private readonly ArchiveSettings _Settings;
        private readonly DateTime _StartedUtc;
        private readonly StringBuilder _Manifest = new StringBuilder();
        private int _ManifestLines = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Start a pass.
        /// </summary>
        /// <param name="sink">Archive sink.</param>
        /// <param name="settings">Archive settings.</param>
        /// <param name="startedUtc">Start of the pass.</param>
        /// <param name="maxFiles">Most files the pass archives before it defers further items (at least 1).</param>
        public HistoryArchivePass(IArchiveSink sink, ArchiveSettings settings, DateTime startedUtc, int maxFiles = Int32.MaxValue)
        {
            MaxFiles = Math.Max(1, maxFiles);
            _Sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _StartedUtc = startedUtc;
            RunId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Upload one file and verify it.
        /// </summary>
        /// <param name="category">History category of the file.</param>
        /// <param name="root">Category root the key path is relative to.</param>
        /// <param name="fullPath">Absolute path of the file.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified result or a named failure.</returns>
        public async Task<ArchivePutResult> ArchiveFileAsync(string category, string root, string fullPath, CancellationToken token)
        {
            Processed++;
            DateTime lastWrite = File.GetLastWriteTimeUtc(fullPath);
            string relative = Path.GetRelativePath(root, fullPath);
            string key = ArchiveKeys.Raw(_Settings.KeyPrefix, category, lastWrite, relative);
            ArchivePutResult result;
            bool reused = false;
            try
            {
                string sha256 = await HashFileAsync(fullPath, token).ConfigureAwait(false);
                long bytes = new FileInfo(fullPath).Length;
                ArchivePutResult stored = await _Sink.VerifyStoredAsync(key, bytes, sha256, token).ConfigureAwait(false);
                if (stored.Verified)
                {
                    result = stored;
                    reused = true;
                }
                else
                {
                    result = await _Sink.PutFileAsync(key, fullPath, sha256, token).ConfigureAwait(false);
                }
            }
            catch (IOException ex)
            {
                result = ArchivePutResult.Failed(key, "local_read_failed", "could not read " + fullPath + ": " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                result = ArchivePutResult.Failed(key, "local_read_failed", "could not read " + fullPath + ": " + ex.Message);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                result = ArchivePutResult.Failed(key, "upload_failed", ex.GetType().Name + ": " + ex.Message);
            }

            if (result.Verified)
            {
                Uploaded++;
                UploadedBytes += result.Bytes;
                if (reused) Reused++;
            }
            else
            {
                Failures.Add(new KeyValuePair<string, ArchivePutResult>(fullPath, result));
            }

            _Manifest.AppendLine(JsonSerializer.Serialize(new
            {
                path = fullPath,
                category = category,
                key = result.Key,
                bytes = result.Bytes,
                sha256 = result.Sha256,
                lastWriteUtc = lastWrite,
                verified = result.Verified,
                alreadyStored = reused,
                failureCode = result.FailureCode,
                archivedUtc = DateTime.UtcNow
            }));
            _ManifestLines++;
            return result;
        }

        /// <summary>
        /// Count an item deferred to the next pass.
        /// </summary>
        public void Defer()
        {
            Deferred++;
        }

        /// <summary>
        /// Record an item whose files are all verified in the archive but whose local copy could not be freed.
        /// </summary>
        /// <param name="path">Local path of the item.</param>
        /// <param name="failure">Why the removal failed.</param>
        public void RecordFreeFailure(string path, string failure)
        {
            FreeFailures.Add(new KeyValuePair<string, string>(path, failure));
        }

        /// <summary>
        /// Upload the pass manifest, when the pass archived anything.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The manifest upload result, or null when the pass archived nothing.</returns>
        public async Task<ArchivePutResult?> WriteManifestAsync(CancellationToken token)
        {
            if (_ManifestLines == 0) return null;
            string key = ArchiveKeys.Manifest(_Settings.KeyPrefix, _StartedUtc, RunId);
            return await _Sink.PutBytesAsync(key, Encoding.UTF8.GetBytes(_Manifest.ToString()), "application/x-ndjson", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Lower-case hex SHA-256 of a file.
        /// </summary>
        /// <param name="path">File path.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Hex digest.</returns>
        public static async Task<string> HashFileAsync(string path, CancellationToken token)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
            }
        }

        #endregion
    }
}
