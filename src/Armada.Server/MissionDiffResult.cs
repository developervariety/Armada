namespace Armada.Server
{
    /// <summary>
    /// Outcome of <see cref="MissionDiffReader.ReadAsync"/>.
    /// </summary>
    public sealed class MissionDiffResult
    {
        #region Public-Members

        /// <summary>
        /// True when a diff was found.
        /// </summary>
        public bool Available { get; private set; }

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; private set; } = "";

        /// <summary>
        /// Branch the diff belongs to.
        /// </summary>
        public string Branch { get; private set; } = "";

        /// <summary>
        /// Diff text, or null when none was found.
        /// </summary>
        public string? Diff { get; private set; }

        /// <summary>
        /// Why no diff was found, or null when one was.
        /// </summary>
        public string? Message { get; private set; }

        #endregion

        #region Constructors-and-Factories

        private MissionDiffResult()
        {
        }

        /// <summary>
        /// A found diff.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="branch">Branch.</param>
        /// <param name="diff">Diff text.</param>
        /// <returns>The result.</returns>
        public static MissionDiffResult Found(string missionId, string branch, string diff)
        {
            return new MissionDiffResult { Available = true, MissionId = missionId, Branch = branch, Diff = diff };
        }

        /// <summary>
        /// No diff.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="message">Why.</param>
        /// <returns>The result.</returns>
        public static MissionDiffResult NotAvailable(string missionId, string message)
        {
            return new MissionDiffResult { Available = false, MissionId = missionId, Message = message };
        }

        #endregion
    }
}
