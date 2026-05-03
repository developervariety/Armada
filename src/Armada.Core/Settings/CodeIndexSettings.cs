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
            "jpro-export",
            "otr-export",
            "otrperformance"
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
            "/jpro-export/",
            "/otr-export/",
            "/otrperformance/"
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

        #endregion
    }
}
