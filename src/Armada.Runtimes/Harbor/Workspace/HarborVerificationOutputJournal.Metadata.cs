namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Validates private verification output metadata.</summary>
    internal sealed partial class HarborVerificationOutputJournal
    {
        private HarborVerificationOutputRecord ReadRecord(string outputId)
        {
            if (outputId.Length != 68 || !outputId.StartsWith("hvo_", StringComparison.Ordinal)
                || outputId.Skip(4).Any(character => !Uri.IsHexDigit(character)))
                throw Failure("workspace_verification_output_id_invalid", "The output identity is invalid.");
            string path = MetadataPath(outputId);
            HarborPrivateStorage.ValidatePrivateFile(path);
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 64 * 1024) throw Failure("workspace_verification_output_metadata_invalid", "Output metadata exceeds its size limit.");
                bytes = new byte[checked((int)stream.Length)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw Failure("workspace_verification_output_metadata_invalid", "Output metadata is truncated.");
                    offset += count;
                }
            }
            HarborVerificationOutputRecord? record = JsonSerializer.Deserialize<HarborVerificationOutputRecord>(bytes);
            if (record == null || record.OutputId != outputId || String.IsNullOrWhiteSpace(record.OperationKey)
                || record.StandardOutputLength < 0 || record.StandardErrorLength < 0
                || record.StandardOutputRetainedBytes < 0 || record.StandardOutputRetainedBytes > MaximumOutputBytesPerStream + MaximumRenderedMarkerBytes
                || record.StandardErrorRetainedBytes < 0 || record.StandardErrorRetainedBytes > MaximumOutputBytesPerStream + MaximumRenderedMarkerBytes
                || record.StandardOutputOmittedBytes < 0 || record.StandardErrorOmittedBytes < 0)
                throw Failure("workspace_verification_output_metadata_invalid", "Output metadata is incomplete.");
            return record;
        }
    }
}
