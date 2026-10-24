namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Harbor;

    /// <summary>Durable replay fence for mutations sent more than once across runner connections.</summary>
    internal sealed partial class HarborWorkspaceOperationJournal
    {
        private const int MaximumRecords = 256;
        private const int MaximumRecordBytes = 4 * 1024 * 1024;
        private readonly string _Root;

        /// <summary>Creates the private bounded operation journal under the runner state directory.</summary>
        /// <param name="stateDirectory">Private runner state root.</param>
        public HarborWorkspaceOperationJournal(string stateDirectory)
        {
            string root = Path.Combine(Path.GetFullPath(stateDirectory), "workspace-operations");
            HarborPrivateStorage.EnsurePrivateDirectory(root);
            _Root = root;
        }

        /// <summary>Reserves a mutation key or returns its completed result for safe replay.</summary>
        /// <param name="request">Typed mutation with stable operation identity.</param>
        /// <param name="replay">Completed prior result when the operation was already recorded.</param>
        /// <returns>Exclusive operation record lease. Dispose it after completion or replay.</returns>
        public FileStream Begin(HarborWorkspaceOperationRequest request, out HarborWorkspaceOperationResult? replay)
        {
            string key = RequireKey(request.OperationKey);
            string hash = HarborWorkspaceOperationHash.Compute(request);
            FileStream lease = AcquireLease(key);
            try
            {
                string path = RecordPath(key);
                if (File.Exists(path))
                {
                    HarborWorkspaceOperationRecord record = Read(path);
                    if (!String.Equals(record.OperationKey, key, StringComparison.Ordinal)
                        || !String.Equals(record.SemanticHash, hash, StringComparison.Ordinal))
                        throw Failure("workspace_operation_key_conflict", "The operation key was reused for different mutation content.");
                    if (!record.Completed || record.Result == null)
                        throw Failure("workspace_operation_uncertain", "The prior mutation has no confirmed durable result.");
                    replay = CloneForRetry(record.Result, request);
                    return lease;
                }

                if (Directory.GetFiles(_Root, "*.json").Length >= MaximumRecords)
                    throw Failure("workspace_operation_limit_reached", "The runner has reached its retained operation limit.");
                Write(path, new HarborWorkspaceOperationRecord { OperationKey = key, SemanticHash = hash, UpdatedUtc = DateTime.UtcNow });
                replay = null;
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        /// <summary>Marks a dispatched mutation complete with its durable result.</summary>
        /// <param name="request">Original mutation request.</param>
        /// <param name="result">Observed result to retain for exact retries.</param>
        public void Complete(HarborWorkspaceOperationRequest request, HarborWorkspaceOperationResult result)
        {
            string key = RequireKey(request.OperationKey);
            string path = RecordPath(key);
            HarborWorkspaceOperationRecord record = Read(path);
            if (!String.Equals(record.SemanticHash, HarborWorkspaceOperationHash.Compute(request), StringComparison.Ordinal))
                throw Failure("workspace_operation_key_conflict", "The operation key was reused for different mutation content.");
            record.Result = result;
            record.Completed = true;
            record.UpdatedUtc = DateTime.UtcNow;
            Write(path, record);
        }

        private string RecordPath(string key) => Path.Combine(_Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");
        private static string RequireKey(string? value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(Char.IsControl))
                throw Failure("workspace_operation_key_invalid", "A bounded stable operation key is required for mutations.");
            return value;
        }
        private static HarborWorkspaceOperationResult CloneForRetry(HarborWorkspaceOperationResult result, HarborWorkspaceOperationRequest request)
        {
            HarborWorkspaceOperationResult clone = System.Text.Json.JsonSerializer.Deserialize<HarborWorkspaceOperationResult>(System.Text.Json.JsonSerializer.Serialize(result))!;
            clone.RequestId = request.RequestId;
            clone.CorrelationId = request.CorrelationId;
            return clone;
        }
        private static HarborJobJournalException Failure(string reason, string message, Exception? inner = null) => new HarborJobJournalException(reason, message, inner);
    }
}
