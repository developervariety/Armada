namespace Armada.Core.Settings
{
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core;

    /// <summary>
    /// Settings for Admiral-owned codebase indexing.
    /// </summary>
    public class CodeIndexSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether code indexing tools are enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Directory where index metadata, chunks, and generated context packs are stored.
        /// </summary>
        public string IndexDirectory
        {
            get => _IndexDirectory;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(IndexDirectory));
                _IndexDirectory = value;
            }
        }

        /// <summary>
        /// Maximum file size read into the index.
        /// </summary>
        public long MaxFileBytes
        {
            get => _MaxFileBytes;
            set
            {
                if (value < 1024) value = 1024;
                if (value > 1024 * 1024 * 8) value = 1024 * 1024 * 8;
                _MaxFileBytes = value;
            }
        }

        /// <summary>
        /// Maximum source lines per indexed chunk.
        /// </summary>
        public int MaxChunkLines
        {
            get => _MaxChunkLines;
            set
            {
                if (value < 10) value = 10;
                if (value > 500) value = 500;
                _MaxChunkLines = value;
            }
        }

        /// <summary>
        /// Maximum search results returned by default.
        /// </summary>
        public int MaxSearchResults
        {
            get => _MaxSearchResults;
            set
            {
                if (value < 1) value = 1;
                if (value > 100) value = 100;
                _MaxSearchResults = value;
            }
        }

        /// <summary>
        /// Maximum context-pack evidence results returned by default.
        /// </summary>
        public int MaxContextPackResults
        {
            get => _MaxContextPackResults;
            set
            {
                if (value < 1) value = 1;
                if (value > 50) value = 50;
                _MaxContextPackResults = value;
            }
        }

        /// <summary>
        /// Whether semantic search is enabled.
        /// </summary>
        public bool UseSemanticSearch { get; set; } = false;

        /// <summary>
        /// Embedding model name.
        /// </summary>
        public string EmbeddingModel
        {
            get => _EmbeddingModel;
            set => _EmbeddingModel = value ?? string.Empty;
        }

        /// <summary>
        /// Embedding API base URL.
        /// </summary>
        public string EmbeddingApiBaseUrl
        {
            get => _EmbeddingApiBaseUrl;
            set => _EmbeddingApiBaseUrl = value ?? string.Empty;
        }

        /// <summary>
        /// Embedding API key.
        /// </summary>
        public string EmbeddingApiKey
        {
            get => _EmbeddingApiKey;
            set => _EmbeddingApiKey = value ?? string.Empty;
        }

        /// <summary>
        /// Semantic score blend weight.
        /// </summary>
        public double SemanticWeight
        {
            get => _SemanticWeight;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 1.0) value = 1.0;
                _SemanticWeight = value;
            }
        }

        /// <summary>
        /// Lexical score blend weight.
        /// </summary>
        public double LexicalWeight
        {
            get => _LexicalWeight;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 1.0) value = 1.0;
                _LexicalWeight = value;
            }
        }

        /// <summary>
        /// Whether summarizer calls are enabled.
        /// </summary>
        public bool UseSummarizer { get; set; } = false;

        /// <summary>
        /// Inference client mode used for summarization and file-signature generation.
        /// </summary>
        public string InferenceClient
        {
            get => _InferenceClient;
            set => _InferenceClient = value ?? "Http";
        }

        /// <summary>
        /// OpenCode server inference settings.
        /// </summary>
        public OpenCodeServerSettings OpenCodeServer
        {
            get => _OpenCodeServer;
            set => _OpenCodeServer = value ?? new OpenCodeServerSettings();
        }

        /// <summary>
        /// Summarizer model name.
        /// </summary>
        public string SummarizerModel
        {
            get => _SummarizerModel;
            set => _SummarizerModel = value ?? string.Empty;
        }

        /// <summary>
        /// Summarizer API base URL. Empty falls back to <see cref="EmbeddingApiBaseUrl"/>.
        /// </summary>
        public string SummarizerApiBaseUrl
        {
            get => _SummarizerApiBaseUrl;
            set => _SummarizerApiBaseUrl = value ?? string.Empty;
        }

        /// <summary>
        /// Summarizer API key. Empty falls back to <see cref="EmbeddingApiKey"/>.
        /// </summary>
        public string SummarizerApiKey
        {
            get => _SummarizerApiKey;
            set => _SummarizerApiKey = value ?? string.Empty;
        }

        /// <summary>
        /// Independent time budget, in seconds, for a single summarizer completion call.
        /// When a summarization exceeds this budget the context pack falls back to the raw
        /// markdown evidence rather than blocking. Clamped to 1-600 seconds.
        /// </summary>
        public int SummarizerTimeoutSeconds
        {
            get => _SummarizerTimeoutSeconds;
            set
            {
                if (value < 1) value = 1;
                if (value > 600) value = 600;
                _SummarizerTimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Maximum tokens for summarizer output.
        /// </summary>
        public int MaxSummaryOutputTokens
        {
            get => _MaxSummaryOutputTokens;
            set
            {
                if (value < 256) value = 256;
                if (value > 8192) value = 8192;
                _MaxSummaryOutputTokens = value;
            }
        }

        /// <summary>
        /// Maximum number of chunks sent to the embedding provider in one request.
        /// </summary>
        public int EmbeddingBatchSize
        {
            get => _EmbeddingBatchSize;
            set
            {
                if (value < 1) value = 1;
                if (value > 256) value = 256;
                _EmbeddingBatchSize = value;
            }
        }

        /// <summary>
        /// Number of embedded chunks between progress log entries during index updates.
        /// </summary>
        public int EmbeddingProgressLogInterval
        {
            get => _EmbeddingProgressLogInterval;
            set
            {
                if (value < 50) value = 50;
                if (value > 2000) value = 2000;
                _EmbeddingProgressLogInterval = value;
            }
        }

        /// <summary>
        /// Delay used to coalesce post-landing code-index refreshes for the same vessel.
        /// Set to 0 to refresh immediately.
        /// </summary>
        public int PostLandRefreshDebounceSeconds
        {
            get => _PostLandRefreshDebounceSeconds;
            set
            {
                if (value < 0) value = 0;
                if (value > 3600) value = 3600;
                _PostLandRefreshDebounceSeconds = value;
            }
        }

        /// <summary>
        /// Whether file signatures are enabled.
        /// </summary>
        public bool UseFileSignatures { get; set; } = false;

        /// <summary>
        /// File signature model name. Empty falls back to <see cref="SummarizerModel"/>.
        /// </summary>
        public string SignatureModel
        {
            get => _SignatureModel;
            set => _SignatureModel = value ?? string.Empty;
        }

        /// <summary>
        /// Score boost blend weight for file-signature ranking.
        /// </summary>
        public double FileSignatureBoostWeight
        {
            get => _FileSignatureBoostWeight;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 1.0) value = 1.0;
                _FileSignatureBoostWeight = value;
            }
        }

        /// <summary>
        /// Whether search ranking should use graph sidecar matches as additional evidence.
        /// </summary>
        public bool UseGraphSearchBoosts { get; set; } = true;

        /// <summary>
        /// Score boost applied to files containing symbols that directly match the query.
        /// </summary>
        public double GraphSeedBoost
        {
            get => _GraphSeedBoost;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 100.0) value = 100.0;
                _GraphSeedBoost = value;
            }
        }

        /// <summary>
        /// Score boost applied to direct caller/callee files for matched query symbols.
        /// </summary>
        public double GraphNeighborBoost
        {
            get => _GraphNeighborBoost;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 100.0) value = 100.0;
                _GraphNeighborBoost = value;
            }
        }

        /// <summary>
        /// Additional score boost for endpoint/route symbols.
        /// </summary>
        public double GraphEndpointBoost
        {
            get => _GraphEndpointBoost;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 100.0) value = 100.0;
                _GraphEndpointBoost = value;
            }
        }

        /// <summary>
        /// Score boost applied when a query names a detected framework.
        /// </summary>
        public double GraphFrameworkBoost
        {
            get => _GraphFrameworkBoost;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 100.0) value = 100.0;
                _GraphFrameworkBoost = value;
            }
        }

        /// <summary>
        /// Score boost applied when a query names a graph symbol tag.
        /// </summary>
        public double GraphTagBoost
        {
            get => _GraphTagBoost;
            set
            {
                if (value < 0.0) value = 0.0;
                if (value > 100.0) value = 100.0;
                _GraphTagBoost = value;
            }
        }

        /// <summary>
        /// Directory names excluded from indexing.
        /// </summary>
        public List<string> ExcludedDirectoryNames { get; set; } = new List<string>
        {
            ".git",
            ".vs",
            ".idea",
            ".vscode",
            "bin",
            "obj",
            "node_modules",
            "dist",
            "build",
            "coverage",
            "packages",
            "TestResults",
            "Generated",
            "generated",
            "output",
            "decompiled-src",
            "decrypted-db",
            "approved-reference",
            "reference-export",
            "source-export",
            "source-dump"
        };

        /// <summary>
        /// File names excluded from indexing.
        /// </summary>
        public List<string> ExcludedFileNames { get; set; } = new List<string>
        {
            ".env",
            ".env.local",
            ".env.development",
            ".env.production",
            "secrets.json",
            "usersecrets.json",
            "credentials",
            "credentials.json",
            "id_rsa",
            "id_dsa",
            "id_ecdsa",
            "id_ed25519"
        };

        /// <summary>
        /// File extensions excluded from indexing.
        /// </summary>
        public List<string> ExcludedExtensions { get; set; } = new List<string>
        {
            ".dll",
            ".exe",
            ".pdb",
            ".bin",
            ".obj",
            ".zip",
            ".7z",
            ".gz",
            ".tar",
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".webp",
            ".ico",
            ".pdf",
            ".pfx",
            ".p12",
            ".pem",
            ".key",
            ".crt",
            ".cer"
        };

        /// <summary>
        /// Case-insensitive path fragments excluded from indexing.
        /// </summary>
        public List<string> ExcludedPathFragments { get; set; } = new List<string>
        {
            "/.secrets/",
            "/secrets/",
            "/secret/",
            "/credentials/",
            "/private-keys/",
            "/build-output/",
            "/generated/",
            "/decompiled-src/",
            "/approved-reference/",
            "/reference-export/",
            "/source-export/",
            "/source-dump/"
        };

        /// <summary>
        /// Path fragments that are allowed but marked reference-only when indexed.
        /// Future sidecar implementations can use this for generated/decompiled evidence.
        /// </summary>
        public List<string> ReferenceOnlyPathFragments { get; set; } = new List<string>();

        #endregion

        #region Private-Members

        private string _IndexDirectory = Path.Combine(Constants.DefaultDataDirectory, "code-index");
        private long _MaxFileBytes = 256 * 1024;
        private int _MaxChunkLines = 80;
        private int _MaxSearchResults = 10;
        private int _MaxContextPackResults = 8;
        private string _EmbeddingModel = "deepseek-embedding";
        private string _EmbeddingApiBaseUrl = "https://api.deepseek.com";
        private string _EmbeddingApiKey = string.Empty;
        private double _SemanticWeight = 0.7;
        private double _LexicalWeight = 0.3;
        private string _InferenceClient = "Http";
        private OpenCodeServerSettings _OpenCodeServer = new OpenCodeServerSettings();
        private string _SummarizerModel = "deepseek-chat";
        private string _SummarizerApiBaseUrl = string.Empty;
        private string _SummarizerApiKey = string.Empty;
        private int _SummarizerTimeoutSeconds = 10;
        private int _MaxSummaryOutputTokens = 2048;
        private int _EmbeddingBatchSize = 32;
        private int _EmbeddingProgressLogInterval = 200;
        private int _PostLandRefreshDebounceSeconds = 30;
        private string _SignatureModel = string.Empty;
        private double _FileSignatureBoostWeight = 0.2;
        private double _GraphSeedBoost = 18.0;
        private double _GraphNeighborBoost = 8.0;
        private double _GraphEndpointBoost = 12.0;
        private double _GraphFrameworkBoost = 10.0;
        private double _GraphTagBoost = 6.0;

        #endregion
    }
}
