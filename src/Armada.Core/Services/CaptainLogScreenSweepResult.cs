namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What one captain-log screening sweep did. Every reason a sweep performed no work has its own
    /// outcome value, so "nothing was eligible" is never reported as an absent status.
    /// </summary>
    public enum CaptainLogScreenSweepOutcomeEnum
    {
        /// <summary>The sweep ran and screened every eligible mission.</summary>
        Completed,

        /// <summary>Screening is switched off in settings, so no log was read.</summary>
        Disabled,

        /// <summary>The configured interval since the last sweep has not elapsed.</summary>
        IntervalNotElapsed,

        /// <summary>No screening pass is registered, so a sweep would read logs and ask nothing.</summary>
        NoPasses,

        /// <summary>No mission is in progress.</summary>
        NoActiveMissions,

        /// <summary>The sweep itself failed; <see cref="CaptainLogScreenSweepResult.Reason"/> names the cause.</summary>
        Failed
    }

    /// <summary>
    /// The result of one screening sweep. The counters separate the reasons a mission was not
    /// screened, so a quiet sweep says which kind of quiet it was.
    /// </summary>
    public class CaptainLogScreenSweepResult
    {
        #region Public-Members

        /// <summary>What the sweep did.</summary>
        public CaptainLogScreenSweepOutcomeEnum Outcome { get; set; } = CaptainLogScreenSweepOutcomeEnum.Completed;

        /// <summary>
        /// Why the sweep did no work, for every outcome other than
        /// <see cref="CaptainLogScreenSweepOutcomeEnum.Completed"/>. Null on a completed sweep.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>Missions whose tail was read and passed to every registered pass.</summary>
        public int Screened { get; set; } = 0;

        /// <summary>Screened missions that produced at least one finding.</summary>
        public int Flagged { get; set; } = 0;

        /// <summary>Screened missions that produced no finding.</summary>
        public int Clean { get; set; } = 0;

        /// <summary>Missions skipped because their tail had not changed since the last screen.</summary>
        public int UnchangedTails { get; set; } = 0;

        /// <summary>Missions skipped because they were flagged inside the cooldown window.</summary>
        public int InCooldown { get; set; } = 0;

        /// <summary>Missions skipped because no log resolved for them.</summary>
        public int NoLog { get; set; } = 0;

        /// <summary>Missions whose own screen failed. The reason of each is logged as it happens.</summary>
        public int Errors { get; set; } = 0;

        /// <summary>Findings this sweep produced, by rule class.</summary>
        public Dictionary<string, int> FindingsByClass { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainLogScreenSweepResult()
        {
        }

        #endregion
    }
}
