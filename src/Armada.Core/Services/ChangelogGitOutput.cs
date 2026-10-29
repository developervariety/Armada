namespace Armada.Core.Services
{
    /// <summary>
    /// Exit code and standard output of one git invocation made while resolving a CHANGELOG.md landing conflict.
    /// </summary>
    public sealed class ChangelogGitOutput
    {
        #region Public-Members

        /// <summary>
        /// Process exit code.
        /// </summary>
        public int ExitCode { get; }

        /// <summary>
        /// Standard output text.
        /// </summary>
        public string StandardOutput { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="exitCode">Process exit code.</param>
        /// <param name="standardOutput">Standard output text.</param>
        public ChangelogGitOutput(int exitCode, string standardOutput)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
        }

        #endregion
    }
}
