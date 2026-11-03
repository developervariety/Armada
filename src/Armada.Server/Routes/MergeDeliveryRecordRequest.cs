namespace Armada.Server.Routes
{
    /// <summary>
    /// Body of <c>POST /api/v1/merge-queue/{id}/delivery-evidence</c>.
    /// </summary>
    public class MergeDeliveryRecordRequest
    {
        /// <summary>
        /// Commit that carries the entry's work on its target branch.
        /// </summary>
        public string? Commit { get; set; } = null;

        /// <summary>
        /// Optional incident the record answers.
        /// </summary>
        public string? IncidentId { get; set; } = null;
    }
}
