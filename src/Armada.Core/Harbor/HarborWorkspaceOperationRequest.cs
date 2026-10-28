namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Authenticated typed operation on a repository grant or pinned checkout.</summary>
    public sealed class HarborWorkspaceOperationRequest : HarborMessage
    {
        /// <summary>Server-issued operation identity retained for mutation replay checks.</summary>
        public string RequestId { get; set; } = String.Empty;

        /// <summary>Stable durable mutation identity; retries retain this key across connection attempts.</summary>
        public string? OperationKey { get; set; }

        /// <summary>Workspace contract version.</summary>
        public int Version { get; set; } = HarborWorkspaceProtocol.Version;

        /// <summary>Typed operation to execute.</summary>
        public HarborWorkspaceOperationEnum Operation { get; set; }

        /// <summary>Persisted checkout identity; absent only for repository inspection.</summary>
        public HarborCheckoutBinding? Binding { get; set; }

        /// <summary>Durable dock authorizing a bound operation.</summary>
        public string DockId { get; set; } = String.Empty;

        /// <summary>Durable vessel for the configured repository grant.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>Runner-local repository grant identifier.</summary>
        public string RepositoryGrantId { get; set; } = String.Empty;

        /// <summary>Verified owner tenant repeated from the authenticated connection.</summary>
        public string TenantId { get; set; } = String.Empty;

        /// <summary>Verified owner user repeated from the authenticated connection.</summary>
        public string UserId { get; set; } = String.Empty;

        /// <summary>Exact runner that owns this request.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary>Pinned durable enrollment generation.</summary>
        public long EnrollmentGeneration { get; set; }

        /// <summary>Current authenticated connection generation.</summary>
        public long ConnectionGeneration { get; set; }

        /// <summary>Full expected commit before an operation that changes the checkout.</summary>
        public string? ExpectedHead { get; set; }

        /// <summary>Relative path beneath the selected checkout; never a host root.</summary>
        public string? RelativePath { get; set; }

        /// <summary>Full repository identity fingerprint expected from the configured grant.</summary>
        public string? ExpectedRepositoryFingerprint { get; set; }

        /// <summary>Structured repository revision to resolve before checkout provisioning.</summary>
        public string? RequestedRevision { get; set; }

        /// <summary>Bounded UTF-8 text for a file write.</summary>
        public string? Text { get; set; }

        /// <summary>Bounded binary file bytes encoded for transport.</summary>
        public string? BytesBase64 { get; set; }

        /// <summary>Maximum bytes of file or operation output accepted by the caller.</summary>
        public int MaxBytes { get; set; } = 1024 * 1024;

        /// <summary>Maximum directory entries accepted by the caller.</summary>
        public int MaxEntries { get; set; } = 1024;

        /// <summary>Bounded operation timeout.</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>Whether an explicit bounded directory listing descends into children.</summary>
        public bool Recursive { get; set; }

        /// <summary>Whether a file write may replace the existing relative file.</summary>
        public bool Overwrite { get; set; }

        /// <summary>Mark a staged file read-only after its complete write.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Whether a new checkout has detached HEAD at its exact base commit.</summary>
        public bool Detached { get; set; }

        /// <summary>Structured Git service inputs for a Git operation.</summary>
        public HarborGitOperationRequest? Git { get; set; }

        /// <summary>Configured host verification command identity.</summary>
        public HarborVerificationRequest? Verification { get; set; }

        /// <summary>Bounded read request for a completed verification output spool.</summary>
        public HarborVerificationOutputRequest? VerificationOutput { get; set; }

        /// <summary>
        /// Allowlisted Codex inventory command. Only a Codex inventory probe may set it.
        /// </summary>
        public string? InventoryCommand { get; set; }

    }
}
