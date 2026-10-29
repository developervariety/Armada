namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using Armada.Core.Models;

    /// <summary>
    /// Byte comparison by git blob hash: the worktree file's hash (git hash-object --no-filters, so no line-ending or
    /// encoding conversion) must equal the hash of the expected bytes. When it does not, the hashes of the expected file
    /// without its byte order mark and with LF line endings explain what was lost. Pure; no I/O.
    /// </summary>
    public static class BatteryByteComparison
    {
        #region Public-Methods

        /// <summary>
        /// The git blob hash (SHA-1 of "blob LENGTH\0" plus the bytes), lowercase hex.
        /// </summary>
        /// <param name="bytes">Content.</param>
        /// <returns>Hash.</returns>
        /// <exception cref="ArgumentNullException">Thrown when bytes is null.</exception>
        public static string GitBlobHash(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
            byte[] all = new byte[header.Length + bytes.Length];
            Buffer.BlockCopy(header, 0, all, 0, header.Length);
            Buffer.BlockCopy(bytes, 0, all, header.Length, bytes.Length);
#pragma warning disable CA5350 // git object names are SHA-1 by definition; this is an identity check, not security.
            return Convert.ToHexString(SHA1.HashData(all)).ToLowerInvariant();
#pragma warning restore CA5350
        }

        /// <summary>
        /// Compare a worktree file's blob hash with the expected bytes.
        /// </summary>
        /// <param name="path">Worktree path (for the report).</param>
        /// <param name="expected">Expected bytes.</param>
        /// <param name="actualHash">The worktree file's blob hash, or null when the file is missing.</param>
        /// <returns>The comparison.</returns>
        /// <exception cref="ArgumentNullException">Thrown when path or expected is null.</exception>
        public static BatteryFileComparison Compare(string path, byte[] expected, string? actualHash)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            string expectedHash = GitBlobHash(expected);
            BatteryFileComparison comparison = new BatteryFileComparison { Path = path, ExpectedHash = expectedHash, ActualHash = actualHash };
            if (String.IsNullOrEmpty(actualHash))
            {
                comparison.Reason = "The file is missing from the worktree.";
                return comparison;
            }

            string actual = actualHash.Trim().ToLowerInvariant();
            if (String.Equals(actual, expectedHash, StringComparison.Ordinal))
            {
                comparison.Matched = true;
                return comparison;
            }

            bool hasBom = expected.Length >= 3 && expected[0] == 0xEF && expected[1] == 0xBB && expected[2] == 0xBF;
            bool hasCrlf = ContainsCrlf(expected);
            byte[] noBom = hasBom ? expected.Skip(3).ToArray() : expected;
            Dictionary<string, string> variants = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hasBom && hasCrlf) variants[GitBlobHash(ToLf(noBom))] = "The byte order mark was dropped and CRLF line endings were converted to LF.";
            if (hasBom) variants[GitBlobHash(noBom)] = "The byte order mark was dropped (line endings kept).";
            if (hasCrlf) variants[GitBlobHash(ToLf(expected))] = "CRLF line endings were converted to LF (byte order mark kept).";

            comparison.Reason = variants.TryGetValue(actual, out string? reason)
                ? reason
                : "The content differs from the expected bytes (the requested change is missing or something else changed).";
            return comparison;
        }

        #endregion

        #region Private-Methods

        private static bool ContainsCrlf(byte[] bytes)
        {
            for (int i = 0; i + 1 < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n') return true;
            }

            return false;
        }

        private static byte[] ToLf(byte[] bytes)
        {
            List<byte> result = new List<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') continue;
                result.Add(bytes[i]);
            }

            return result.ToArray();
        }

        #endregion
    }
}
