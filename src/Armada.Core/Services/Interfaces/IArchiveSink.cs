namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Destination for archived history. An implementation verifies each upload against the local
    /// SHA-256 and size before it reports success, so a caller frees its local copy only on
    /// <see cref="ArchivePutResult.Verified"/>. Failures are returned as named results, not thrown,
    /// except for cancellation.
    /// </summary>
    public interface IArchiveSink
    {
        /// <summary>
        /// Upload a local file under a key and verify the stored copy.
        /// </summary>
        /// <param name="key">Object key, built with <see cref="Archive.ArchiveKeys"/>.</param>
        /// <param name="localPath">Absolute path of the file to upload.</param>
        /// <param name="sha256">Lower-case hex SHA-256 of the file, computed by the caller.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified result or a named failure.</returns>
        Task<ArchivePutResult> PutFileAsync(string key, string localPath, string sha256, CancellationToken token = default);

        /// <summary>
        /// Check whether a key already holds exactly the given content, without uploading anything.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="bytes">Size of the local content.</param>
        /// <param name="sha256">Lower-case hex SHA-256 of the local content.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified when the stored size and SHA-256 match; otherwise a named failure, <c>not_found</c> when the key holds no object.</returns>
        Task<ArchivePutResult> VerifyStoredAsync(string key, long bytes, string sha256, CancellationToken token = default);

        /// <summary>
        /// Upload in-memory content (a trace record or a manifest) under a key and verify it.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="content">Content bytes.</param>
        /// <param name="contentType">MIME type stored with the object.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified result or a named failure.</returns>
        Task<ArchivePutResult> PutBytesAsync(string key, byte[] content, string contentType, CancellationToken token = default);

        /// <summary>
        /// List every object whose key starts with a prefix.
        /// </summary>
        /// <param name="prefix">Key prefix.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Listed objects.</returns>
        Task<List<ArchiveObjectInfo>> ListAsync(string prefix, CancellationToken token = default);

        /// <summary>
        /// Read one object's content.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="maxBytes">Largest content accepted; a larger object throws.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The content, or null when the key does not exist. Any other failure throws.</returns>
        Task<byte[]?> GetBytesAsync(string key, long maxBytes, CancellationToken token = default);

        /// <summary>
        /// Delete one object. Deleting a key that does not exist succeeds.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null on success, otherwise the failure message.</returns>
        Task<string?> DeleteAsync(string key, CancellationToken token = default);
    }
}
