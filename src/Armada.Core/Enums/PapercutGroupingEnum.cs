namespace Armada.Core.Enums
{
    /// <summary>
    /// How a papercut listing collapses reports into groups.
    /// </summary>
    public enum PapercutGroupingEnum
    {
        /// <summary>
        /// One group per vessel. Each group summarizes its categories and its largest title subgroups.
        /// </summary>
        Vessel,

        /// <summary>
        /// One group per vessel and category. Each group summarizes its largest title subgroups.
        /// </summary>
        Category,

        /// <summary>
        /// One group per vessel, category, and normalized title.
        /// </summary>
        Title
    }
}
