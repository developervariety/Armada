namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A configuration file that must be materialized on disk before an isolated captain launch, so the
    /// scoped agent configuration contains the Armada MCP server. Carries a path relative to the scoped
    /// configuration directory and the exact file contents.
    /// </summary>
    public sealed class IsolationConfigFile
    {
        #region Public-Members

        /// <summary>
        /// Path of the file to write, relative to the scoped configuration directory (e.g.
        /// "armada-mcp.json" or ".gemini/settings.json"). Never absolute.
        /// </summary>
        public string RelativePath
        {
            get => _RelativePath;
            set => _RelativePath = value ?? throw new ArgumentNullException(nameof(RelativePath));
        }

        /// <summary>
        /// Exact contents to write to the file.
        /// </summary>
        public string Contents
        {
            get => _Contents;
            set => _Contents = value ?? throw new ArgumentNullException(nameof(Contents));
        }

        #endregion

        #region Private-Members

        private string _RelativePath = "";
        private string _Contents = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public IsolationConfigFile()
        {
        }

        /// <summary>
        /// Instantiate with a relative path and contents.
        /// </summary>
        /// <param name="relativePath">Path relative to the scoped configuration directory.</param>
        /// <param name="contents">File contents.</param>
        public IsolationConfigFile(string relativePath, string contents)
        {
            RelativePath = relativePath;
            Contents = contents;
        }

        #endregion
    }
}
