namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Executes fixed Git operations on a Harbor-owned checkout.</summary>
    public interface IHarborCheckoutOperations
    {
        /// <summary>Executes one typed Git operation against an opaque dock address.</summary>
        /// <param name="address">Opaque Harbor dock, checkout, repository, or working-directory address.</param>
        /// <param name="request">Fixed operation and structured arguments.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The typed operation result.</returns>
        Task<HarborGitOperationResult> ExecuteGitAsync(string address, HarborGitOperationRequest request, CancellationToken token = default);
    }
}
