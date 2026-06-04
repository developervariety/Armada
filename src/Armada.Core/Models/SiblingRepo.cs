namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Declaration of a sibling (dependency) repository that must be provisioned alongside a
    /// vessel's primary worktree so consumer projects that resolve cross-repo sources via
    /// MSBuild parent-probe paths can compile and test inside an Armada-provisioned dock.
    /// Each entry identifies the source repo (a known Armada vessel reference and/or a git
    /// URL), the relative checkout path the consumer's probes expect, and the branch-selection
    /// strategy used to keep the sibling compatible with the dock branch.
    /// </summary>
    public class SiblingRepo
    {
        #region Public-Members

        /// <summary>
        /// Optional reference to a known Armada vessel (vessel ID or name) that supplies the
        /// sibling source. Resolved before <see cref="RepoUrl"/>. When set and resolvable, the
        /// referenced vessel's bare repository is used as the checkout source.
        /// </summary>
        public string? VesselRef { get; set; } = null;

        /// <summary>
        /// Optional git URL for the sibling source. Used when <see cref="VesselRef"/> is unset
        /// or cannot be resolved to a known vessel. The repository is bare-cloned into the
        /// repos directory on first use and reused thereafter.
        /// </summary>
        public string? RepoUrl { get; set; } = null;

        /// <summary>
        /// Relative checkout path, resolved against the dock's primary worktree directory, where
        /// the sibling is materialized. Use "../Name" style paths to place the sibling next to
        /// the dock so the consumer's parent-probe arithmetic resolves (e.g. "..\\JproDeobfuscator").
        /// Required.
        /// </summary>
        public string RelativePath { get; set; } = String.Empty;

        /// <summary>
        /// Strategy used to select the sibling ref relative to the dock branch.
        /// Defaults to <see cref="SiblingBranchStrategyEnum.MatchBranchElseDefault"/>.
        /// </summary>
        public SiblingBranchStrategyEnum BranchStrategy { get; set; } = SiblingBranchStrategyEnum.MatchBranchElseDefault;

        /// <summary>
        /// Optional default branch for the sibling, used as the fallback base ref when no
        /// dock-matching branch exists. Null falls back to "main".
        /// </summary>
        public string? DefaultBranch { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty sibling repo declaration.
        /// </summary>
        public SiblingRepo()
        {
        }

        #endregion
    }
}
