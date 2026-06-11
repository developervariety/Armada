namespace Armada.Core.Models
{
    /// <summary>
    /// JSON payload stored on Nudge and Mail signals targeting a voyage or mission mailbox.
    /// Injected into downstream mission briefs at the pipeline handoff boundary.
    /// </summary>
    public class VoyageMailboxSignalPayload
    {
        #region Public-Members

        /// <summary>
        /// Target mission identifier. Null when the signal addresses an entire voyage.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Target voyage identifier.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Orchestrator note or nudge message content.
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// Identifier of the creator: a captain id, user id, or "system".
        /// </summary>
        public string? CreatedBy { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public VoyageMailboxSignalPayload()
        {
        }

        #endregion
    }
}
