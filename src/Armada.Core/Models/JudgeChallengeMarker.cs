namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// The JSON value of a Challenger's <c>[ARMADA:CHALLENGE]</c> line.
    /// </summary>
    public class JudgeChallengeMarker
    {
        #region Public-Members

        /// <summary>
        /// One ruling per numbered finding.
        /// </summary>
        public List<JudgeChallengeRuling>? Findings { get; set; } = null;

        #endregion
    }
}
