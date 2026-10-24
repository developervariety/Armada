namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>Resolves opaque checkout operations against their durable authenticated owner.</summary>
    public sealed partial class HarborCheckoutService : IHarborCheckoutOperations
    {
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private IHarborWorkspaceOperations? _Channel;
        private IDockService? _DockLifecycle;

        /// <summary>Instantiate the owner-bound checkout provider.</summary>
        public HarborCheckoutService(DatabaseDriver database, ArmadaSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Attach the authenticated server channel during server initialization.</summary>
        public void SetChannel(IHarborWorkspaceOperations channel)
        {
            if (_Channel != null) throw new InvalidOperationException("harbor_workspace_channel_already_set");
            _Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        /// <summary>Attach the durable dock lifecycle used to release completed auxiliary checkouts.</summary>
        /// <param name="dockLifecycle">The server dock lifecycle service.</param>
        public void SetDockLifecycle(IDockService dockLifecycle)
        {
            if (_DockLifecycle != null) throw new InvalidOperationException("harbor_dock_lifecycle_already_set");
            _DockLifecycle = dockLifecycle ?? throw new ArgumentNullException(nameof(dockLifecycle));
        }

        /// <inheritdoc />
        public async Task<HarborGitOperationResult> ExecuteGitAsync(string address, HarborGitOperationRequest request, CancellationToken token = default)
        {
            if (!HarborCheckoutAddress.TryParse(address, out HarborCheckoutAddress? parsed) || parsed == null
                || parsed.RelativePath.Length != 0) throw new InvalidOperationException("harbor_checkout_address_invalid");
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (parsed.CheckoutId != null) throw new InvalidOperationException("harbor_auxiliary_checkout_binding_required");
            Dock dock = await ReadDockAsync(parsed.DockId, token).ConfigureAwait(false);
            request.UseRepository = parsed.UseRepository;
            request.UseWorkingDirectory = parsed.UseWorkingDirectory;
            HarborWorkspaceOperationRequest operation = CreateRequest(dock, HarborWorkspaceOperationEnum.Git);
            operation.Git = request;
            HarborWorkspaceOperationResult result = await ExecuteAsync(dock, operation, token).ConfigureAwait(false);
            return result.Git ?? throw new InvalidOperationException("harbor_git_result_missing");
        }

        /// <summary>Execute a typed operation with the exact persisted dock identity.</summary>
        public async Task<HarborWorkspaceOperationResult> ExecuteAsync(Dock dock, HarborWorkspaceOperationRequest request, CancellationToken token = default)
        {
            HarborCheckoutBinding binding = RequireBinding(dock);
            if (!_Settings.Harbor.Enabled) throw new InvalidOperationException("harbor_checkout_disabled");
            IHarborWorkspaceOperations channel = _Channel ?? throw new InvalidOperationException("harbor_workspace_unavailable");
            UserMaster? user = await _Database.Users.ReadAsync(binding.TenantId, binding.UserId, token).ConfigureAwait(false);
            if (user == null || !user.Active) throw new InvalidOperationException("harbor_checkout_owner_unavailable");
            if (HarborWorkspaceOperationRules.IsMutation(request)) ApplyMutationScope(request, binding);
            AuthContext owner = AuthContext.Authenticated(binding.TenantId, binding.UserId, false, false, "HarborCheckout");
            HarborWorkspaceOperationResult result = await channel.ExecuteAsync(owner, request, token).ConfigureAwait(false);
            if (!result.Accepted || (result.Truncated && request.Operation != HarborWorkspaceOperationEnum.RunVerification))
                throw new InvalidOperationException(result.Reason ?? "harbor_workspace_result_incomplete");
            return result;
        }

        private async Task<Dock> ReadDockAsync(string dockId, CancellationToken token)
        {
            Dock? dock = await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null) throw new InvalidOperationException("harbor_checkout_dock_missing");
            RequireBinding(dock);
            return dock;
        }

        private static HarborCheckoutBinding RequireBinding(Dock dock)
        {
            HarborCheckoutBinding? binding = dock.HostCheckout;
            if (binding == null || !binding.IsComplete
                || !String.Equals(binding.TenantId, dock.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, dock.UserId, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_checkout_binding_invalid");
            return binding;
        }
    }
}
