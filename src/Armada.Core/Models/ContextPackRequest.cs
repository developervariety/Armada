namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Request for building a dispatch-ready code context pack.
    /// </summary>
    public class ContextPackRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Mission goal used as the search query.
        /// </summary>
        public string Goal { get; set; } = "";

        /// <summary>
        /// Approximate token budget for the markdown pack.
        /// </summary>
        public int TokenBudget { get; set; } = 5000;

        /// <summary>
        /// Optional maximum result count.
        /// </summary>
        public int? MaxResults { get; set; } = null;

        /// <summary>
        /// When true, skip graph/impact expansion and build a search-only pack.
        /// </summary>
        public bool FastPackOnly { get; set; } = false;

        /// <summary>
        /// When true, build the pack from a lexical search of the local index only: no query embedding,
        /// graph expansion or summary. Dispatch uses it when the full pack for a mission's goal cannot be built.
        /// </summary>
        public bool LexicalOnly { get; set; } = false;

        /// <summary>
        /// Repository files the mission must read or change. Their chunks come first in the pack, ahead of
        /// ranked search evidence. Files the goal text names are added to this list by the pack builder.
        /// </summary>
        public List<string> AssignedPaths
        {
            get => _AssignedPaths;
            set => _AssignedPaths = value ?? new List<string>();
        }

        /// <summary>
        /// Sibling-checkout files the mission reads. A single-vessel index does not cover them, so the pack
        /// header lists them as inputs to read directly. Sibling paths the goal text names are added by the
        /// pack builder.
        /// </summary>
        public List<string> SiblingPaths
        {
            get => _SiblingPaths;
            set => _SiblingPaths = value ?? new List<string>();
        }

        /// <summary>
        /// Ref the mission checkout starts from. When it resolves to a commit other than the indexed
        /// commit, assigned files changed between the two are read at the start ref and the header says
        /// the indexed commit differs. Null means the default branch.
        /// </summary>
        public string? StartRef { get; set; } = null;

        #endregion

        #region Private-Members

        private List<string> _AssignedPaths = new List<string>();
        private List<string> _SiblingPaths = new List<string>();

        #endregion
    }
}
