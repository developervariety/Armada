namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// A single symbol extracted from a source file during code indexing.
    /// Emitted to the <c>symbols.jsonl</c> sidecar alongside <c>chunks.jsonl</c>.
    /// </summary>
    public class CodeGraphSymbolRecord
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier this symbol belongs to.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Commit SHA at which this symbol was extracted.
        /// </summary>
        public string CommitSha { get; set; } = "";

        /// <summary>
        /// Repository-relative path of the source file.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Discriminated symbol kind (class, method, etc.).
        /// </summary>
        public CodeGraphSymbolKindEnum Kind { get; set; } = CodeGraphSymbolKindEnum.Unknown;

        /// <summary>
        /// Simple (unqualified) name of the symbol, e.g. <c>DoWork</c>.
        /// </summary>
        public string SimpleName { get; set; } = "";

        /// <summary>
        /// Fully qualified name when determinable from the file context, e.g. <c>Armada.Core.Services.CodeIndexService.DoWork</c>.
        /// Empty when the qualifier cannot be resolved from the local file alone.
        /// </summary>
        public string QualifiedName { get; set; } = "";

        /// <summary>
        /// 1-based line number where the symbol declaration begins.
        /// </summary>
        public int StartLine { get; set; } = 1;

        /// <summary>
        /// 1-based line number where the symbol declaration ends (best-effort estimate).
        /// </summary>
        public int EndLine { get; set; } = 1;

        /// <summary>
        /// SHA-256 hash of the full file content from which this symbol was extracted.
        /// Matches <see cref="CodeIndexRecord.ContentHash"/> for the same file.
        /// </summary>
        public string ContentHash { get; set; } = "";

        /// <summary>
        /// Detected source language for the symbol when known.
        /// </summary>
        public string Language { get; set; } = "";

        /// <summary>
        /// Recognized framework or platform context, e.g. aspnet, react, express, fastapi, spring.
        /// </summary>
        public string Framework { get; set; } = "";

        /// <summary>
        /// Additional symbol tags used for ranking and diagnostics.
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        #endregion
    }
}
