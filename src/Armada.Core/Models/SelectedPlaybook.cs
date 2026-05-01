namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Identifies a playbook selected for a voyage or mission along with the delivery mode.
    /// </summary>
    public class SelectedPlaybook
    {
        /// <summary>
        /// Playbook identifier.
        /// </summary>
        public string PlaybookId { get; set; } = "";

        /// <summary>
        /// Delivery mode to use for this selection.
        /// </summary>
        public PlaybookDeliveryModeEnum DeliveryMode { get; set; } = PlaybookDeliveryModeEnum.InlineFullContent;

        /// <summary>
        /// Optional inline playbook body. When non-null, the dispatch pipeline
        /// short-circuits the curated-playbook DB lookup and ships this content
        /// directly with the mission. Used by recovery flows that ship a
        /// compile-time playbook (for example <c>pbk_rebase_captain</c>) without
        /// requiring a tenant-scoped playbook row to exist.
        /// </summary>
        public string? InlineFullContent { get; set; } = null;
    }
}
