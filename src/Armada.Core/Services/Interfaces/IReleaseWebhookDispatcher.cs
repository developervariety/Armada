namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Single-purpose outbound dispatcher for CD webhook delivery of release evidence.</summary>
    public interface IReleaseWebhookDispatcher
    {
        /// <summary>
        /// POSTs <paramref name="payload"/> to the configured CD endpoint and returns the categorized outcome.
        /// Transport failures are returned in the result, never thrown.
        /// </summary>
        Task<WebhookDispatchResult> DispatchAsync(ReleaseWebhookPayload payload, CancellationToken token = default);
    }
}
