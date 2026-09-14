namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    /// <summary>Immutable view of a Harbor job.</summary>
    public sealed class HarborJobSnapshot
    {
        /// <summary>Server-issued job identifier.</summary>
        public string JobId { get; }

        /// <summary>Runner the job is bound to. A job never moves to another runner.</summary>
        public string RunnerId { get; }

        /// <summary>Caller-supplied launch key used to reject duplicate launches.</summary>
        public string LaunchKey { get; }

        /// <summary>Tenant of the caller that launched the job.</summary>
        public string RequestedByTenantId { get; }

        /// <summary>User of the caller that launched the job.</summary>
        public string RequestedByUserId { get; }

        /// <summary>Durable enrollment generation that authorized the job.</summary>
        public long EnrollmentGeneration { get; }

        /// <summary>Connection generation currently allowed to report for the job.</summary>
        public long SessionGeneration { get; }

        /// <summary>Lifecycle state.</summary>
        public HarborJobStateEnum State { get; }

        /// <summary>Host process identifier reported by the owning runner.</summary>
        public int? ProcessId { get; }

        /// <summary>Exit code reported by the owning runner.</summary>
        public int? ExitCode { get; }

        /// <summary>Stable reason for a failed or lost job.</summary>
        public string? FailureReason { get; }

        /// <summary>Accepted output chunks in sequence order.</summary>
        public IReadOnlyList<HarborOutput> Output { get; }

        internal HarborJobSnapshot(
            string jobId,
            string runnerId,
            string launchKey,
            string requestedByTenantId,
            string requestedByUserId,
            long enrollmentGeneration,
            long sessionGeneration,
            HarborJobStateEnum state,
            int? processId,
            int? exitCode,
            string? failureReason,
            IReadOnlyList<HarborOutput> output)
        {
            JobId = jobId;
            RunnerId = runnerId;
            LaunchKey = launchKey;
            RequestedByTenantId = requestedByTenantId;
            RequestedByUserId = requestedByUserId;
            EnrollmentGeneration = enrollmentGeneration;
            SessionGeneration = sessionGeneration;
            State = state;
            ProcessId = processId;
            ExitCode = exitCode;
            FailureReason = failureReason;
            Output = output;
        }
    }

    /// <summary>Outcome of a Harbor command or runner event.</summary>
    public sealed class HarborCommandResult
    {
        /// <summary>Whether the command or event was accepted.</summary>
        public bool Accepted { get; }

        /// <summary>Stable rejection reason, or empty when accepted.</summary>
        public string Reason { get; }

        private HarborCommandResult(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        /// <summary>Accepted result.</summary>
        public static HarborCommandResult Accept() => new HarborCommandResult(true, String.Empty);

        /// <summary>Rejected result with a stable reason.</summary>
        /// <param name="reason">Stable reason.</param>
        public static HarborCommandResult Reject(string reason) => new HarborCommandResult(false, reason);
    }

    /// <summary>Outcome of a Harbor launch.</summary>
    public sealed class HarborLaunchResult
    {
        /// <summary>Whether the launch was sent to the runner.</summary>
        public bool Accepted { get; }

        /// <summary>Stable rejection reason, or empty when accepted.</summary>
        public string Reason { get; }

        /// <summary>Server-issued job identifier when accepted.</summary>
        public string? JobId { get; }

        /// <summary>Completes with the terminal job state when accepted.</summary>
        public Task<HarborJobSnapshot>? Completion { get; }

        private HarborLaunchResult(bool accepted, string reason, string? jobId, Task<HarborJobSnapshot>? completion)
        {
            Accepted = accepted;
            Reason = reason;
            JobId = jobId;
            Completion = completion;
        }

        internal static HarborLaunchResult Accept(string jobId, Task<HarborJobSnapshot> completion) => new HarborLaunchResult(true, String.Empty, jobId, completion);

        internal static HarborLaunchResult Reject(string reason) => new HarborLaunchResult(false, reason, null, null);
    }

    /// <summary>Outcome of applying a heartbeat's live job list.</summary>
    public sealed class HarborHeartbeatResult
    {
        /// <summary>Jobs rebound to the reporting connection generation.</summary>
        public int Rebound { get; }

        /// <summary>Reported job identifiers that were refused, with the reason for each.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Rejected { get; }

        internal HarborHeartbeatResult(int rebound, IReadOnlyList<KeyValuePair<string, string>> rejected)
        {
            Rebound = rebound;
            Rejected = rejected;
        }
    }
}
