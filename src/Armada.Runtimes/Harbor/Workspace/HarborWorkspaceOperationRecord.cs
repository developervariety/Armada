namespace Armada.Runtimes.Harbor
{
    using System;
    using Armada.Core.Harbor;

    /// <summary>Private, non-secret replay record for one typed workspace mutation.</summary>
    internal sealed class HarborWorkspaceOperationRecord
    {
        /// <summary>Stable idempotency key for the mutation.</summary>
        public string OperationKey { get; set; } = String.Empty;
        /// <summary>Hash of the normalized request fields that define mutation semantics.</summary>
        public string SemanticHash { get; set; } = String.Empty;
        /// <summary>UTC time of the latest record write.</summary>
        public DateTime UpdatedUtc { get; set; }
        /// <summary>Whether dispatch completed and a result is safe to replay.</summary>
        public bool Completed { get; set; }
        /// <summary>Result retained after confirmed operation completion.</summary>
        public HarborWorkspaceOperationResult? Result { get; set; }
    }
}
