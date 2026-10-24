namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text;
    using Armada.Core.Harbor;

    /// <summary>Validates retained failures before building a test rerun.</summary>
    internal sealed partial class HarborVerificationOutputJournal
    {
        /// <summary>Validates and returns complete output from an exact failed verification for rerun planning.</summary>
        /// <param name="request">Authenticated request for the retained checkout.</param>
        /// <param name="outputId">Opaque identifier of the retained verification output.</param>
        /// <param name="checkId">Expected check identity that owns the output.</param>
        /// <param name="exitCode">Receives the verified nonzero exit code.</param>
        /// <returns>The verified stdout and stderr text used by the existing rerun planner.</returns>
        public string ReadForRerun(HarborWorkspaceOperationRequest request, string outputId, string checkId, out int exitCode)
        {
            HarborVerificationOutputRecord record = ReadRecord(outputId);
            if (record.CheckId != checkId || record.RunnerId != request.RunnerId || record.TenantId != request.TenantId
                || record.UserId != request.UserId || record.DockId != request.DockId || !SameBinding(record.Binding, request.Binding))
                throw Failure("workspace_verification_output_owner_mismatch", "The prior output does not belong to this authenticated checkout.");
            if (record.Truncated)
                throw Failure("workspace_verification_rerun_output_incomplete", "The prior output was truncated and cannot prove the complete failure set.");
            if (!record.ExitCode.HasValue || record.ExitCode.Value == 0)
                throw Failure("workspace_verification_rerun_source_not_failed", "The referenced verification did not record a failed exit.");
            string stdoutPath = StreamPath(outputId, false);
            string stderrPath = StreamPath(outputId, true);
            HarborPrivateStorage.ValidatePrivateFile(stdoutPath);
            HarborPrivateStorage.ValidatePrivateFile(stderrPath);
            byte[] stdout = ReadCompleteStream(stdoutPath, record.StandardOutputRetainedBytes);
            byte[] stderr = ReadCompleteStream(stderrPath, record.StandardErrorRetainedBytes);
            if (stdout.LongLength != record.StandardOutputRetainedBytes || stderr.LongLength != record.StandardErrorRetainedBytes)
                throw Failure("workspace_verification_output_corrupt", "The prior output length does not match its metadata.");
            if (!String.Equals(Hash(stdout), record.StandardOutputSha256, StringComparison.Ordinal)
                || !String.Equals(Hash(stderr), record.StandardErrorSha256, StringComparison.Ordinal))
                throw Failure("workspace_verification_output_corrupt", "The prior output digest does not match its metadata.");
            exitCode = record.ExitCode.Value;
            return Encoding.UTF8.GetString(stdout) + Environment.NewLine + Encoding.UTF8.GetString(stderr);
        }

        private static byte[] ReadCompleteStream(string path, long expectedLength)
        {
            if (expectedLength < 0 || expectedLength > MaximumOutputBytesPerStream + MaximumRenderedMarkerBytes)
                throw Failure("workspace_verification_output_corrupt", "The prior output exceeds its bounded stream limit.");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != expectedLength) throw Failure("workspace_verification_output_corrupt", "The prior output length does not match its metadata.");
                byte[] bytes = new byte[checked((int)expectedLength)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw Failure("workspace_verification_output_corrupt", "The prior output is truncated.");
                    offset += read;
                }
                return bytes;
            }
        }
    }
}
