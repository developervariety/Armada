namespace Armada.Core.Models
{
    /// <summary>
    /// One page of a vessel's branches, with the vessel's default branch and a git error when the branches could not be
    /// read (then the page is empty).
    /// </summary>
    public class BranchEnumerationResult : EnumerationResult<BranchInfo>
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = string.Empty;

        /// <summary>
        /// The vessel's default branch.
        /// </summary>
        public string DefaultBranch { get; set; } = "main";

        /// <summary>
        /// Why the branches could not be read, or null.
        /// </summary>
        public string? Error { get; set; } = null;

        #endregion
    }
}
