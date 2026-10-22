namespace Armada.Core.Services.Archive
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Deletes archive objects past their retention: <c>raw/</c> objects older than
    /// <see cref="ArchiveSettings.RawRetentionDays"/> and <c>trace/</c> objects older than
    /// <see cref="ArchiveSettings.TraceRetentionDays"/>, by the object's last-modified time. Runs only
    /// while the archive is enabled and configured, and touches nothing outside the configured key prefix.
    /// </summary>
    public sealed class ArchiveExpiryService
    {
        #region Public-Members

        /// <summary>
        /// Builds the archive sink from the current archive settings. Defaults to the S3-compatible sink.
        /// </summary>
        public Func<ArchiveSettings, IArchiveSink> ArchiveSinkFactory
        {
            get => _ArchiveSinkFactory;
            set => _ArchiveSinkFactory = value ?? throw new ArgumentNullException(nameof(value));
        }

        #endregion

        #region Private-Members

        private readonly string _Header = "[ArchiveExpiryService] ";
        private readonly LoggingModule _Logging;
        private readonly Func<ArchiveSettings> _SettingsProvider;
        private Func<ArchiveSettings, IArchiveSink> _ArchiveSinkFactory = settings => new S3ArchiveSink(settings);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="settingsProvider">Returns the current archive settings at each run.</param>
        public ArchiveExpiryService(LoggingModule logging, Func<ArchiveSettings> settingsProvider)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _SettingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Delete every archive object past its retention.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run's counts; <see cref="ArchiveExpiryResult.Ran"/> is false when the archive is not active.</returns>
        public async Task<ArchiveExpiryResult> ExpireAsync(CancellationToken token = default)
        {
            ArchiveSettings settings = _SettingsProvider();
            ArchiveExpiryResult result = new ArchiveExpiryResult();
            if (!settings.Enabled || !settings.IsConfigured)
            {
                result.SkipReason = settings.Enabled ? "archive is enabled but not configured" : "archive is disabled";
                return result;
            }

            result.Ran = true;
            IArchiveSink sink = _ArchiveSinkFactory(settings);
            DateTime now = DateTime.UtcNow;
            await ExpireFolderAsync(sink, ArchiveKeys.FolderPrefix(settings.KeyPrefix, ArchiveKeys.RawFolder), now.AddDays(-settings.RawRetentionDays), result, token).ConfigureAwait(false);
            await ExpireFolderAsync(sink, ArchiveKeys.FolderPrefix(settings.KeyPrefix, ArchiveKeys.TraceFolder), now.AddDays(-settings.TraceRetentionDays), result, token).ConfigureAwait(false);

            string line = "archive expiry: rawRetentionDays=" + settings.RawRetentionDays + " traceRetentionDays=" + settings.TraceRetentionDays
                + " listed=" + result.Listed + " deleted=" + result.Deleted + " failed=" + result.Failed;
            if (result.Failed > 0) _Logging.Warn(_Header + line + " firstFailure=" + result.Failures[0]);
            else _Logging.Info(_Header + line);
            return result;
        }

        #endregion

        #region Private-Methods

        private async Task ExpireFolderAsync(IArchiveSink sink, string prefix, DateTime cutoffUtc, ArchiveExpiryResult result, CancellationToken token)
        {
            List<ArchiveObjectInfo> objects = await sink.ListAsync(prefix, token).ConfigureAwait(false);
            foreach (ArchiveObjectInfo info in objects)
            {
                token.ThrowIfCancellationRequested();
                result.Listed++;
                if (!info.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (info.LastModifiedUtc >= cutoffUtc) continue;

                string? failure = await sink.DeleteAsync(info.Key, token).ConfigureAwait(false);
                if (failure == null)
                {
                    result.Deleted++;
                    result.DeletedBytes += info.Bytes;
                }
                else
                {
                    result.Failed++;
                    if (result.Failures.Count < 20) result.Failures.Add(info.Key + ": " + failure);
                }
            }
        }

        #endregion
    }
}
