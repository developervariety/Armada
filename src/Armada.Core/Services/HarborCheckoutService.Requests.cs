namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class HarborCheckoutService
    {
        /// <summary>Create a typed request from a persisted dock binding.</summary>
        public static HarborWorkspaceOperationRequest CreateRequest(Dock dock, HarborWorkspaceOperationEnum operation)
        {
            HarborCheckoutBinding binding = RequireBinding(dock);
            return new HarborWorkspaceOperationRequest
            {
                Operation = operation, Binding = binding, DockId = dock.Id, VesselId = dock.VesselId,
                RepositoryGrantId = binding.RepositoryGrantId, RunnerId = binding.RunnerId,
                TenantId = binding.TenantId, UserId = binding.UserId,
                EnrollmentGeneration = binding.EnrollmentGeneration,
                ExpectedRepositoryFingerprint = binding.RepositoryFingerprint
            };
        }

        private static void ApplyMutationScope(HarborWorkspaceOperationRequest request, HarborCheckoutBinding binding)
        {
            HarborCheckoutOperationScope scope = HarborCheckoutOperationScope.Current
                ?? throw new InvalidOperationException("harbor_operation_scope_required");
            request.ExpectedHead = scope.ExpectedHead;
            request.RequestId = String.Empty;
            request.CorrelationId = null;
            request.TraceParent = null;
            request.OperationKey = null;
            request.ConnectionGeneration = 0;
            string semantic = scope.Identity + "\n" + binding.Revision + "\n" + JsonSerializer.Serialize(request);
            request.OperationKey = "hop_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic))).ToLowerInvariant();
        }
    }
}
