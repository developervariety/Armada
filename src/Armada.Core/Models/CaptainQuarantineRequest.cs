namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Manual captain quarantine request body.
    /// </summary>
    public class CaptainQuarantineRequest
    {
        #region Public-Members

        /// <summary>
        /// Required operator reason.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Optional future UTC expiry. Takes precedence over <see cref="DurationMinutes"/>.
        /// </summary>
        public DateTime? UntilUtc { get; set; } = null;

        /// <summary>
        /// Optional positive hold duration in minutes from now, used when <see cref="UntilUtc"/> is absent.
        /// With neither value the hold is indefinite until released.
        /// </summary>
        public int? DurationMinutes { get; set; } = null;

        #endregion
    }
}
