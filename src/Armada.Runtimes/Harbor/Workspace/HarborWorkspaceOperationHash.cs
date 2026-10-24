namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Security.Cryptography;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Hashes typed mutation content independently of connection-attempt metadata.</summary>
    internal static class HarborWorkspaceOperationHash
    {
        /// <summary>Computes the semantic hash used to reject operation-key reuse with different content.</summary>
        /// <param name="request">Typed workspace operation to hash.</param>
        /// <returns>Lowercase hexadecimal SHA-256 digest of the semantic request.</returns>
        public static string Compute(HarborWorkspaceOperationRequest request)
        {
            HarborWorkspaceOperationRequest semantic = JsonSerializer.Deserialize<HarborWorkspaceOperationRequest>(
                JsonSerializer.SerializeToUtf8Bytes(request))
                ?? throw new InvalidOperationException("workspace_operation_hash_invalid");
            semantic.RequestId = String.Empty;
            semantic.OperationKey = null;
            semantic.CorrelationId = null;
            semantic.TraceParent = null;
            semantic.ConnectionGeneration = 0;
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(semantic))).ToLowerInvariant();
        }
    }
}
