namespace Armada.Server
{
    /// <summary>
    /// Response of GET /api/v1/vessels/{id}/git-status: commits ahead of and behind the remote default branch.
    /// </summary>
    public class VesselGitStatusResponse
    {
        /// <summary>
        /// Vessel ID.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Commits on the checkout not on the remote default branch, or null when unknown.
        /// </summary>
        public int? CommitsAhead { get; set; } = null;

        /// <summary>
        /// Commits on the remote default branch not on the checkout, or null when unknown.
        /// </summary>
        public int? CommitsBehind { get; set; } = null;

        /// <summary>
        /// Why the counts are unknown, or null.
        /// </summary>
        public string? Error { get; set; } = null;
    }
}
