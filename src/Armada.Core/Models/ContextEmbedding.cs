namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One stored embedding of a context source. A vector is current only while its model and the
    /// SHA-256 of the embedded text both match the source as it is read now.
    /// </summary>
    public sealed class ContextEmbedding
    {
        #region Public-Members

        /// <summary>Tenant that owns the embedded source.</summary>
        public string TenantId
        {
            get => _TenantId;
            set => _TenantId = String.IsNullOrWhiteSpace(value) ? throw new ArgumentNullException(nameof(TenantId)) : value;
        }

        /// <summary>Search item identity, for example <c>memory:&lt;id&gt;</c> or <c>context:&lt;chunk&gt;</c>.</summary>
        public string SourceKey
        {
            get => _SourceKey;
            set => _SourceKey = String.IsNullOrWhiteSpace(value) ? throw new ArgumentNullException(nameof(SourceKey)) : value;
        }

        /// <summary>Embedding model that produced the vector.</summary>
        public string Model
        {
            get => _Model;
            set => _Model = String.IsNullOrWhiteSpace(value) ? throw new ArgumentNullException(nameof(Model)) : value;
        }

        /// <summary>Lower-case hex SHA-256 of the exact text that was embedded.</summary>
        public string ContentSha256 { get; set; } = "";

        /// <summary>Vector values; the dimension is its length.</summary>
        public float[] Vector { get; set; } = Array.Empty<float>();

        #endregion

        #region Public-Methods

        /// <summary>The source key, and search item identity, of one native memory record.</summary>
        /// <param name="memoryId">Memory identifier.</param>
        public static string MemorySourceKey(string memoryId) => "memory:" + memoryId;

        #endregion

        #region Private-Members

        private string _TenantId = "default";
        private string _SourceKey = "unset";
        private string _Model = "unset";

        #endregion
    }
}
