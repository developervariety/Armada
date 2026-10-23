namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Outcome of one archive upload. <see cref="Verified"/> is true only when the bucket holds an
    /// object whose size and SHA-256 match the local content; a caller may free the local copy only
    /// then. A failed upload names its cause in <see cref="FailureCode"/> and <see cref="FailureMessage"/>.
    /// </summary>
    public class ArchivePutResult
    {
        #region Public-Members

        /// <summary>
        /// <see cref="FailureCode"/> set when a read-back verify is refused because the object store's
        /// download or transaction cap is exceeded. This failure is not per-object: the caller stops
        /// verifying for the rest of the run rather than sending one more capped request per file.
        /// </summary>
        public const string CapExceededCode = "cap_exceeded";

        /// <summary>
        /// Object key that was written.
        /// </summary>
        public string Key { get; set; } = String.Empty;

        /// <summary>
        /// True when the stored object's size and SHA-256 match the local content.
        /// </summary>
        public bool Verified { get; set; } = false;

        /// <summary>
        /// Size of the uploaded content in bytes.
        /// </summary>
        public long Bytes { get; set; } = 0;

        /// <summary>
        /// Lower-case hex SHA-256 of the uploaded content.
        /// </summary>
        public string Sha256 { get; set; } = String.Empty;

        /// <summary>
        /// Entity tag the bucket returned, when it returned one.
        /// </summary>
        public string? ETag { get; set; } = null;

        /// <summary>
        /// Short machine-readable cause when the upload is not verified, for example
        /// <c>upload_failed</c>, <c>size_mismatch</c>, <c>sha256_mismatch</c>, <c>credentials_missing</c>.
        /// </summary>
        public string? FailureCode { get; set; } = null;

        /// <summary>
        /// Human-readable cause when the upload is not verified.
        /// </summary>
        public string? FailureMessage { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build a failed result.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="code">Failure code.</param>
        /// <param name="message">Failure message.</param>
        /// <returns>Failed result.</returns>
        public static ArchivePutResult Failed(string key, string code, string message)
        {
            return new ArchivePutResult { Key = key, Verified = false, FailureCode = code, FailureMessage = message };
        }

        #endregion
    }
}
