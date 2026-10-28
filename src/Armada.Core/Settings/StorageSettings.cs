namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Settings for the administrator storage inventory: a read-only, bounded, cached measurement of
    /// Armada's on-disk data and database. The inventory never deletes or moves anything.
    /// </summary>
    public class StorageSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether the inventory refreshes itself in the background. When false only a manual refresh
        /// collects. Defaults to true.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Seconds between background collections, 30 to 86400. Defaults to 900.</summary>
        public int RefreshIntervalSeconds
        {
            get => _RefreshIntervalSeconds;
            set => _RefreshIntervalSeconds = Math.Clamp(value, 30, 86400);
        }

        /// <summary>Seconds one collection may run before it ends partial, 1 to 300. Defaults to 30.</summary>
        public int TimeoutSeconds
        {
            get => _TimeoutSeconds;
            set => _TimeoutSeconds = Math.Clamp(value, 1, 300);
        }

        /// <summary>Files and directories one scan may examine, 100 to 2000000. Defaults to 250000.</summary>
        public int MaxEntries
        {
            get => _MaxEntries;
            set => _MaxEntries = Math.Clamp(value, 100, 2000000);
        }

        /// <summary>Directory levels below a location the scan may enter, 1 to 256. Defaults to 32.</summary>
        public int MaxDepth
        {
            get => _MaxDepth;
            set => _MaxDepth = Math.Clamp(value, 1, 256);
        }

        /// <summary>Largest files listed per location, 1 to 100. Defaults to 20.</summary>
        public int LargestFileLimit
        {
            get => _LargestFileLimit;
            set => _LargestFileLimit = Math.Clamp(value, 1, 100);
        }

        /// <summary>Growth-history samples kept, 2 to 2016. Defaults to 288.</summary>
        public int HistoryCapacity
        {
            get => _HistoryCapacity;
            set => _HistoryCapacity = Math.Clamp(value, 2, 2016);
        }

        #endregion

        #region Public-Methods

        /// <summary>Copy every value from another instance, keeping this object in place for readers that hold it.</summary>
        /// <param name="source">Source settings; null leaves this instance unchanged.</param>
        public void CopyFrom(StorageSettings? source)
        {
            if (source == null) return;
            Enabled = source.Enabled;
            RefreshIntervalSeconds = source.RefreshIntervalSeconds;
            TimeoutSeconds = source.TimeoutSeconds;
            MaxEntries = source.MaxEntries;
            MaxDepth = source.MaxDepth;
            LargestFileLimit = source.LargestFileLimit;
            HistoryCapacity = source.HistoryCapacity;
        }

        #endregion

        #region Private-Members

        private int _RefreshIntervalSeconds = 900;
        private int _TimeoutSeconds = 30;
        private int _MaxEntries = 250000;
        private int _MaxDepth = 32;
        private int _LargestFileLimit = 20;
        private int _HistoryCapacity = 288;

        #endregion
    }
}
