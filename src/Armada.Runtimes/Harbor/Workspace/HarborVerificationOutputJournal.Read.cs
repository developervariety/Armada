namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using Armada.Core.Harbor;

    /// <summary>Reads authenticated bounded chunks from retained verification streams.</summary>
    internal sealed partial class HarborVerificationOutputJournal
    {
        private const int MaximumChunkBytes = 1024 * 1024;

        /// <summary>Reads one bounded output chunk after validating its owner and checkout binding.</summary>
        /// <param name="request">Authenticated output read request.</param>
        /// <returns>The requested bytes and retained stream metadata.</returns>
        public HarborWorkspaceOperationResult Read(HarborWorkspaceOperationRequest request)
        {
            HarborVerificationOutputRequest? read = request.VerificationOutput;
            if (read == null || read.MaxBytes < 1 || read.MaxBytes > MaximumChunkBytes || read.Offset < 0
                || String.IsNullOrWhiteSpace(read.OutputId) || String.IsNullOrWhiteSpace(read.CheckId))
                throw Failure("workspace_verification_output_request_invalid", "The verification output range is invalid.");
            HarborVerificationOutputRecord record = ReadRecord(read.OutputId);
            if (record.CheckId != read.CheckId || record.RunnerId != request.RunnerId || record.TenantId != request.TenantId
                || record.UserId != request.UserId || record.DockId != request.DockId || !SameBinding(record.Binding, request.Binding))
                throw Failure("workspace_verification_output_owner_mismatch", "The output does not belong to this authenticated checkout.");
            long length = read.StandardError ? record.StandardErrorRetainedBytes : record.StandardOutputRetainedBytes;
            if (read.Offset > length) throw Failure("workspace_verification_output_range_invalid", "The output offset is outside the retained stream.");
            int count = (int)Math.Min(read.MaxBytes, length - read.Offset);
            byte[] bytes = new byte[count];
            string path = StreamPath(record.OutputId, read.StandardError);
            HarborPrivateStorage.ValidatePrivateFile(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != length) throw Failure("workspace_verification_output_corrupt", "The retained output length does not match its metadata.");
                stream.Position = read.Offset;
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int readCount = stream.Read(bytes, offset, bytes.Length - offset);
                    if (readCount == 0) throw Failure("workspace_verification_output_corrupt", "The retained output is truncated.");
                    offset += readCount;
                }
            }
            return new HarborWorkspaceOperationResult
            {
                Accepted = true, BytesBase64 = Convert.ToBase64String(bytes),
                VerificationCheckId = record.CheckId,
                Length = read.StandardError ? record.StandardErrorLength : record.StandardOutputLength,
                StandardOutputRetainedBytes = record.StandardOutputRetainedBytes,
                StandardErrorRetainedBytes = record.StandardErrorRetainedBytes,
                VerificationOutputId = record.OutputId
            };
        }
    }
}
