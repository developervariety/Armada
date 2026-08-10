namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The result of searching the target revision for one subject term drawn from the mission.
    ///
    /// The negative result carries as much value as the positive one. A captain asked to add
    /// something that may already exist spends several turns proving absence before it writes any
    /// code, and a captain that fails to prove absence duplicates work that already landed. Stating
    /// the outcome either way removes that search from the mission.
    /// </summary>
    public class GitAnchorPriorArt
    {
        #region Public-Members

        /// <summary>
        /// The searched term. Empty when unset.
        /// </summary>
        public string Term
        {
            get
            {
                return _Term;
            }
            set
            {
                _Term = value ?? "";
            }
        }

        /// <summary>
        /// Whether the term was found on the searched revision.
        /// </summary>
        public bool Found { get; set; } = false;

        /// <summary>
        /// Number of tracked files that contain the term, clamped at or above zero.
        ///
        /// Files rather than lines, because a file count is exact and cheap to obtain, while a line
        /// count is neither: bounding a line search per file makes the total a sample, and a sampled
        /// number reported as a total is the kind of near-fact that sends a captain to verify it.
        /// </summary>
        public int MatchingFileCount
        {
            get
            {
                return _MatchingFileCount;
            }
            set
            {
                _MatchingFileCount = value < 0 ? 0 : value;
            }
        }

        /// <summary>
        /// Sample match locations in "path:line" form, capped by the resolver. Never null.
        /// </summary>
        public List<string> SampleLocations
        {
            get
            {
                return _SampleLocations;
            }
            set
            {
                _SampleLocations = value ?? new List<string>();
            }
        }

        #endregion

        #region Private-Members

        private string _Term = "";
        private int _MatchingFileCount = 0;
        private List<string> _SampleLocations = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public GitAnchorPriorArt()
        {
        }

        #endregion
    }
}
