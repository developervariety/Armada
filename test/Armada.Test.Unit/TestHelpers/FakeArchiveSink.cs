namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// An in-memory archive sink. It stores each uploaded object with its last-modified time, verifies
    /// an upload by hashing what it stored against the caller's SHA-256, and can be told to fail
    /// uploads whose key contains a given text, so a test can prove a failed upload keeps the file.
    /// </summary>
    public sealed class FakeArchiveSink : IArchiveSink
    {
        /// <summary>Stored objects by key.</summary>
        public Dictionary<string, byte[]> Objects { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Last-modified time of each stored object.</summary>
        public Dictionary<string, DateTime> LastModified { get; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>Keys uploaded and stored, in order, one entry per upload.</summary>
        public List<string> Puts { get; } = new List<string>();

        /// <summary>Keys deleted, in order.</summary>
        public List<string> Deleted { get; } = new List<string>();

        /// <summary>An upload whose key contains this text fails verification. Null fails none.</summary>
        public string? FailKeysContaining { get; set; } = null;

        /// <inheritdoc />
        public async Task<ArchivePutResult> PutFileAsync(string key, string localPath, string sha256, CancellationToken token = default)
        {
            byte[] content = await File.ReadAllBytesAsync(localPath, token).ConfigureAwait(false);
            return Store(key, content, sha256);
        }

        /// <inheritdoc />
        public Task<ArchivePutResult> VerifyStoredAsync(string key, long bytes, string sha256, CancellationToken token = default)
        {
            if (!Objects.TryGetValue(key, out byte[]? content))
                return Task.FromResult(ArchivePutResult.Failed(key, "not_found", "no stored object " + key));
            string stored = Hash(content);
            if (content.LongLength != bytes || !String.Equals(stored, sha256, StringComparison.Ordinal))
                return Task.FromResult(ArchivePutResult.Failed(key, "sha256_mismatch", "the stored object does not match the local file"));
            return Task.FromResult(new ArchivePutResult { Key = key, Verified = true, Bytes = content.LongLength, Sha256 = stored });
        }

        /// <inheritdoc />
        public Task<ArchivePutResult> PutBytesAsync(string key, byte[] content, string contentType, CancellationToken token = default)
        {
            return Task.FromResult(Store(key, content, Hash(content)));
        }

        /// <inheritdoc />
        public Task<List<ArchiveObjectInfo>> ListAsync(string prefix, CancellationToken token = default)
        {
            List<ArchiveObjectInfo> list = Objects.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => new ArchiveObjectInfo { Key = key, Bytes = Objects[key].Length, LastModifiedUtc = LastModified[key] })
                .ToList();
            return Task.FromResult(list);
        }

        /// <inheritdoc />
        public Task<byte[]?> GetBytesAsync(string key, long maxBytes, CancellationToken token = default)
        {
            if (!Objects.TryGetValue(key, out byte[]? content)) return Task.FromResult<byte[]?>(null);
            if (content.LongLength > maxBytes) throw new InvalidOperationException("object " + key + " is above the read limit");
            return Task.FromResult<byte[]?>(content);
        }

        /// <inheritdoc />
        public Task<string?> DeleteAsync(string key, CancellationToken token = default)
        {
            Objects.Remove(key);
            LastModified.Remove(key);
            Deleted.Add(key);
            return Task.FromResult<string?>(null);
        }

        /// <summary>Store an object directly, as if it had been uploaded at the given time.</summary>
        public void Seed(string key, byte[] content, DateTime lastModifiedUtc)
        {
            Objects[key] = content;
            LastModified[key] = lastModifiedUtc;
        }

        private ArchivePutResult Store(string key, byte[] content, string sha256)
        {
            if (FailKeysContaining != null && key.Contains(FailKeysContaining, StringComparison.Ordinal))
                return ArchivePutResult.Failed(key, "sha256_mismatch", "the stored object does not match the local file");
            Objects[key] = content;
            LastModified[key] = DateTime.UtcNow;
            Puts.Add(key);
            string stored = Hash(content);
            if (!String.Equals(stored, sha256, StringComparison.Ordinal))
                return ArchivePutResult.Failed(key, "sha256_mismatch", "the stored object does not match the local file");
            return new ArchivePutResult { Key = key, Verified = true, Bytes = content.Length, Sha256 = stored };
        }

        private static string Hash(byte[] content)
        {
            return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        }
    }
}
