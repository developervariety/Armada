namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Executes a native database utility without exposing secrets in command arguments or logs.
    /// </summary>
    public interface ISelfDeployNativeCommandRunner
    {
        /// <summary>Execute an injected command request.</summary>
        /// <param name="request">Command request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Process result.</returns>
        Task<SelfDeployNativeCommandResult> RunAsync(SelfDeployNativeCommandRequest request, CancellationToken token = default);
    }
}
