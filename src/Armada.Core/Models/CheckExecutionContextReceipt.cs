namespace Armada.Core.Models
{
    using System;

    /// <summary>Versioned proof of the context captured for an independently executed full Check.</summary>
    public sealed class CheckExecutionContextReceipt
    {
        /// <summary>Receipt format version; zero or an unknown version cannot satisfy reuse.</summary>
        public int Version { get; set; } = 0;

        /// <summary>The exact Check that executed the command.</summary>
        public string CheckRunId { get; set; } = String.Empty;

        /// <summary>Opaque SHA-256 of the captured execution context. Environment values are not stored.</summary>
        public string Fingerprint { get; set; } = String.Empty;
    }
}
