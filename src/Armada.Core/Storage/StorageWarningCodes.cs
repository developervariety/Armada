namespace Armada.Core.Storage
{
    /// <summary>Stable codes that explain why a storage figure is partial or missing.</summary>
    public static class StorageWarningCodes
    {
        /// <summary>The scan examined the configured maximum number of entries and stopped.</summary>
        public const string EntryLimit = "EntryLimit";

        /// <summary>A directory deeper than the configured maximum depth was not entered.</summary>
        public const string DepthLimit = "DepthLimit";

        /// <summary>The scan reached its time limit and stopped.</summary>
        public const string TimeLimit = "TimeLimit";

        /// <summary>The scan was not run for this location because an earlier bound ended the scan.</summary>
        public const string NotReached = "NotReached";

        /// <summary>An entry could not be read because of an I/O error other than the entry disappearing.</summary>
        public const string Unreadable = "Unreadable";

        /// <summary>The server process may not read an entry.</summary>
        public const string AccessDenied = "AccessDenied";

        /// <summary>A root path is not a valid path.</summary>
        public const string InvalidPath = "InvalidPath";

        /// <summary>The stored history file could not be read and history restarted empty.</summary>
        public const string HistoryUnreadable = "HistoryUnreadable";

        /// <summary>The stored history file could not be written.</summary>
        public const string HistoryNotSaved = "HistoryNotSaved";

        /// <summary>The database statistics query timed out.</summary>
        public const string DatabaseTimedOut = "DatabaseTimedOut";

        /// <summary>The database statistics could not be read.</summary>
        public const string DatabaseUnavailable = "DatabaseUnavailable";

        /// <summary>The database reported more tables than the inventory lists.</summary>
        public const string TableLimit = "TableLimit";

        /// <summary>The database allocation figure could not be read.</summary>
        public const string AllocationUnavailable = "AllocationUnavailable";

        /// <summary>The provider reports no per-table sizes; tables list row counts only. This does not make the measurement partial.</summary>
        public const string TableSizesUnavailable = "TableSizesUnavailable";

        /// <summary>An exact row count failed for one table.</summary>
        public const string ExactCountUnavailable = "ExactCountUnavailable";

        /// <summary>The collection was cancelled by the time limit before it finished.</summary>
        public const string CollectionTimedOut = "CollectionTimedOut";

        /// <summary>The collection failed; the Admiral log names the cause.</summary>
        public const string CollectionFailed = "CollectionFailed";
    }
}
