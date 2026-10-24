namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Reply accepted only for the exact pending runner session and request.</summary>
    public sealed class HarborWorkspaceOperationResult : HarborMessage
    {
        /// <summary>Exact server-issued request identity.</summary>
        public string RequestId { get; set; } = String.Empty;

        /// <summary>Operation answered by this response.</summary>
        public HarborWorkspaceOperationEnum Operation { get; set; }

        /// <summary>Exact checkout identity observed by the owner.</summary>
        public HarborCheckoutBinding? Binding { get; set; }

        /// <summary>Whether the complete operation succeeded.</summary>
        public bool Accepted { get; set; }

        /// <summary>Named refusal or incomplete-result reason.</summary>
        public string? Reason { get; set; }

        /// <summary>Complete bounded text result.</summary>
        public string? Text { get; set; }

        /// <summary>Complete bounded binary file result.</summary>
        public string? BytesBase64 { get; set; }

        /// <summary>Relative paths from a bounded listing.</summary>
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>Verified identity fingerprint of the configured repository.</summary>
        public string? RepositoryFingerprint { get; set; }

        /// <summary>Full observed checkout commit.</summary>
        public string? HeadCommit { get; set; }

        /// <summary>Observed branch, or a detached marker.</summary>
        public string? BranchName { get; set; }

        /// <summary>Verified runner-local launch directory; not a provider-selection input.</summary>
        public string? CanonicalWorkingDirectory { get; set; }

        /// <summary>Whether a relative filesystem entry exists.</summary>
        public bool Exists { get; set; }

        /// <summary>Whether the observed entry is a directory.</summary>
        public bool IsDirectory { get; set; }

        /// <summary>Observed file length in bytes.</summary>
        public long Length { get; set; }

        /// <summary>Observed last-write instant.</summary>
        public DateTime? LastWriteUtc { get; set; }

        /// <summary>Observed configured verification exit code.</summary>
        public int? ExitCode { get; set; }

        /// <summary>Verification exceeded its configured time limit.</summary>
        public bool TimedOut { get; set; }

        /// <summary>Verification stopped because its caller requested cancellation.</summary>
        public bool Cancelled { get; set; }

        /// <summary>The process exit and output drain were both confirmed.</summary>
        public bool KillConfirmed { get; set; }

        /// <summary>Bounded standard output captured from verification.</summary>
        public string? StandardOutput { get; set; }

        /// <summary>Bounded standard error captured from verification.</summary>
        public string? StandardError { get; set; }

        /// <summary>Observed verification duration in milliseconds.</summary>
        public long? DurationMilliseconds { get; set; }

        /// <summary>UTC time immediately before the configured verification process starts.</summary>
        public DateTime? StartedUtc { get; set; }

        /// <summary>Opaque retained output identity for a completed verification.</summary>
        public string? VerificationOutputId { get; set; }

        /// <summary>Check identity bound to the retained verification output.</summary>
        public string? VerificationCheckId { get; set; }

        /// <summary>Complete byte length of standard output.</summary>
        public long StandardOutputLength { get; set; }

        /// <summary>Bytes retained in the bounded standard output spool.</summary>
        public long StandardOutputRetainedBytes { get; set; }

        /// <summary>SHA-256 of complete standard output bytes.</summary>
        public string? StandardOutputSha256 { get; set; }

        /// <summary>Complete byte length of standard error.</summary>
        public long StandardErrorLength { get; set; }

        /// <summary>Bytes retained in the bounded standard error spool.</summary>
        public long StandardErrorRetainedBytes { get; set; }

        /// <summary>Bytes omitted from standard output at its configured cap.</summary>
        public long StandardOutputOmittedBytes { get; set; }

        /// <summary>Bytes omitted from standard error at its configured cap.</summary>
        public long StandardErrorOmittedBytes { get; set; }

        /// <summary>SHA-256 of complete standard error bytes.</summary>
        public string? StandardErrorSha256 { get; set; }

        /// <summary>Whether output could not be returned completely.</summary>
        public bool Truncated { get; set; }

        /// <summary>Typed Git service result.</summary>
        public HarborGitOperationResult? Git { get; set; }

    }
}
