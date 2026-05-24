using System.Collections.Generic;

namespace Armada.Core.Models
{
    /// <summary>
    /// Source excerpt for a graph symbol or explored file section.
    /// </summary>
    public class CodeGraphSourceSection
    {
        /// <summary>
        /// Repository-relative source path.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// One-based first line included in the excerpt.
        /// </summary>
        public int StartLine { get; set; } = 1;

        /// <summary>
        /// One-based last line included in the excerpt.
        /// </summary>
        public int EndLine { get; set; } = 1;

        /// <summary>
        /// Source text for the requested excerpt.
        /// </summary>
        public string Content { get; set; } = "";
    }

    /// <summary>
    /// Request for a specific graph node and optional source excerpt.
    /// </summary>
    public class CodeGraphNodeRequest
    {
        /// <summary>
        /// Vessel identifier whose persisted code graph should be queried.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Simple or qualified symbol name to resolve.
        /// </summary>
        public string Symbol { get; set; } = "";

        /// <summary>
        /// Whether to include a source excerpt for the primary resolved symbol.
        /// </summary>
        public bool IncludeSource { get; set; } = true;

        /// <summary>
        /// Number of context lines to include around the symbol range.
        /// </summary>
        public int SourcePadding { get; set; } = 2;
    }

    /// <summary>
    /// Node detail response for a graph symbol.
    /// </summary>
    public class CodeGraphNodeResponse
    {
        /// <summary>
        /// Persisted index status used for the graph read.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Original requested symbol string.
        /// </summary>
        public string RequestedSymbol { get; set; } = "";

        /// <summary>
        /// Symbols resolved from the request.
        /// </summary>
        public List<CodeGraphSymbolRecord> ResolvedSymbols { get; set; } = new List<CodeGraphSymbolRecord>();

        /// <summary>
        /// Direct caller symbols for the primary resolved symbol.
        /// </summary>
        public List<CodeGraphNeighborResult> Callers { get; set; } = new List<CodeGraphNeighborResult>();

        /// <summary>
        /// Direct callee symbols for the primary resolved symbol.
        /// </summary>
        public List<CodeGraphNeighborResult> Callees { get; set; } = new List<CodeGraphNeighborResult>();

        /// <summary>
        /// Optional source excerpt for the primary resolved symbol.
        /// </summary>
        public CodeGraphSourceSection? Source { get; set; }

        /// <summary>
        /// Non-fatal warnings from the graph loader.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Request for indexed file/symbol structure.
    /// </summary>
    public class CodeGraphFileStructureRequest
    {
        /// <summary>
        /// Vessel identifier whose persisted code graph should be queried.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Optional repository-relative path prefix filter.
        /// </summary>
        public string? PathPrefix { get; set; }

        /// <summary>
        /// Maximum number of files to return.
        /// </summary>
        public int Limit { get; set; } = 100;

        /// <summary>
        /// Whether each file entry should include its symbol records.
        /// </summary>
        public bool IncludeSymbols { get; set; } = true;
    }

    /// <summary>
    /// Indexed file/symbol structure response.
    /// </summary>
    public class CodeGraphFileStructureResponse
    {
        /// <summary>
        /// Persisted index status used for the graph read.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Indexed files grouped with optional symbol lists.
        /// </summary>
        public List<CodeGraphFileStructureEntry> Files { get; set; } = new List<CodeGraphFileStructureEntry>();

        /// <summary>
        /// Non-fatal warnings from the graph loader.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Symbol summary for one indexed file.
    /// </summary>
    public class CodeGraphFileStructureEntry
    {
        /// <summary>
        /// Repository-relative source path.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Detected or indexed language for the file.
        /// </summary>
        public string Language { get; set; } = "";

        /// <summary>
        /// Number of graph symbols indexed for the file.
        /// </summary>
        public int SymbolCount { get; set; }

        /// <summary>
        /// Symbols indexed for the file when requested.
        /// </summary>
        public List<CodeGraphSymbolRecord> Symbols { get; set; } = new List<CodeGraphSymbolRecord>();
    }

    /// <summary>
    /// Request for grouped graph exploration around a symbol/query.
    /// </summary>
    public class CodeGraphExploreRequest
    {
        /// <summary>
        /// Vessel identifier whose persisted code graph should be queried.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Symbol name or free-text query to explore.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Maximum graph traversal depth.
        /// </summary>
        public int MaxDepth { get; set; } = 2;

        /// <summary>
        /// Maximum number of symbols to include before grouping by file.
        /// </summary>
        public int MaxResults { get; set; } = 25;

        /// <summary>
        /// Whether grouped files should include source excerpts.
        /// </summary>
        public bool IncludeSource { get; set; } = true;
    }

    /// <summary>
    /// Grouped graph exploration response.
    /// </summary>
    public class CodeGraphExploreResponse
    {
        /// <summary>
        /// Persisted index status used for the graph read.
        /// </summary>
        public CodeIndexStatus Status { get; set; } = new CodeIndexStatus();

        /// <summary>
        /// Original graph exploration query.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Seed symbols resolved from the query before traversal.
        /// </summary>
        public List<CodeGraphSymbolRecord> ResolvedSeedSymbols { get; set; } = new List<CodeGraphSymbolRecord>();

        /// <summary>
        /// Traversal results grouped by repository-relative file path.
        /// </summary>
        public List<CodeGraphExploreFile> Files { get; set; } = new List<CodeGraphExploreFile>();

        /// <summary>
        /// Graph relationships connecting included symbols.
        /// </summary>
        public List<CodeGraphEdgeRecord> Relationships { get; set; } = new List<CodeGraphEdgeRecord>();

        /// <summary>
        /// Non-fatal warnings from the graph loader.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Exploration data grouped by file.
    /// </summary>
    public class CodeGraphExploreFile
    {
        /// <summary>
        /// Repository-relative source path.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Symbols selected for this file.
        /// </summary>
        public List<CodeGraphSymbolRecord> Symbols { get; set; } = new List<CodeGraphSymbolRecord>();

        /// <summary>
        /// Optional source excerpts for selected symbols in this file.
        /// </summary>
        public List<CodeGraphSourceSection> SourceSections { get; set; } = new List<CodeGraphSourceSection>();

        /// <summary>
        /// Highest traversal or match score among selected symbols in the file.
        /// </summary>
        public double Score { get; set; }
    }
}
