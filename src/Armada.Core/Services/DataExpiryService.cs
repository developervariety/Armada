namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Archive;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Background service that purges expired records through the database driver, so it runs on
    /// every provider. Removes completed voyages and their missions, completed standalone missions,
    /// read signals, events, released docks and finished merge entries older than the data retention
    /// period, append-only production metric facts older than their own retention period, and captured
    /// request history older than the request-history retention period. Events that are the only record
    /// of something are kept: the newest snapshot of each incident and of each runbook execution,
    /// objective deletion tombstones, typed-decision reversals, and dispatch attempts inside the
    /// reconciliation look-back. Logs one summary line with per-table deleted counts and per-class
    /// kept counts on every run. With <see cref="ArchiveSettingsProvider"/> set, expired rows are history:
    /// with the archive active every delete statement's rows are exported as gzip JSON lines and
    /// verified before they are deleted, and with no archive nothing is deleted unless
    /// <see cref="ArchiveSettings.KeepHistoryWithoutArchive"/> is off. Each <see cref="Observers"/> entry
    /// sees every row before it is deleted.
    /// </summary>
    public class DataExpiryService
    {
        #region Public-Members

        /// <summary>
        /// Returns the current archive settings at each run. Null purges without an archive, as a
        /// plain retention purge.
        /// </summary>
        public Func<ArchiveSettings>? ArchiveSettingsProvider { get; set; } = null;

        /// <summary>
        /// Builds the archive sink from the current archive settings. Defaults to the S3-compatible sink.
        /// </summary>
        public Func<ArchiveSettings, IArchiveSink> ArchiveSinkFactory
        {
            get => _ArchiveSinkFactory;
            set => _ArchiveSinkFactory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Directory for export staging files. Required when the archive is active; kept on the data
        /// volume, never in a RAM-backed temp folder.
        /// </summary>
        public string? StagingDirectory { get; set; } = null;

        /// <summary>
        /// Observers that see every expiring row before it is deleted. An observer that throws stops
        /// the run, so the rows it did not see are kept.
        /// </summary>
        public List<IDataExpiryObserver> Observers { get; } = new List<IDataExpiryObserver>();

        #endregion

        #region Private-Members

        private readonly string _Header = "[DataExpiryService] ";
        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly int _RetentionDays;
        private readonly int _ProductionFactRetentionDays;
        private readonly int _RequestHistoryRetentionDays;
        private Func<ArchiveSettings, IArchiveSink> _ArchiveSinkFactory = settings => new S3ArchiveSink(settings);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="retentionDays">Number of days to retain completed data. Set to 0 to disable.</param>
        /// <param name="productionFactRetentionDays">Number of days to retain production metric facts. Set to 0 to keep them forever.</param>
        /// <param name="requestHistoryRetentionDays">Number of days to retain captured request history. Set to 0 to keep it forever.</param>
        public DataExpiryService(LoggingModule logging, DatabaseDriver database, int retentionDays, int productionFactRetentionDays, int requestHistoryRetentionDays = 0)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            if (retentionDays < 0) throw new ArgumentOutOfRangeException(nameof(retentionDays), "Must be >= 0");
            if (productionFactRetentionDays < 0) throw new ArgumentOutOfRangeException(nameof(productionFactRetentionDays), "Must be >= 0");
            _RetentionDays = retentionDays;
            if (requestHistoryRetentionDays < 0) throw new ArgumentOutOfRangeException(nameof(requestHistoryRetentionDays), "Must be >= 0");
            _ProductionFactRetentionDays = productionFactRetentionDays;
            _RequestHistoryRetentionDays = requestHistoryRetentionDays;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run the data expiry process once.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rows deleted per table.</returns>
        public async Task<DataExpiryResult> PurgeExpiredDataAsync(CancellationToken token = default)
        {
            DataExpiryCutoffs cutoffs = DataExpiryCutoffs.FromRetention(DateTime.UtcNow, _RetentionDays, _ProductionFactRetentionDays, _RequestHistoryRetentionDays);
            if (!cutoffs.AnyEnabled)
            {
                _Logging.Info(_Header + "data expiry skipped: dataRetentionDays=0, productionFactRetentionDays=0 and requestHistoryRetentionDays=0 disable it");
                return new DataExpiryResult();
            }

            ArchiveSettings? archive = ArchiveSettingsProvider?.Invoke();
            HistoryRetentionModeEnum mode = archive == null ? HistoryRetentionModeEnum.Delete : archive.ResolveHistoryMode();
            if (archive != null && archive.Enabled && !archive.IsConfigured)
                _Logging.Warn(_Header + "archive is enabled but endpoint, bucket or credentialsFile is missing");
            if (mode == HistoryRetentionModeEnum.Keep)
            {
                _Logging.Info(_Header + "data expiry kept every expired row: no archive is configured and archive.keepHistoryWithoutArchive is true");
                return new DataExpiryResult();
            }

            ArchiveRowExporter? exporter = null;
            if (mode == HistoryRetentionModeEnum.Archive || Observers.Count > 0)
            {
                IArchiveSink? sink = null;
                if (mode == HistoryRetentionModeEnum.Archive)
                {
                    if (String.IsNullOrWhiteSpace(StagingDirectory))
                        throw new InvalidOperationException("data expiry archive needs a staging directory; nothing was deleted");
                    SweepStaging(StagingDirectory);
                    sink = _ArchiveSinkFactory(archive!);
                }
                exporter = new ArchiveRowExporter(sink, archive ?? new ArchiveSettings(), StagingDirectory, Observers.ToArray(), DateTime.UtcNow);
            }

            DataExpiryResult result;
            try
            {
                result = await _Database.DataExpiry.PurgeExpiredAsync(cutoffs, exporter, token).ConfigureAwait(false);
            }
            finally
            {
                if (exporter != null)
                {
                    foreach (string warning in exporter.Warnings) _Logging.Warn(_Header + warning);
                }
            }

            if (exporter != null)
                _Logging.Info(_Header + "data expiry export: mode=" + mode + " run=" + exporter.RunId + " rows=" + exporter.Rows + " objects=" + exporter.UploadedKeys.Count);
            _Logging.Info(_Header + "data expiry summary:"
                + " dataRetentionDays=" + _RetentionDays + " cutoff=" + Describe(cutoffs.RecordCutoffUtc)
                + " productionFactRetentionDays=" + _ProductionFactRetentionDays + " factCutoff=" + Describe(cutoffs.ProductionFactCutoffUtc)
                + " requestHistoryRetentionDays=" + _RequestHistoryRetentionDays + " requestHistoryCutoff=" + Describe(cutoffs.RequestHistoryCutoffUtc)
                + " deleted=" + result.Total + " " + result);
            return result;
        }

        #endregion

        #region Private-Methods

        // A staging file left by an interrupted run holds rows that were never deleted, so it is only
        // a stale copy and is removed.
        private void SweepStaging(string staging)
        {
            if (!Directory.Exists(staging)) return;
            foreach (string file in Directory.EnumerateFiles(staging, "expiry-*.jsonl.gz"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _Logging.Warn(_Header + "could not remove stale staging file " + file + ": " + ex.Message);
                }
            }
        }

        private static string Describe(DateTime? cutoff)
        {
            return cutoff.HasValue ? cutoff.Value.ToString("o", CultureInfo.InvariantCulture) : "disabled";
        }

        #endregion
    }
}
