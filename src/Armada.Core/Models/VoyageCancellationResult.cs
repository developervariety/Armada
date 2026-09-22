namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of cancelling a voyage through the shared voyage cancellation operation.
    /// </summary>
    public class VoyageCancellationResult
    {
        #region Public-Members

        /// <summary>
        /// The voyage as stored after the operation. A voyage that was already Cancelled or Complete
        /// is returned unchanged.
        /// </summary>
        public Voyage Voyage { get; set; } = new Voyage();

        /// <summary>
        /// Missions this operation moved to Cancelled. Missions that were already terminal are not listed.
        /// </summary>
        public List<Mission> CancelledMissions { get; set; } = new List<Mission>();

        #endregion
    }
}
