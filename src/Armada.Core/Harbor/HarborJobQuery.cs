namespace Armada.Core.Harbor
{
    using System;

    /// <summary>Filter for reading durable Harbor job records, newest first.</summary>
    public sealed class HarborJobQuery
    {
        #region Public-Members

        /// <summary>Restrict to one tenant.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>Restrict to one user.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Restrict to one runner.</summary>
        public string? RunnerId { get; set; } = null;

        /// <summary>Restrict to one mission.</summary>
        public string? MissionId { get; set; } = null;

        /// <summary>Restrict to one dock.</summary>
        public string? DockId { get; set; } = null;

        /// <summary>Return job identifiers after this key in ascending order for recovery paging.</summary>
        public string? JobIdAfter { get; set; } = null;

        /// <summary>Restrict to mission jobs whose dock identity was not stored.</summary>
        public bool UnboundMissionOnly { get; set; } = false;

        /// <summary>Restrict to jobs that are not terminal.</summary>
        public bool ActiveOnly { get; set; } = false;

        /// <summary>Return active, unconfirmed-terminal, and terminal-undelivered records for startup recovery.</summary>
        public bool RecoveryOnly { get; set; } = false;

        /// <summary>Return records whose runner stop is not confirmed.</summary>
        public bool UnconfirmedOnly { get; set; } = false;

        /// <summary>Maximum records returned, 1 to 1000.</summary>
        public int Limit
        {
            get => _Limit;
            set
            {
                if (value < 1 || value > 1000) throw new ArgumentOutOfRangeException(nameof(Limit), "Must be in range [1, 1000]");
                _Limit = value;
            }
        }

        #endregion

        #region Private-Members

        private int _Limit = 100;

        #endregion
    }
}
