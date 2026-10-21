namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A Challenger's ruling on one numbered Judge finding, as written in its <c>[ARMADA:CHALLENGE]</c> line.
    /// </summary>
    public class JudgeChallengeRuling
    {
        #region Public-Members

        /// <summary>
        /// One-based finding number from the Challenger brief.
        /// </summary>
        public int Index { get; set; } = 0;

        /// <summary>
        /// Finding kind the Challenger assigned (FACT, MISSING, DEFECT, SCOPE, PROOF, BOUNDARY, FORMAT,
        /// PROCESS, PENDING or CONFLICT).
        /// </summary>
        public string? Kind { get; set; } = null;

        /// <summary>
        /// UPHOLD or REFUTE.
        /// </summary>
        public string? Ruling { get; set; } = null;

        /// <summary>
        /// Challenger confidence in its ruling, 0.0-1.0.
        /// </summary>
        public double Confidence { get; set; } = 0.0;

        /// <summary>
        /// The quote or file:line the ruling rests on.
        /// </summary>
        public string? Evidence { get; set; } = null;

        #endregion
    }
}
