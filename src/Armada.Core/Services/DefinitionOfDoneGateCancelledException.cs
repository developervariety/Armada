namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Thrown by <see cref="DefinitionOfDoneGate.EvaluateAsync"/> when the evaluation stopped because its mission was
    /// cancelled. The gate produced no result; the mission's own status records the cancel.
    /// </summary>
    public class DefinitionOfDoneGateCancelledException : OperationCanceledException
    {
        #region Public-Members

        /// <summary>
        /// Mission whose evaluation was cancelled.
        /// </summary>
        public string MissionId { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the exception for a mission.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        public DefinitionOfDoneGateCancelledException(string missionId)
            : base("Definition-of-done gate for mission " + missionId + " stopped because the mission was cancelled.")
        {
            MissionId = missionId ?? throw new ArgumentNullException(nameof(missionId));
        }

        #endregion
    }
}
