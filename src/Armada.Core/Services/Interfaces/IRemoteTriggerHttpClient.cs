namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Single-purpose HTTP wire wrapper for posting to the Claude Code Routines /fire endpoint.</summary>
    public interface IRemoteTriggerHttpClient
    {
        /// <summary>
        /// Posts to the /fire endpoint described in <paramref name="request"/> and returns the categorized outcome.
        /// </summary>
        Task<FireResult> FireAsync(FireRequest request, CancellationToken token = default);
    }
}
