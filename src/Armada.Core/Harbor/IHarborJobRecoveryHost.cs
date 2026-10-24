namespace Armada.Core.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Rebuilds lifecycle ownership for a durable Harbor job before links accept runner messages.</summary>
    public interface IHarborJobRecoveryHost
    {
        /// <summary>Restore the exact mission attempt and synthetic process registration.</summary>
        Task<HarborJobRecoveryResult> RestoreAsync(HarborJobSnapshot job, CancellationToken token = default);

        /// <summary>Validate a runner's authenticated resume proof and attach the restored observer.</summary>
        Task<HarborJobRecoveryResult> TryAdoptAsync(HarborJobSnapshot job, HarborRunnerSession session, CancellationToken token = default);

        /// <summary>Deliver a confirmed terminal result to the exact lifecycle attempt.</summary>
        Task<bool> DeliverCompletionAsync(HarborJobSnapshot job, CancellationToken token = default);
    }

}
