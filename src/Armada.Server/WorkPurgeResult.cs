namespace Armada.Server
{
    /// <summary>
    /// Outcome of permanently deleting a mission or a voyage.
    /// </summary>
    public sealed class WorkPurgeResult
    {
        #region Public-Members

        /// <summary>
        /// Refusal code for a mission a captain is working, or a voyage that is live or holds such a mission.
        /// </summary>
        public const string WorkActiveCode = "work_active";

        /// <summary>
        /// Refusal code for a mission whose dock could not be removed through the dock service.
        /// </summary>
        public const string DockUnavailableCode = "dock_cleanup_unavailable";

        /// <summary>
        /// True when the record was deleted.
        /// </summary>
        public bool Succeeded { get; private set; }

        /// <summary>
        /// Refusal code when refused; null on success.
        /// </summary>
        public string? Code { get; private set; }

        /// <summary>
        /// Refusal reason when refused; null on success.
        /// </summary>
        public string? Message { get; private set; }

        /// <summary>
        /// The mission or voyage identifier.
        /// </summary>
        public string Id { get; private set; } = "";

        /// <summary>
        /// Missions deleted: one for a mission purge, every mission of the voyage for a voyage purge.
        /// </summary>
        public int MissionsDeleted { get; private set; }

        #endregion

        #region Constructors-and-Factories

        private WorkPurgeResult()
        {
        }

        /// <summary>
        /// A completed purge.
        /// </summary>
        /// <param name="id">Deleted record identifier.</param>
        /// <param name="missionsDeleted">Missions deleted.</param>
        /// <returns>The result.</returns>
        public static WorkPurgeResult Deleted(string id, int missionsDeleted)
        {
            return new WorkPurgeResult { Succeeded = true, Id = id, MissionsDeleted = missionsDeleted };
        }

        /// <summary>
        /// A refused purge. Nothing was deleted.
        /// </summary>
        /// <param name="id">Record identifier.</param>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Refusal reason.</param>
        /// <returns>The result.</returns>
        public static WorkPurgeResult Refused(string id, string code, string message)
        {
            return new WorkPurgeResult { Succeeded = false, Id = id, Code = code, Message = message };
        }

        #endregion
    }
}
