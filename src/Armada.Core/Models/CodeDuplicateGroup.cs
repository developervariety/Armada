namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Chunks linked by pairwise similarity at or above the request threshold. Membership is transitive:
    /// two members of a group may be less similar to each other than the threshold when both are similar
    /// to a third member, so <see cref="MinPairSimilarity"/> describes the weakest linking pair only.
    /// </summary>
    public class CodeDuplicateGroup
    {
        #region Public-Members

        /// <summary>
        /// Members ordered by path, then start line.
        /// </summary>
        public List<CodeDuplicateMember> Members { get; set; } = new List<CodeDuplicateMember>();

        /// <summary>
        /// Highest similarity among the pairs that formed the group.
        /// </summary>
        public double MaxPairSimilarity { get; set; } = 0;

        /// <summary>
        /// Lowest similarity among the pairs that formed the group.
        /// </summary>
        public double MinPairSimilarity { get; set; } = 0;

        /// <summary>
        /// True when every member has the same content after trimming each line.
        /// </summary>
        public bool IdenticalContent { get; set; } = false;

        #endregion
    }
}
