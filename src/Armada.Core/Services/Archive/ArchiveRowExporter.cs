namespace Armada.Core.Services.Archive
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Exports the rows a retention purge is about to delete. Each row goes to every observer; with a
    /// sink, the rows of one delete statement are written as gzip JSON lines to a staging file,
    /// uploaded under <see cref="ArchiveKeys.Database"/> and verified before the exporter returns, so
    /// the purge deletes only rows that have a verified copy. A failed upload throws and stops the run.
    /// </summary>
    public sealed class ArchiveRowExporter : IDataExpiryRowExporter
    {
        #region Public-Members

        /// <summary>
        /// Identifier of the run, used in the object keys.
        /// </summary>
        public string RunId { get; }

        /// <summary>
        /// Rows exported.
        /// </summary>
        public int Rows { get; private set; } = 0;

        /// <summary>
        /// Object keys uploaded and verified.
        /// </summary>
        public List<string> UploadedKeys { get; } = new List<string>();

        /// <summary>
        /// Staging files that could not be removed, with the reason. The next run sweeps them.
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();

        #endregion

        #region Private-Members

        private readonly IArchiveSink? _Sink;
        private readonly ArchiveSettings _Settings;
        private readonly string _StagingDirectory;
        private readonly IReadOnlyList<IDataExpiryObserver> _Observers;
        private readonly DateTime _RunUtc;
        private int _Sequence = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="sink">Archive sink; null exports to the observers only.</param>
        /// <param name="settings">Archive settings.</param>
        /// <param name="stagingDirectory">Directory for the staging files; required with a sink.</param>
        /// <param name="observers">Observers that see each expiring row.</param>
        /// <param name="runUtc">Start of the run.</param>
        public ArchiveRowExporter(IArchiveSink? sink, ArchiveSettings settings, string? stagingDirectory, IReadOnlyList<IDataExpiryObserver> observers, DateTime runUtc)
        {
            if (sink != null && String.IsNullOrWhiteSpace(stagingDirectory)) throw new ArgumentNullException(nameof(stagingDirectory));
            _Sink = sink;
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _StagingDirectory = stagingDirectory ?? String.Empty;
            _Observers = observers ?? throw new ArgumentNullException(nameof(observers));
            _RunUtc = runUtc;
            RunId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<int> ExportAsync(string table, DbDataReader rows, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(table)) throw new ArgumentNullException(nameof(table));
            if (rows == null) throw new ArgumentNullException(nameof(rows));

            int count = 0;
            string? staging = null;
            FileStream? file = null;
            GZipStream? gzip = null;
            try
            {
                while (await rows.ReadAsync(token).ConfigureAwait(false))
                {
                    Dictionary<string, object?> row = ReadRow(rows);
                    foreach (IDataExpiryObserver observer in _Observers)
                    {
                        await observer.OnRowExpiringAsync(table, row, token).ConfigureAwait(false);
                    }

                    if (_Sink != null)
                    {
                        if (gzip == null)
                        {
                            Directory.CreateDirectory(_StagingDirectory);
                            staging = Path.Combine(_StagingDirectory, "expiry-" + RunId + "-" + table + "-" + (_Sequence + 1) + ".jsonl.gz");
                            file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None);
                            gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false);
                        }
                        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n");
                        await gzip.WriteAsync(line, token).ConfigureAwait(false);
                    }
                    count++;
                }

                if (gzip != null)
                {
                    await gzip.DisposeAsync().ConfigureAwait(false);
                    gzip = null;
                    file = null;
                    _Sequence++;
                    string key = ArchiveKeys.Database(_Settings.KeyPrefix, table, _RunUtc, RunId + "-" + _Sequence.ToString("D2"));
                    string sha256 = await HistoryArchivePass.HashFileAsync(staging!, token).ConfigureAwait(false);
                    ArchivePutResult put = await _Sink!.PutFileAsync(key, staging!, sha256, token).ConfigureAwait(false);
                    if (!put.Verified)
                        throw new InvalidOperationException("archive export of " + count + " expiring " + table + " rows to " + key + " was not verified ("
                            + put.FailureCode + ": " + put.FailureMessage + "); no row of it is deleted");
                    UploadedKeys.Add(key);
                }

                Rows += count;
                return count;
            }
            finally
            {
                if (gzip != null) await gzip.DisposeAsync().ConfigureAwait(false);
                else if (file != null) await file.DisposeAsync().ConfigureAwait(false);
                if (staging != null) TryDelete(staging);
            }
        }

        #endregion

        #region Private-Methods

        private static Dictionary<string, object?> ReadRow(DbDataReader rows)
        {
            Dictionary<string, object?> row = new Dictionary<string, object?>(rows.FieldCount, StringComparer.Ordinal);
            for (int i = 0; i < rows.FieldCount; i++)
            {
                object value = rows.GetValue(i);
                row[rows.GetName(i)] = value is DBNull ? null : value;
            }
            return row;
        }

        private void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Warnings.Add("could not remove staging file " + path + ": " + ex.Message);
            }
        }

        #endregion
    }
}
