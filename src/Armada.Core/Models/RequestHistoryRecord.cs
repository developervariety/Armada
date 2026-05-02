namespace Armada.Core.Models
{
    /// <summary>
    /// Combined request-history entry and expanded detail.
    /// </summary>
    public class RequestHistoryRecord
    {
        #region Public-Members

        /// <summary>
        /// Summary entry.
        /// </summary>
        public RequestHistoryEntry Entry { get; set; } = new RequestHistoryEntry();

        /// <summary>
        /// Expanded detail.
        /// </summary>
        public RequestHistoryDetail? Detail { get; set; } = null;

        #endregion
    }
}
