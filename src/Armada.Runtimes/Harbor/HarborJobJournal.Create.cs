namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Local job journal create operations.</summary>
    public sealed partial class HarborJobJournal
    {
        /// <summary>Create a non-secret pending record before a child process can start.</summary>
        /// <param name="request">Launch request whose identity fields are retained.</param>
        /// <returns>The durable pending record.</returns>
        public HarborLocalJobRecord CreateIntent(HarborLaunchRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ValidateJobId(request.JobId);
            ValidateLaunchKey(request.LaunchKey);
            if ((request.CheckoutBinding == null) != String.IsNullOrWhiteSpace(request.DockId)
                || request.CheckoutBinding != null && !request.CheckoutBinding.IsComplete)
                throw Failure("checkout_launch_binding_incomplete", "Checkout launch identity must include one complete dock binding.");
            lock (_Sync)
            {
                using (FileStream admission = AcquireAdmissionLock())
                {
                    EnsureStoragePolicyPinned();
                    List<HarborLocalJobRecord> existing = Enumerate();
                    if (existing.Any(record => String.Equals(record.JobId, request.JobId, StringComparison.Ordinal)))
                        throw Failure("job_already_exists", "A job with this identifier is already retained.");
                    // A child starts only after the staging directory is atomically published; the admission lock makes stale unpublished stages safe to inspect and remove.
                    CleanupUnpublishedStagingDirectories();
                    if (existing.Count >= _Options.MaxRetainedJobs)
                        throw Failure("job_capacity_exhausted", "The retained job limit has been reached.");
                    DateTime supervisorStartedUtc;
                    using (Process supervisor = Process.GetCurrentProcess())
                        supervisorStartedUtc = NormalizeUtc(supervisor.StartTime.ToUniversalTime());
                    HarborLinuxProcessIdentity? supervisorIdentity = OperatingSystem.IsLinux()
                        ? HarborLinuxProcessIdentity.Read(Environment.ProcessId)
                        : null;
                    HarborLocalJobRecord record = new HarborLocalJobRecord
                    {
                        JobId = request.JobId,
                        LaunchKey = request.LaunchKey,
                        DockId = request.DockId,
                        CheckoutId = request.CheckoutBinding?.CheckoutId,
                        CheckoutRevision = request.CheckoutBinding?.Revision,
                        SupervisorProcessId = Environment.ProcessId,
                        SupervisorStartedUtc = supervisorStartedUtc,
                        SupervisorLinuxStartTicks = supervisorIdentity?.StartTicks,
                        SupervisorLinuxBootId = supervisorIdentity?.BootId,
                        FirstOutputSequence = 0,
                        NextOutputSequence = 0
                    };
                    string stagingDirectory = Path.Combine(_Root, ".pending-" + Guid.NewGuid().ToString("N"));
                    HarborPrivateStorage.EnsurePrivateDirectory(stagingDirectory);
                    EnsureNoLink(stagingDirectory);
                    using (FileStream spool = new FileStream(Path.Combine(stagingDirectory, "output.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        spool.Flush(true);
                    HarborPrivateStorage.EnsurePrivateFile(Path.Combine(stagingDirectory, "output.jsonl"));
                    string stagedRecord = Path.Combine(stagingDirectory, "job.json");
                    AtomicWrite(stagedRecord, JsonSerializer.Serialize(record, _JsonOptions));
                    Directory.Move(stagingDirectory, JobDirectory(record.JobId));
                    return record;
                }
            }
        }
    }
}
