namespace Armada.Runtimes.Tools
{
    /// <summary>
    /// Provides configurable safety limits for tool execution.
    /// </summary>
    public static class ToolSafetyLimits
    {
        #region Public-Members

        /// <summary>
        /// The maximum file size in bytes that the read_file tool will accept. Defaults to 1 MB.
        /// </summary>
        public static int MaxReadFileBytes = 1_048_576;

        /// <summary>The maximum UTF-8 size accepted for one tool text argument. Defaults to 1 MB.</summary>
        public static int MaxToolInputBytes = 1_048_576;

        /// <summary>The maximum UTF-8 size returned by one workspace tool. Defaults to 256 KB.</summary>
        public static int MaxToolOutputBytes = 262_144;

        /// <summary>The maximum number of entries visited by recursive file tools.</summary>
        public static int MaxEnumeratedEntries = 10_000;

        /// <summary>
        /// The maximum output size in bytes for process stdout/stderr before truncation. Defaults to 100 KB.
        /// </summary>
        public static int MaxProcessOutputBytes = 102_400;

        /// <summary>
        /// The default timeout in milliseconds for general tool execution. Defaults to 30 seconds.
        /// </summary>
        public static int DefaultToolTimeoutMs = 30_000;

        /// <summary>
        /// The default timeout in milliseconds for process execution. Defaults to 120 seconds.
        /// </summary>
        public static int DefaultProcessTimeoutMs = 120_000;

        #endregion
    }
}
