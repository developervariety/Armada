namespace Armada.Server
{
    using System.Reflection;

    /// <summary>
    /// Provides build-time information for the running server assembly.
    /// </summary>
    public static class BuildInfo
    {
        #region Public-Members

        /// <summary>
        /// The git commit SHA the server was built from, or null if not embedded.
        /// </summary>
        public static string? RunningCommit { get; } = ParseCommit(
            Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Extracts the git commit SHA from an informational version string.
        /// Returns the substring after the first '+', trimmed, or null when absent or empty.
        /// </summary>
        /// <param name="informationalVersion">The AssemblyInformationalVersion string to parse.</param>
        /// <returns>The commit SHA, or null if not present.</returns>
        public static string? ParseCommit(string? informationalVersion)
        {
            if (string.IsNullOrEmpty(informationalVersion)) return null;
            int plus = informationalVersion.IndexOf('+');
            if (plus < 0) return null;
            string sha = informationalVersion.Substring(plus + 1).Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }

        #endregion
    }
}
