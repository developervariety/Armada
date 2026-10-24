namespace Armada.Server.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Verifies that a caller may use a runner grant or a durable Harbor checkout.</summary>
    public interface IHarborWorkspaceBindingVerifier
    {
        /// <summary>Return null only when the caller and the durable records authorize this operation.</summary>
        /// <param name="auth">Verified API or mission caller.</param>
        /// <param name="session">Current authenticated runner lease.</param>
        /// <param name="request">Typed operation request with server-assigned runner identity.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null when allowed, otherwise a stable refusal reason.</returns>
        Task<string?> ValidateAsync(AuthContext auth, HarborRunnerSession session, HarborWorkspaceOperationRequest request, CancellationToken token);
    }
}
