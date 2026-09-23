namespace Armada.Core.Services
{
    using System;
    using System.IO;

    /// <summary>
    /// The one containment rule for file-system paths. A path is inside a root when, after both are
    /// normalized with <see cref="Path.GetFullPath(string)"/>, it equals the root or starts with the root
    /// plus a directory separator. A sibling whose name starts with the root's name (root <c>dashboard</c>,
    /// path <c>dashboard-backup/x</c>) is outside. The comparison is ordinal on every platform: a
    /// directory can be case-sensitive on any host, so a case-only difference is refused, never accepted.
    /// An ordinal check can refuse a path that is inside the root, but it can never accept one outside it,
    /// so every write, read and deletion boundary uses it. <see cref="IsWithinHostCasing"/> is the one
    /// exception, for identity matches where a refusal silently disables a feature.
    /// </summary>
    public static class PathContainment
    {
        #region Public-Methods

        /// <summary>
        /// Whether <paramref name="path"/> is <paramref name="rootDirectory"/> itself or lies below it.
        /// </summary>
        /// <param name="rootDirectory">Root directory; relative, trailing-separator and <c>..</c> forms are normalized.</param>
        /// <param name="path">Candidate path; relative forms resolve against the current directory.</param>
        /// <returns>True when the normalized path is the root or below it.</returns>
        public static bool IsWithin(string rootDirectory, string path)
        {
            if (String.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentNullException(nameof(rootDirectory));
            if (String.IsNullOrWhiteSpace(path)) return false;

            string normalizedRoot = NormalizeRoot(rootDirectory);
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return IsWithinNormalized(normalizedRoot, normalizedPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether <paramref name="path"/> is <paramref name="rootDirectory"/> itself or lies below it, comparing
        /// letter case the way the host's default file system does: exactly on Linux, ignoring case on Windows
        /// and macOS. Use it only to recognize a location (for example, which vessel checkout holds the running
        /// server), never to allow a write, read or deletion; those use <see cref="IsWithin"/>.
        /// </summary>
        /// <param name="rootDirectory">Root directory; relative, trailing-separator and <c>..</c> forms are normalized.</param>
        /// <param name="path">Candidate path; relative forms resolve against the current directory.</param>
        /// <returns>True when the normalized path is the root or below it under the host's casing rule.</returns>
        public static bool IsWithinHostCasing(string rootDirectory, string path)
        {
            if (String.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentNullException(nameof(rootDirectory));
            if (String.IsNullOrWhiteSpace(path)) return false;

            string normalizedRoot = NormalizeRoot(rootDirectory);
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return IsWithinNormalized(normalizedRoot, normalizedPath, HostPathComparison);
        }

        /// <summary>
        /// Resolve <paramref name="relativePath"/> against <paramref name="rootDirectory"/> and return the full
        /// path only when it stays inside the root. A rooted <paramref name="relativePath"/> is taken as is and
        /// must itself lie inside the root.
        /// </summary>
        /// <param name="rootDirectory">Root directory.</param>
        /// <param name="relativePath">Path relative to the root, or an absolute path.</param>
        /// <returns>The normalized full path when it is inside the root; otherwise null.</returns>
        public static string? TryResolve(string rootDirectory, string relativePath)
        {
            if (String.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentNullException(nameof(rootDirectory));
            if (relativePath == null) return null;

            string normalizedRoot = NormalizeRoot(rootDirectory);
            string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            return IsWithinNormalized(normalizedRoot, Path.TrimEndingDirectorySeparator(fullPath), StringComparison.Ordinal) ? fullPath : null;
        }

        #endregion

        #region Private-Methods

        private static string NormalizeRoot(string rootDirectory)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        }

        private static StringComparison HostPathComparison =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static bool IsWithinNormalized(string normalizedRoot, string normalizedPath, StringComparison comparison)
        {
            if (String.Equals(normalizedPath, normalizedRoot, comparison)) return true;
            string rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(rootPrefix, comparison);
        }

        #endregion
    }
}
