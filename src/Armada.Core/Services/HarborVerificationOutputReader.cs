namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Reads and verifies bounded output chunks from one exact owned checkout.</summary>
    internal static class HarborVerificationOutputReader
    {
        private const int ChunkBytes = 1024 * 1024;

        /// <summary>Reads both retained streams and checks their lengths and hashes against the runner result.</summary>
        /// <param name="checkouts">Authenticated owner-bound checkout service.</param>
        /// <param name="dock">Durable dock that owns the output spool.</param>
        /// <param name="completed">Completed verification metadata from the runner.</param>
        /// <param name="checkId">Expected check identity.</param>
        /// <param name="expectedHead">Pinned checkout revision for the reads.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Decoded stdout and stderr after integrity checks.</returns>
        public static async Task<HarborVerificationOutputText> ReadAsync(HarborCheckoutService checkouts, Dock dock,
            HarborWorkspaceOperationResult completed, string checkId, string expectedHead, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(completed.VerificationOutputId)) throw new InvalidOperationException("harbor_verification_output_id_missing");
            byte[] stdout = await ReadStreamAsync(checkouts, dock, completed, checkId, expectedHead, false, token).ConfigureAwait(false);
            byte[] stderr = await ReadStreamAsync(checkouts, dock, completed, checkId, expectedHead, true, token).ConfigureAwait(false);
            return new HarborVerificationOutputText
            {
                StandardOutput = Encoding.UTF8.GetString(stdout),
                StandardError = Encoding.UTF8.GetString(stderr)
            };
        }

        private static async Task<byte[]> ReadStreamAsync(HarborCheckoutService checkouts, Dock dock,
            HarborWorkspaceOperationResult completed, string checkId, string expectedHead, bool standardError, CancellationToken token)
        {
            long retained = standardError ? completed.StandardErrorRetainedBytes : completed.StandardOutputRetainedBytes;
            long expectedTotal = standardError ? completed.StandardErrorLength : completed.StandardOutputLength;
            string expectedHash = standardError ? completed.StandardErrorSha256 ?? String.Empty : completed.StandardOutputSha256 ?? String.Empty;
            if (retained < 0 || retained > 16 * 1024 * 1024 + 512 || expectedTotal < 0 || expectedHash.Length != 64)
                throw new InvalidOperationException("harbor_verification_output_metadata_invalid");
            using (MemoryStream output = new MemoryStream(checked((int)retained)))
            {
                long offset = 0;
                while (offset < retained)
                {
                    HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.ReadVerificationOutput);
                    request.ExpectedHead = expectedHead;
                    request.MaxBytes = ChunkBytes;
                    request.VerificationOutput = new HarborVerificationOutputRequest
                    {
                        OutputId = completed.VerificationOutputId!, CheckId = checkId, StandardError = standardError,
                        Offset = offset, MaxBytes = ChunkBytes
                    };
                    HarborWorkspaceOperationResult chunk = await checkouts.ExecuteAsync(dock, request, token).ConfigureAwait(false);
                    if (!String.Equals(chunk.VerificationOutputId, completed.VerificationOutputId, StringComparison.Ordinal)
                        || !String.Equals(chunk.VerificationCheckId, checkId, StringComparison.Ordinal)
                        || chunk.Length != expectedTotal || String.IsNullOrEmpty(chunk.BytesBase64))
                        throw new InvalidOperationException("harbor_verification_output_chunk_mismatch");
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(chunk.BytesBase64); }
                    catch (FormatException exception) { throw new InvalidOperationException("harbor_verification_output_chunk_invalid", exception); }
                    if (bytes.Length < 1 || bytes.Length > ChunkBytes || bytes.Length > retained - offset)
                        throw new InvalidOperationException("harbor_verification_output_chunk_invalid");
                    output.Write(bytes, 0, bytes.Length);
                    offset += bytes.Length;
                }
                byte[] result = output.ToArray();
                string actualHash = Convert.ToHexString(SHA256.HashData(result)).ToLowerInvariant();
                if (!String.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("harbor_verification_output_hash_mismatch");
                return result;
            }
        }
    }
}
