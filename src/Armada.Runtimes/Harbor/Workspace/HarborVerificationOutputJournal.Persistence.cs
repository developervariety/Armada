namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    internal sealed partial class HarborVerificationOutputJournal
    {
        private static readonly JsonSerializerOptions _Options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

        /// <summary>Flushes both output streams and publishes their metadata before releasing the reservation.</summary>
        /// <param name="request">Owner-bound verification request with its reserved operation key.</param>
        /// <param name="execution">Observed process exit and bounded stream bytes.</param>
        /// <param name="binding">Immutable checkout binding for the output owner.</param>
        /// <returns>Durable result with stream lengths and integrity hashes.</returns>
        public HarborWorkspaceOperationResult Complete(
            HarborWorkspaceOperationRequest request,
            HarborWorkspaceProcessResult execution,
            HarborCheckoutBinding binding)
        {
            string outputId = OutputId(request.OperationKey!);
            using (FileStream lease = AcquireLease())
            {
                string reservation = ReservationPath(outputId);
                if (!File.Exists(reservation)) throw Failure("workspace_verification_reservation_missing", "The output reservation is missing.");
                byte[] stdout = execution.StandardOutputBytes;
                byte[] stderr = execution.StandardErrorBytes;
                if (stdout.Length > 16 * 1024 * 1024 + 512 || stderr.Length > 16 * 1024 * 1024 + 512)
                    throw Failure("workspace_verification_output_limit_exceeded", "Verification output exceeded its per-stream reservation.");
                HarborVerificationOutputRecord record = new HarborVerificationOutputRecord
                {
                    OutputId = outputId, OperationKey = request.OperationKey!, CheckId = request.Verification!.CheckId,
                    ExitCode = execution.ExitCode,
                    RunnerId = request.RunnerId, TenantId = request.TenantId, UserId = request.UserId, DockId = request.DockId,
                    Binding = binding, StandardOutputLength = execution.StandardOutputTotalBytes,
                    StandardOutputRetainedBytes = stdout.Length, StandardOutputOmittedBytes = execution.StandardOutputOmittedBytes,
                    Truncated = execution.Truncated, StandardOutputSha256 = Hash(stdout),
                    StandardErrorLength = execution.StandardErrorTotalBytes, StandardErrorRetainedBytes = stderr.Length,
                    StandardErrorOmittedBytes = execution.StandardErrorOmittedBytes,
                    StandardErrorSha256 = Hash(stderr), CreatedUtc = DateTime.UtcNow, StartedUtc = execution.StartedUtc
                };
                WriteBytes(StreamPath(outputId, false), stdout);
                WriteBytes(StreamPath(outputId, true), stderr);
                WriteBytes(MetadataPath(outputId), JsonSerializer.SerializeToUtf8Bytes(record, _Options));
                File.Delete(reservation);
                return new HarborWorkspaceOperationResult
                {
                    Accepted = true, ExitCode = execution.ExitCode, TimedOut = execution.TimedOut,
                    Cancelled = execution.Cancelled, KillConfirmed = (execution.TimedOut || execution.Cancelled) && execution.StopConfirmed,
                    DurationMilliseconds = execution.DurationMilliseconds, StartedUtc = execution.StartedUtc, Truncated = execution.Truncated,
                    VerificationOutputId = outputId, VerificationCheckId = record.CheckId, StandardOutputLength = record.StandardOutputLength,
                    StandardOutputRetainedBytes = record.StandardOutputRetainedBytes,
                    StandardOutputOmittedBytes = record.StandardOutputOmittedBytes,
                    StandardOutputSha256 = record.StandardOutputSha256, StandardErrorLength = record.StandardErrorLength,
                    StandardErrorRetainedBytes = record.StandardErrorRetainedBytes,
                    StandardErrorOmittedBytes = record.StandardErrorOmittedBytes,
                    StandardErrorSha256 = record.StandardErrorSha256, Reason = execution.Truncated ? "workspace_verification_output_truncated" : null
                };
            }
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static void WriteBytes(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    HarborPrivateStorage.EnsurePrivateFile(temporary);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
