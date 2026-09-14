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
    /// read signals, events, released docks and finished merge entries older than the retention
    /// period, and logs one summary line with per-table counts on every run.
    /// </summary>
    public class DataExpiryService
    {
        #region Private-Members

        private readonly string _Header = "[DataExpiryService] ";
        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly int _RetentionDays;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="retentionDays">Number of days to retain completed data. Set to 0 to disable.</param>
        public DataExpiryService(LoggingModule logging, DatabaseDriver database, int retentionDays)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            if (retentionDays < 0) throw new ArgumentOutOfRangeException(nameof(retentionDays), "Must be >= 0");
            _RetentionDays = retentionDays;
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
            DataExpiryCutoffs cutoffs = DataExpiryCutoffs.FromRetention(DateTime.UtcNow, _RetentionDays);
            if (!cutoffs.RecordCutoffUtc.HasValue)
            {
                _Logging.Info(_Header + "data expiry skipped: dataRetentionDays=0 disables it");
                return new DataExpiryResult();
            }

            DataExpiryResult result = await _Database.DataExpiry.PurgeExpiredAsync(cutoffs, token).ConfigureAwait(false);
            _Logging.Info(_Header + "data expiry summary: dataRetentionDays=" + _RetentionDays
                + " cutoff=" + cutoffs.RecordCutoffUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                + " deleted=" + result.Total + " " + result);
            return result;
        }

        #endregion
    }
}
