namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Why one captain of the mission's tenant cannot take a mission that no captain can take.
    /// </summary>
    public sealed class AssignmentHoldCaptainReason
    {
        /// <summary>Captain identifier.</summary>
        public string CaptainId { get; set; } = String.Empty;

        /// <summary>Captain name.</summary>
        public string CaptainName { get; set; } = String.Empty;

        /// <summary>Reason code that excludes the captain, such as <c>quarantined</c> or <c>persona_not_allowed</c>.</summary>
        public string Reason { get; set; } = String.Empty;
    }
}
