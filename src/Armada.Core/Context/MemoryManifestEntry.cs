namespace Armada.Core.Context
{
    using System;

    /// <summary>
    /// One staged memory file in a <see cref="MemoryManifest"/>: its dock-relative path, UTF-8 byte
    /// count and SHA-256 digest, so a captain can confirm it holds the same bytes as every other stage.
    /// </summary>
    public sealed class MemoryManifestEntry
    {
        #region Public-Members

        /// <summary>Dock-relative path, using forward slashes.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>UTF-8 byte count of the file as written.</summary>
        public int Bytes { get; set; }

        /// <summary>Lower-case hexadecimal SHA-256 of the file's UTF-8 bytes.</summary>
        public string Sha256 { get; set; } = String.Empty;

        #endregion
    }
}
