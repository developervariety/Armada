namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Authenticated owner channel for typed operations on Harbor repository grants.</summary>
    public interface IHarborWorkspaceOperations
    {
        /// <summary>Execute on the exact authenticated owner without another-host fallback.</summary>
        /// <param name="auth">Verified owner context supplied by the calling service.</param>
        /// <param name="request">Bounded typed request checked against durable ownership.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The complete typed result or its named refusal.</returns>
        Task<HarborWorkspaceOperationResult> ExecuteAsync(AuthContext auth, HarborWorkspaceOperationRequest request, CancellationToken token = default);
    }
}
