namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Payload of the <c>mission.assignment_held</c> event that records the <c>no_eligible_captain</c> hold: the
    /// reason each captain of the mission's tenant was passed over.
    /// </summary>
    public sealed class NoEligibleCaptainPayload
    {
        /// <summary>Mission held from assignment.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Hold reason code.</summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>Persona of the held mission.</summary>
        public string? Persona { get; set; } = null;

        /// <summary>One entry per captain of the mission's tenant.</summary>
        public List<AssignmentHoldCaptainReason> Captains { get; set; } = new List<AssignmentHoldCaptainReason>();
    }
}
