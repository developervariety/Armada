namespace Armada.Core.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Resumes a confirmed Harbor completion whose mission transition was only partly applied.</summary>
    public interface IHarborCompletionRecovery
    {
        /// <summary>Finish an exact pending mission attempt after its runner has confirmed exit.</summary>
        /// <param name="job">Confirmed durable Harbor job.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True only when the same attempt's pending transition and cleanup are complete.</returns>
        Task<bool> ResumeConfirmedCompletionAsync(HarborJobSnapshot job, CancellationToken token = default);
    }
}
