namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Durable job metadata and exact process identity operations.</summary>
    public sealed partial class HarborJobJournal
    {
        private HarborLocalJobRecord ReadRecord(string jobId)
        {
            ValidateJobId(jobId);
            lock (_Sync)
            {
                string directory = JobDirectory(jobId);
                if (!Directory.Exists(directory)) throw Failure("job_not_found", "The requested job is not retained.");
                EnsureNoLink(directory);
                HarborPrivateStorage.EnsurePrivateDirectory(directory);
                string path = RecordPath(jobId);
                HarborLocalJobRecord? record;
                try { record = JsonSerializer.Deserialize<HarborLocalJobRecord>(ReadBoundedTextFile(path, "job_metadata_too_large", "job_metadata_corrupt"), _JsonOptions); }
                catch (Exception exception) when (exception is JsonException || exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw Failure("job_metadata_corrupt", "The retained job metadata cannot be read.", exception);
                }
                if (record == null || !String.Equals(record.JobId, jobId, StringComparison.Ordinal)
                    || record.NextOutputSequence < record.FirstOutputSequence || record.FirstOutputSequence < 0
                    || record.SupervisorProcessId <= 0 || record.SupervisorStartedUtc.Kind != DateTimeKind.Utc
                    || record.IsRunning && (record.StopConfirmed || (!record.ProcessId.HasValue || !record.ProcessStartedUtc.HasValue) && (!record.OutputGap || String.IsNullOrWhiteSpace(record.FailureReason)))
                    || record.ProcessStartedUtc.HasValue && !record.ProcessId.HasValue
                    || record.ProcessId.HasValue && record.ProcessId.Value <= 0
                    || record.ProcessStartedUtc.HasValue && record.ProcessStartedUtc.Value.Kind != DateTimeKind.Utc
                    || !HasValidLinuxIdentity(record.SupervisorLinuxStartTicks, record.SupervisorLinuxBootId)
                    || !HasValidLinuxIdentity(record.ProcessLinuxStartTicks, record.ProcessLinuxBootId)
                    || OperatingSystem.IsLinux() && (!record.SupervisorLinuxStartTicks.HasValue || String.IsNullOrWhiteSpace(record.SupervisorLinuxBootId)
                        || record.ProcessId.HasValue && (!record.ProcessLinuxStartTicks.HasValue || String.IsNullOrWhiteSpace(record.ProcessLinuxBootId))
                            && (!record.OutputGap || String.IsNullOrWhiteSpace(record.FailureReason))))
                    throw Failure("job_metadata_invalid", "The retained job metadata failed validation.");
                ValidateLaunchKey(record.LaunchKey);
                return record;
            }
        }

        internal void PersistRecord(HarborLocalJobRecord record)
        {
            ValidateRecord(record);
            lock (_Sync) AtomicWrite(RecordPath(record.JobId), JsonSerializer.Serialize(record, _JsonOptions));
        }

        internal void MarkOutputGap(HarborLocalJobRecord record, string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw Failure("output_gap_reason_missing", "An output gap requires a reason.");
            lock (_Sync)
            {
                record.OutputGap = true;
                if (String.IsNullOrWhiteSpace(record.FailureReason)) record.FailureReason = reason;
                PersistRecord(record);
            }
        }

        internal bool TryReadStopRequest(string jobId, string launchKey, out int gracePeriodMs)
        {
            gracePeriodMs = 0;
            string path = StopPath(jobId);
            if (!File.Exists(path)) return false;
            EnsureNoLink(path);
            StopRequest? request;
            try { request = JsonSerializer.Deserialize<StopRequest>(ReadBoundedTextFile(path, "stop_request_too_large", "stop_request_corrupt"), _JsonOptions); }
            catch (Exception exception) when (exception is JsonException || exception is IOException || exception is UnauthorizedAccessException)
            {
                throw Failure("stop_request_corrupt", "The stop request cannot be read.", exception);
            }
            if (request == null || request.GracePeriodMs < 0 || request.GracePeriodMs > 300000
                || !String.Equals(request.JobId, jobId, StringComparison.Ordinal) || !String.Equals(request.LaunchKey, launchKey, StringComparison.Ordinal))
                throw Failure("stop_identity_mismatch", "The stop request does not match the retained launch.");
            HarborLocalJobRecord record = ReadRecord(jobId);
            if (request.ProcessId != record.ProcessId || request.ProcessStartedUtc != record.ProcessStartedUtc
                || request.ProcessLinuxStartTicks != record.ProcessLinuxStartTicks
                || !String.Equals(request.ProcessLinuxBootId, record.ProcessLinuxBootId, StringComparison.Ordinal))
                throw Failure("stop_process_mismatch", "The stop request does not match the recorded child process.");
            gracePeriodMs = request.GracePeriodMs;
            return true;
        }

    }
}
