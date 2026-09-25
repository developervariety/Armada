namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
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
    /// kept counts on every run.
    /// </summary>
    public class DataExpiryService
    {
        #region Private-Members

        private readonly string _Header = "[DataExpiryService] ";
        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly int _RetentionDays;
        private readonly int _ProductionFactRetentionDays;
        private readonly int _RequestHistoryRetentionDays;

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

            DataExpiryResult result = await _Database.DataExpiry.PurgeExpiredAsync(cutoffs, token).ConfigureAwait(false);
            _Logging.Info(_Header + "data expiry summary:"
                + " dataRetentionDays=" + _RetentionDays + " cutoff=" + Describe(cutoffs.RecordCutoffUtc)
                + " productionFactRetentionDays=" + _ProductionFactRetentionDays + " factCutoff=" + Describe(cutoffs.ProductionFactCutoffUtc)
                + " requestHistoryRetentionDays=" + _RequestHistoryRetentionDays + " requestHistoryCutoff=" + Describe(cutoffs.RequestHistoryCutoffUtc)
                + " deleted=" + result.Total + " " + result);
            return result;
        }

        #endregion

        #region Private-Methods

        private static string Describe(DateTime? cutoff)
        {
            return cutoff.HasValue ? cutoff.Value.ToString("o", CultureInfo.InvariantCulture) : "disabled";
        }

        #endregion
    }
}
