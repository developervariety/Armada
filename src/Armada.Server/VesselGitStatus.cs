namespace Armada.Server
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How far a vessel's working checkout is from the remote default branch.
    /// </summary>
    public class VesselGitStatus
    {
        #region Public-Members

        /// <summary>
        /// Vessel the status belongs to.
        /// </summary>
        public string VesselId { get; set; } = string.Empty;

        /// <summary>
        /// Commits the checkout has that the remote default branch lacks; null when the status could not be read.
        /// </summary>
        public int? CommitsAhead { get; set; } = null;

        /// <summary>
        /// Commits the remote default branch has that the checkout lacks; null when the status could not be read.
        /// </summary>
        public int? CommitsBehind { get; set; } = null;

        /// <summary>
        /// Why the status could not be read, or null when it was.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; } = null;

        #endregion
    }
}
