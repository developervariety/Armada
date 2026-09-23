namespace Armada.Core.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Resolves the authoritative verified owner of a Harbor runner identifier.
    /// Implementations must read durable enrollment or equivalent owner state that
    /// is independent of the live session registry.
    /// </summary>
    public interface IHarborRunnerOwnerResolver
    {
        /// <summary>
        /// Resolve a runner's current owner and enrollment generation. A changed generation invalidates work from an
        /// earlier runner connection, even when the credential and principal are unchanged. A resolver that does not
        /// track generations reports zero. A failed resolution names its reason, which callers pass on as the
        /// runner's refusal, so a revoked runner hears <c>runner_enrollment_revoked</c> rather than a generic denial.
        /// </summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolved owner and generation, or the named failure.</returns>
        Task<HarborRunnerOwnerResolution> ResolveOwnerAsync(string runnerId, CancellationToken token = default);
    }
}
