namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of a request to stop one job on a Harbor.
    /// </summary>
    public class HarborJobStopResult
    {
        #region Public-Members

        /// <summary>
        /// What happened.
        /// </summary>
        public HarborJobStopStatusEnum Status { get; set; } = HarborJobStopStatusEnum.NotFound;

        /// <summary>
        /// The job record, or null when it was not found.
        /// </summary>
        public HarborJobRecord? Job { get; set; } = null;

        /// <summary>
        /// Human-readable explanation.
        /// </summary>
        public string Message { get; set; } = "";

        #endregion
    }
}
