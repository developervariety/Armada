namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Limits of the WebSocket event hub: the retained replay history a reconnecting client resumes from, and
    /// the outbound queue of each session. Read when the admiral starts.
    /// </summary>
    public class WebSocketSettings
    {
        #region Public-Members

        /// <summary>
        /// Most event frames the replay history retains. Clamped to 128 through 1,000,000.
        /// </summary>
        public int ReplayHistoryEvents
        {
            get => _ReplayHistoryEvents;
            set => _ReplayHistoryEvents = Math.Max(128, Math.Min(1000000, value));
        }

        /// <summary>
        /// Most UTF-8 bytes the replay history retains. A single frame larger than this cannot be retained,
        /// so a resume from before it reports a gap. Clamped to 1 MiB through 1 GiB.
        /// </summary>
        public int ReplayHistoryBytes
        {
            get => _ReplayHistoryBytes;
            set => _ReplayHistoryBytes = Math.Max(1024 * 1024, Math.Min(1024 * 1024 * 1024, value));
        }

        /// <summary>
        /// Most frames one session's outbound queue holds behind the active send. A session whose queue fills
        /// is disconnected, and a subscription replays at most this many frames. Clamped to 64 through 65,536.
        /// </summary>
        public int ClientQueueCapacity
        {
            get => _ClientQueueCapacity;
            set => _ClientQueueCapacity = Math.Max(64, Math.Min(65536, value));
        }

        #endregion

        #region Private-Members

        private int _ReplayHistoryEvents = 8192;
        private int _ReplayHistoryBytes = 32 * 1024 * 1024;
        private int _ClientQueueCapacity = 1024;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public WebSocketSettings()
        {
        }

        #endregion
    }
}
