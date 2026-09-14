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

        /// <summary>Restrict to jobs that are not terminal.</summary>
        public bool ActiveOnly { get; set; } = false;

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
