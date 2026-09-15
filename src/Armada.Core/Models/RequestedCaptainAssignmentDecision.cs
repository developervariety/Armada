namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Result of applying a mission's requested captain and stored fallback tier to an assignable captain pool.
    /// </summary>
    public class RequestedCaptainAssignmentDecision
    {
        #region Public-Members

        /// <summary>
        /// How the requested captain and tier were applied.
        /// </summary>
        public RequestedCaptainOutcomeEnum Outcome { get; set; } = RequestedCaptainOutcomeEnum.NotRequested;

        /// <summary>
        /// Requested captain identifier stored on the mission, or null.
        /// </summary>
        public string? RequestedCaptainId { get; set; } = null;

        /// <summary>
        /// The requested captain when it is assigned, otherwise null.
        /// </summary>
        public Captain? Captain { get; set; } = null;

        /// <summary>
        /// Tier floor applied to the fallback, or null when no floor applies.
        /// </summary>
        public CaptainTierEnum? FallbackTier { get; set; } = null;

        /// <summary>
        /// Captains normal routing may choose from after this decision.
        /// </summary>
        public List<Captain> Candidates
        {
            get => _Candidates;
            set => _Candidates = value ?? new List<Captain>();
        }

        /// <summary>
        /// Why the requested captain was not assigned, naming the captain and the tier. Empty when the
        /// outcome is <see cref="RequestedCaptainOutcomeEnum.NotRequested"/> or
        /// <see cref="RequestedCaptainOutcomeEnum.AssignRequested"/>.
        /// </summary>
        public string Reason
        {
            get => _Reason;
            set => _Reason = value ?? String.Empty;
        }

        #endregion

        #region Private-Members

        private List<Captain> _Candidates = new List<Captain>();
        private string _Reason = String.Empty;

        #endregion
    }
}
