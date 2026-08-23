namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Point-in-time view of an active dispatch hold.
    /// </summary>
    public class DispatchHoldSnapshot
    {
        /// <summary>
        /// Why the hold was engaged.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// Who engaged the hold, when known.
        /// </summary>
        public string? SetBy { get; set; } = null;

        /// <summary>
        /// When the hold was engaged, in UTC.
        /// </summary>
        public DateTime SetByUtc { get; set; } = DateTime.UtcNow;
    }
}
