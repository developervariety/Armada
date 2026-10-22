namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One title group inside a vessel or category papercut group: the reports that share a category
    /// and a normalized title.
    /// </summary>
    public class PapercutTitleSubgroup
    {
        #region Public-Members

        /// <summary>
        /// Title grouping key: vessel, category, and normalized title. It equals the
        /// <see cref="PapercutGroup.Key"/> of the matching group in a title listing.
        /// </summary>
        public string Key { get; set; } = "";

        /// <summary>
        /// Friction category of the subgroup.
        /// </summary>
        public PapercutCategoryEnum Category { get; set; } = PapercutCategoryEnum.Other;

        /// <summary>
        /// Normalized title the reports share.
        /// </summary>
        public string NormalizedTitle { get; set; } = "";

        /// <summary>
        /// Title of the most recent report in the subgroup.
        /// </summary>
        public string SampleTitle { get; set; } = "";

        /// <summary>
        /// Number of reports in the subgroup.
        /// </summary>
        public int Count { get; set; } = 0;

        /// <summary>
        /// Latest report timestamp in UTC.
        /// </summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PapercutTitleSubgroup()
        {
        }

        #endregion
    }
}
