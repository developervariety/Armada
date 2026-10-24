namespace Armada.Runtimes.Harbor
{
    using System;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Private ownership and integrity metadata for completed verification output.</summary>
    internal sealed class HarborVerificationOutputRecord
    {
        /// <summary>Opaque identifier for the retained verification output.</summary>
        public string OutputId { get; set; } = String.Empty;
        /// <summary>Stable operation key that produced this output.</summary>
        public string OperationKey { get; set; } = String.Empty;
        /// <summary>Check identity supplied by the owning service.</summary>
        public string CheckId { get; set; } = String.Empty;
        /// <summary>Observed child exit code, when the process supplied one.</summary>
        public int? ExitCode { get; set; }
        /// <summary>Runner that executed the verification.</summary>
        public string RunnerId { get; set; } = String.Empty;
        /// <summary>Tenant that owns the verification.</summary>
        public string TenantId { get; set; } = String.Empty;
        /// <summary>User that owns the verification.</summary>
        public string UserId { get; set; } = String.Empty;
        /// <summary>Durable dock that authorized the verification.</summary>
        public string DockId { get; set; } = String.Empty;
        /// <summary>Immutable checkout identity that authorized the verification.</summary>
        public HarborCheckoutBinding Binding { get; set; } = new HarborCheckoutBinding();
        /// <summary>Total stdout bytes observed before truncation policy.</summary>
        public long StandardOutputLength { get; set; }
        /// <summary>Stdout bytes retained in the spool.</summary>
        public long StandardOutputRetainedBytes { get; set; }
        /// <summary>Stdout bytes omitted from the spool.</summary>
        public long StandardOutputOmittedBytes { get; set; }
        /// <summary>Whether either stream exceeded its retained byte limit.</summary>
        public bool Truncated { get; set; }
        /// <summary>SHA-256 hash of the retained stdout bytes.</summary>
        public string StandardOutputSha256 { get; set; } = String.Empty;
        /// <summary>Total stderr bytes observed before truncation policy.</summary>
        public long StandardErrorLength { get; set; }
        /// <summary>Stderr bytes retained in the spool.</summary>
        public long StandardErrorRetainedBytes { get; set; }
        /// <summary>Stderr bytes omitted from the spool.</summary>
        public long StandardErrorOmittedBytes { get; set; }
        /// <summary>SHA-256 hash of the retained stderr bytes.</summary>
        public string StandardErrorSha256 { get; set; } = String.Empty;
        /// <summary>UTC time when the durable output record was written.</summary>
        public DateTime CreatedUtc { get; set; }
        /// <summary>UTC time when the verification process started.</summary>
        public DateTime StartedUtc { get; set; }
    }
}
