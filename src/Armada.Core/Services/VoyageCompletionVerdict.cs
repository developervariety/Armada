namespace Armada.Core.Services
{
    using Armada.Core.Enums;

    /// <summary>
    /// What <see cref="VoyageCompletionRule"/> decided for one voyage.
    /// </summary>
    public sealed class VoyageCompletionVerdict
    {
        /// <summary>The terminal status to write, or null when the voyage is kept as it is.</summary>
        public VoyageStatusEnum? NewStatus { get; }

        /// <summary>Why the rule decided this; one of the <c>Reason*</c> constants on <see cref="VoyageCompletionRule"/>.</summary>
        public string Reason { get; }

        private VoyageCompletionVerdict(VoyageStatusEnum? newStatus, string reason)
        {
            NewStatus = newStatus;
            Reason = reason;
        }

        internal static VoyageCompletionVerdict Keep(string reason) => new VoyageCompletionVerdict(null, reason);

        internal static VoyageCompletionVerdict Finish(VoyageStatusEnum status, string reason) => new VoyageCompletionVerdict(status, reason);
    }
}
