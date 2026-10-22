namespace Armada.Core.Services.Archive
{
    using System;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Connects data expiry and the trace recorder to one archive: both read the same current archive
    /// settings and build the same sink, and data expiry writes each expiring record's trace record
    /// before it deletes the row. With the archive off nothing is uploaded and data expiry keeps
    /// expired rows, as <see cref="ArchiveSettings.KeepHistoryWithoutArchive"/> sets.
    /// </summary>
    public static class HistoryArchiveWiring
    {
        /// <summary>
        /// Connect data expiry and the trace recorder to the archive.
        /// </summary>
        /// <param name="dataExpiry">Data expiry service.</param>
        /// <param name="traceRecorder">Trace recorder.</param>
        /// <param name="settingsProvider">Returns the current archive settings at each use.</param>
        /// <param name="sinkFactory">Builds the archive sink from the current settings.</param>
        public static void Connect(DataExpiryService dataExpiry, TraceRecorder traceRecorder, Func<ArchiveSettings> settingsProvider, Func<ArchiveSettings, IArchiveSink> sinkFactory)
        {
            if (dataExpiry == null) throw new ArgumentNullException(nameof(dataExpiry));
            if (traceRecorder == null) throw new ArgumentNullException(nameof(traceRecorder));
            if (settingsProvider == null) throw new ArgumentNullException(nameof(settingsProvider));
            if (sinkFactory == null) throw new ArgumentNullException(nameof(sinkFactory));

            traceRecorder.ArchiveSettingsProvider = settingsProvider;
            traceRecorder.ArchiveSinkFactory = sinkFactory;
            dataExpiry.ArchiveSettingsProvider = settingsProvider;
            dataExpiry.ArchiveSinkFactory = sinkFactory;
            dataExpiry.Observers.Add(new TraceExpiryObserver(traceRecorder));
        }
    }
}
