namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    /// <summary>
    /// The call, result-size, deadline and concurrency bounds of the mission change summary, with the state that
    /// enforces them. REST and MCP share <see cref="Default"/>, so a caller cannot widen a bound by switching
    /// surface. The per-mission call count is process-local and resets on an admiral restart, which ends every
    /// running captain.
    /// </summary>
    public sealed class MissionChangeSummaryBounds
    {
        #region Public-Members

        /// <summary>The bounds every surface uses.</summary>
        public static MissionChangeSummaryBounds Default { get; } = new MissionChangeSummaryBounds();

        /// <summary>Paths returned when the caller names no limit.</summary>
        public int DefaultMaxPaths
        {
            get { return _DefaultMaxPaths; }
            set { _DefaultMaxPaths = Math.Clamp(value, 1, MaxPathsCeiling); }
        }

        /// <summary>Most paths any caller may ask for.</summary>
        public int MaxPathsCeiling
        {
            get { return _MaxPathsCeiling; }
            set { _MaxPathsCeiling = Math.Max(1, value); }
        }

        /// <summary>Most bytes of comparison output read from git; past it the summary is unavailable.</summary>
        public int OutputLimitBytes
        {
            get { return _OutputLimitBytes; }
            set { _OutputLimitBytes = Math.Max(1024, value); }
        }

        /// <summary>Deadline for the whole read, across every git process it starts.</summary>
        public TimeSpan Deadline
        {
            get { return _Deadline; }
            set
            {
                if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Deadline), "Must be positive.");
                _Deadline = value;
            }
        }

        /// <summary>How long a read waits for a free slot before it answers busy.</summary>
        public TimeSpan ConcurrencyWait
        {
            get { return _ConcurrencyWait; }
            set { _ConcurrencyWait = value < TimeSpan.Zero ? TimeSpan.Zero : value; }
        }

        /// <summary>Calls one mission credential may make in this admiral process.</summary>
        public int MaxCallsPerMission
        {
            get { return _MaxCallsPerMission; }
            set { _MaxCallsPerMission = Math.Max(1, value); }
        }

        /// <summary>Most reads that may run at once.</summary>
        public int MaxConcurrentReads { get; }

        #endregion

        #region Private-Members

        private int _DefaultMaxPaths = 1000;
        private int _MaxPathsCeiling = 5000;
        private int _OutputLimitBytes = 4 * 1024 * 1024;
        private TimeSpan _Deadline = TimeSpan.FromSeconds(30);
        private TimeSpan _ConcurrencyWait = TimeSpan.FromSeconds(2);
        private int _MaxCallsPerMission = 60;
        private readonly SemaphoreSlim _Slots;
        private readonly ConcurrentDictionary<string, int> _CallsByMission = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the production bounds.
        /// </summary>
        /// <param name="maxConcurrentReads">Most reads that may run at once.</param>
        public MissionChangeSummaryBounds(int maxConcurrentReads = 4)
        {
            MaxConcurrentReads = Math.Max(1, maxConcurrentReads);
            _Slots = new SemaphoreSlim(MaxConcurrentReads, MaxConcurrentReads);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Clamp a requested path limit to these bounds.
        /// </summary>
        /// <param name="requested">Requested limit; null or below one uses the default.</param>
        /// <returns>The limit to apply.</returns>
        public int ResolveMaxPaths(int? requested)
        {
            if (!requested.HasValue || requested.Value < 1) return Math.Min(DefaultMaxPaths, MaxPathsCeiling);
            return Math.Min(requested.Value, MaxPathsCeiling);
        }

        /// <summary>
        /// Charge one call to a mission credential.
        /// </summary>
        /// <param name="missionId">The credential's mission.</param>
        /// <returns>The call count including this call.</returns>
        public int ChargeCall(string missionId)
        {
            return _CallsByMission.AddOrUpdate(missionId, 1, (_, previous) => previous + 1);
        }

        /// <summary>
        /// Wait for a free read slot.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a slot was taken; the caller must call <see cref="ReleaseSlot"/>.</returns>
        public System.Threading.Tasks.Task<bool> TryTakeSlotAsync(CancellationToken token)
        {
            return _Slots.WaitAsync(ConcurrencyWait, token);
        }

        /// <summary>Release a slot taken with <see cref="TryTakeSlotAsync"/>.</summary>
        public void ReleaseSlot()
        {
            _Slots.Release();
        }

        #endregion
    }
}
