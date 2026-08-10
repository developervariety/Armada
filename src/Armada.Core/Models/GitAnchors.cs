namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The git facts a captain would otherwise spend its first turns discovering: where its branch
    /// starts, what recently touched the files it must change, and whether its subject already
    /// exists on the target branch.
    ///
    /// The admiral resolves these once per dispatch with plain git. A captain that derives them
    /// itself spends several tool calls on search output that then occupies its context window for
    /// the rest of the mission, in place of the source it came to read.
    ///
    /// Resolution never blocks a dispatch. When it cannot run, <see cref="ResolutionError"/> holds
    /// the reason and the block is rendered empty with that reason stated, so a missing anchors
    /// section is never read as "no prior art exists".
    /// </summary>
    public class GitAnchors
    {
        #region Public-Members

        /// <summary>
        /// Branch the mission targets. Empty when unset.
        /// </summary>
        public string TargetBranch
        {
            get
            {
                return _TargetBranch;
            }
            set
            {
                _TargetBranch = value ?? "";
            }
        }

        /// <summary>
        /// Commit the mission branch starts from. Empty when unresolved.
        /// </summary>
        public string BaseCommit
        {
            get
            {
                return _BaseCommit;
            }
            set
            {
                _BaseCommit = value ?? "";
            }
        }

        /// <summary>
        /// Tip commit of the target branch at dispatch time. Empty when unresolved.
        /// </summary>
        public string TargetTip
        {
            get
            {
                return _TargetTip;
            }
            set
            {
                _TargetTip = value ?? "";
            }
        }

        /// <summary>
        /// Recent history of each path the mission names. Never null.
        /// </summary>
        public List<GitAnchorFileHistory> Files
        {
            get
            {
                return _Files;
            }
            set
            {
                _Files = value ?? new List<GitAnchorFileHistory>();
            }
        }

        /// <summary>
        /// Prior-art search results for the mission's subject terms. Never null.
        /// </summary>
        public List<GitAnchorPriorArt> PriorArt
        {
            get
            {
                return _PriorArt;
            }
            set
            {
                _PriorArt = value ?? new List<GitAnchorPriorArt>();
            }
        }

        /// <summary>
        /// Why resolution produced nothing, when it failed or was skipped. Null when resolution ran.
        /// </summary>
        public string? ResolutionError { get; set; } = null;

        /// <summary>
        /// True when the block carries at least one resolved fact worth rendering.
        /// </summary>
        public bool HasContent
        {
            get
            {
                return !String.IsNullOrEmpty(_BaseCommit)
                    || !String.IsNullOrEmpty(_TargetTip)
                    || _Files.Count > 0
                    || _PriorArt.Count > 0;
            }
        }

        #endregion

        #region Private-Members

        private string _TargetBranch = "";
        private string _BaseCommit = "";
        private string _TargetTip = "";
        private List<GitAnchorFileHistory> _Files = new List<GitAnchorFileHistory>();
        private List<GitAnchorPriorArt> _PriorArt = new List<GitAnchorPriorArt>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public GitAnchors()
        {
        }

        /// <summary>
        /// Create an unresolved instance that states why it is empty.
        /// </summary>
        /// <param name="reason">Reason resolution produced nothing.</param>
        /// <returns>Instance carrying only the reason.</returns>
        public static GitAnchors Unresolved(string reason)
        {
            GitAnchors anchors = new GitAnchors();
            anchors.ResolutionError = String.IsNullOrWhiteSpace(reason) ? "reason not recorded" : reason.Trim();
            return anchors;
        }

        #endregion
    }
}
