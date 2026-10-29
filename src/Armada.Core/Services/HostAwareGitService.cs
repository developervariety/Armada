namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services.Interfaces;

    /// <summary>Routes opaque Harbor checkout addresses to Harbor while preserving local Git behavior.</summary>
    public sealed partial class HostAwareGitService : IGitService, IBranchInventory, IDockWorkSnapshotApplier
    {
        private readonly IGitService _Local;
        private readonly IBranchInventory? _LocalInventory;
        private readonly IHarborCheckoutOperations _Harbor;

        /// <summary>Creates a Git router for local paths and opt-in Harbor checkout addresses.</summary>
        /// <param name="local">The existing local Git service.</param>
        /// <param name="harbor">The authenticated Harbor operation facade.</param>
        /// <param name="localInventory">The local branch inventory implementation, when available.</param>
        public HostAwareGitService(IGitService local, IHarborCheckoutOperations harbor, IBranchInventory? localInventory = null)
        {
            _Local = local ?? throw new ArgumentNullException(nameof(local));
            _Harbor = harbor ?? throw new ArgumentNullException(nameof(harbor));
            _LocalInventory = localInventory ?? local as IBranchInventory;
        }

        private bool IsRemote(string path)
        {
            if (HarborCheckoutAddress.IsHarbor(path))
            {
                if (!HarborCheckoutAddress.TryParse(path, out HarborCheckoutAddress? parsed) || parsed == null)
                    throw InvalidAddress();
                return true;
            }
            if (path.StartsWith("harbor:", StringComparison.OrdinalIgnoreCase)) throw InvalidAddress();
            return false;
        }

        private static InvalidOperationException InvalidAddress()
        {
            return new InvalidOperationException("harbor_checkout_address_invalid");
        }

        private static void RequireRepositoryAddress(string address)
        {
            if (!HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed) || parsed == null
                || !parsed.UseRepository || !String.IsNullOrEmpty(parsed.RelativePath))
                throw new InvalidOperationException("harbor_repository_address_required");
        }

        private static bool SameRemoteScope(string first, string second)
        {
            if (!HarborCheckoutAddress.TryParse(first, out HarborCheckoutAddress? firstAddress) || firstAddress == null
                || !HarborCheckoutAddress.TryParse(second, out HarborCheckoutAddress? secondAddress) || secondAddress == null)
                throw InvalidAddress();
            if (!String.IsNullOrEmpty(firstAddress.RelativePath) || !String.IsNullOrEmpty(secondAddress.RelativePath))
                throw InvalidAddress();
            if (!String.Equals(firstAddress.DockId, secondAddress.DockId, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_checkout_scope_mismatch");
            return true;
        }

        private async Task<HarborGitOperationResult> SendAsync(string address, HarborGitOperationRequest request, CancellationToken token)
        {
            return await _Harbor.ExecuteGitAsync(address, request, token).ConfigureAwait(false);
        }

        private HarborGitOperationRequest NewRequest(HarborGitOperationEnum operation, string address)
        {
            if (!HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed) || parsed == null)
                throw InvalidAddress();
            if (!String.IsNullOrEmpty(parsed.RelativePath)) throw InvalidAddress();
            HarborGitOperationRequest request = new HarborGitOperationRequest();
            request.Operation = operation;
            request.UseRepository = parsed.UseRepository;
            request.UseWorkingDirectory = parsed.UseWorkingDirectory;
            return request;
        }

        private async Task SendVoidAsync(string address, HarborGitOperationRequest request, CancellationToken token)
        {
            await SendAsync(address, request, token).ConfigureAwait(false);
        }

        private async Task<T> SendValueAsync<T>(string address, HarborGitOperationRequest request, CancellationToken token, Func<HarborGitOperationResult, T> select)
        {
            HarborGitOperationResult result = await SendAsync(address, request, token).ConfigureAwait(false);
            return select(result);
        }

        private static string RequireText(HarborGitOperationResult result, string field)
        {
            return result.Text ?? throw MissingResult(field);
        }

        private static InvalidOperationException MissingResult(string field)
        {
            return new InvalidOperationException("harbor_git_result_missing_" + field);
        }

        private IBranchInventory LocalInventory()
        {
            return _LocalInventory ?? throw new NotSupportedException("The configured local Git service has no branch inventory.");
        }

        private void SetTarget(HarborGitOperationRequest request, string targetAddress, string sourceAddress)
        {
            if (!IsRemote(targetAddress) || !IsRemote(sourceAddress)) throw new InvalidOperationException("harbor_checkout_scope_mismatch");
            SameRemoteScope(sourceAddress, targetAddress);
            if (!HarborCheckoutAddress.TryParse(targetAddress, out HarborCheckoutAddress? target) || target == null
                || target.UseRepository || !String.IsNullOrEmpty(target.RelativePath) || String.IsNullOrWhiteSpace(target.CheckoutId))
                throw new InvalidOperationException("harbor_target_checkout_required");
            request.TargetCheckoutId = target.CheckoutId;
        }
    }
}
