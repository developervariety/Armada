namespace Armada.Core.Services
{
    using Armada.Core.Enums;

    /// <summary>
    /// The one classification of merge entry statuses. The landing job has its own lifecycle and its own terminal
    /// set; this class classifies the merge entry only.
    /// </summary>
    public static class MergeStatusRules
    {
        /// <summary>
        /// Whether a merge entry has finished: Landed, Failed or Cancelled. A finished entry keeps its recorded
        /// outcome; it is never cancelled and only a finished entry may be purged.
        /// </summary>
        /// <param name="status">Merge entry status.</param>
        /// <returns>True when the entry has finished.</returns>
        public static bool IsTerminal(MergeStatusEnum status)
        {
            return status == MergeStatusEnum.Landed
                || status == MergeStatusEnum.Failed
                || status == MergeStatusEnum.Cancelled;
        }
    }
}
