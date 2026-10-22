namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Repeated reports of the same friction, collapsed into one row. One captain reporting a problem
    /// is an anecdote. Nine captains reporting it is a defect with evidence, and the count is the part
    /// an operator triages on.
    /// </summary>
    public class PapercutGroup
    {
        #region Public-Members

        /// <summary>
        /// Grouping key. A title group keys on vessel, category, and normalized title; a category
        /// group on vessel and category; a vessel group on the vessel alone.
        /// </summary>
        public string Key { get; set; } = "";

        /// <summary>
        /// Grouping mode that produced the group.
        /// </summary>
        public PapercutGroupingEnum GroupBy { get; set; } = PapercutGroupingEnum.Title;

        /// <summary>
        /// Vessel the friction was reported against, or null when the report carried no vessel.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Name of the vessel, when the listing could read it.
        /// </summary>
        public string? VesselName { get; set; } = null;

        /// <summary>
        /// Friction category. A vessel group spans categories, so it names the category with the most
        /// reports; <see cref="CategoryCounts"/> carries the full split.
        /// </summary>
        public PapercutCategoryEnum Category { get; set; } = PapercutCategoryEnum.Other;

        /// <summary>
        /// Number of reports per category name.
        /// </summary>
        public Dictionary<string, int> CategoryCounts
        {
            get => _CategoryCounts;
            set => _CategoryCounts = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Number of distinct title groups (category and normalized title) inside the group. A title
        /// group counts one.
        /// </summary>
        public int TitleGroupCount { get; set; } = 1;

        /// <summary>
        /// The largest title groups inside a vessel or category group, by count and then most recent
        /// report. Empty for a title group.
        /// </summary>
        public List<PapercutTitleSubgroup> TitleSubgroups
        {
            get => _TitleSubgroups;
            set => _TitleSubgroups = value ?? new List<PapercutTitleSubgroup>();
        }

        /// <summary>
        /// Highest severity reported in the group.
        /// </summary>
        public PapercutSeverityEnum HighestSeverity { get; set; } = PapercutSeverityEnum.Low;

        /// <summary>
        /// Title of the most recent report in the group.
        /// </summary>
        public string SampleTitle { get; set; } = "";

        /// <summary>
        /// Detail of the most recent report in the group.
        /// </summary>
        public string? SampleDetail { get; set; } = null;

        /// <summary>
        /// Path of the most recent report in the group that carried one.
        /// </summary>
        public string? SamplePath { get; set; } = null;

        /// <summary>
        /// Number of reports in the group.
        /// </summary>
        public int Count { get; set; } = 0;

        /// <summary>
        /// Number of distinct captains that reported it. A high count from one captain is a habit;
        /// a high count across captains is a real defect.
        /// </summary>
        public int DistinctCaptainCount { get; set; } = 0;

        /// <summary>
        /// Earliest report timestamp in UTC.
        /// </summary>
        public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Latest report timestamp in UTC.
        /// </summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Up to five mission identifiers that reported it, newest first, for evidence links.
        /// </summary>
        public List<string> SampleMissionIds
        {
            get => _SampleMissionIds;
            set => _SampleMissionIds = value ?? new List<string>();
        }

        /// <summary>
        /// Keys of other groups the typed-decision merge (D6 <c>papercut_merge</c>) folded into this
        /// one at listing time. Empty when the group was not model-merged. This is a listing-time
        /// annotation only: the stored papercut events are never changed and no group is deleted, so
        /// the merge is transparent and reversible by turning the decision off.
        /// </summary>
        public List<string> MergedGroupKeys
        {
            get => _MergedGroupKeys;
            set => _MergedGroupKeys = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private List<string> _SampleMissionIds = new List<string>();
        private List<string> _MergedGroupKeys = new List<string>();
        private Dictionary<string, int> _CategoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private List<PapercutTitleSubgroup> _TitleSubgroups = new List<PapercutTitleSubgroup>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PapercutGroup()
        {
        }

        #endregion
    }
}
