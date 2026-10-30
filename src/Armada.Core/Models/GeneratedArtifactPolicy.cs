namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Operator-declared artifacts that landing regenerates from the combined authored tree.
    /// </summary>
    public sealed class GeneratedArtifactPolicy
    {
        /// <summary>
        /// Exact repository-relative generated file paths. Authored paths are never resolved by this policy.
        /// </summary>
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>
        /// Command executed in the isolated landing worktree before candidate validation and review.
        /// </summary>
        public string RegenerateCommand { get; set; } = string.Empty;
    }
}
