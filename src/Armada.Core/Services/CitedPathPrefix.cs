namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Reads the checkout a cited path names by its first segment. Text copied from a report written in a
    /// parent workspace names a file with the repository's own directory in front
    /// (<c>ExampleVessel/src/Foo.cs</c>) or with a sibling checkout's directory in front and no
    /// <c>../</c> (<c>ExampleSibling/lib/Bar.cs</c>). Read literally, both name nothing this checkout
    /// tracks, and calling such a path absent tells a captain to create a file that exists.
    /// </summary>
    public static class CitedPathPrefix
    {
        #region Public-Methods

        /// <summary>
        /// The path with the repository's own directory removed from the front, or null when its first
        /// segment does not name this repository. The repository is named by the vessel's name and by
        /// the last segment of its repository URL and local path, compared without case.
        /// </summary>
        /// <param name="path">The path as the text names it.</param>
        /// <param name="vessel">The vessel whose checkout the path is read against.</param>
        /// <returns>The remaining path, or null.</returns>
        public static string? WithoutOwnRepository(string? path, Vessel? vessel)
        {
            if (vessel == null) return null;
            string[] parts = SplitFirstSegment(path);
            if (parts.Length != 2) return null;
            foreach (string name in OwnNames(vessel))
            {
                if (String.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase)) return parts[1];
            }
            return null;
        }

        /// <summary>
        /// The path rewritten into the declared sibling checkout its first segment names, in the
        /// <c>../</c> form the sibling citation check reads, or null when the first segment names no
        /// declared sibling. A sibling is named by the last segment of its declared relative path,
        /// compared without case.
        /// </summary>
        /// <param name="path">The path as the text names it.</param>
        /// <param name="siblings">The sibling checkouts the vessel declares.</param>
        /// <returns>The path inside the sibling checkout, or null.</returns>
        public static string? AsSiblingPath(string? path, IEnumerable<SiblingRepo>? siblings)
        {
            if (siblings == null) return null;
            string[] parts = SplitFirstSegment(path);
            if (parts.Length != 2) return null;
            foreach (SiblingRepo sibling in siblings)
            {
                if (sibling == null) continue;
                string declared = Normalize(sibling.RelativePath);
                if (declared.Length == 0) continue;
                if (String.Equals(parts[0], LastSegment(declared), StringComparison.OrdinalIgnoreCase))
                    return declared + "/" + parts[1];
            }
            return null;
        }

        /// <summary>
        /// Whether the path is a bare file name, with no directory in front of it.
        /// </summary>
        /// <param name="path">The path as the text names it.</param>
        /// <returns>True when the path has no directory separator.</returns>
        public static bool IsBareName(string? path)
        {
            string normalized = Normalize(path);
            return normalized.Length > 0 && normalized.IndexOf('/') < 0;
        }

        #endregion

        #region Private-Methods

        private static IEnumerable<string> OwnNames(Vessel vessel)
        {
            List<string> names = new List<string>();
            AddName(names, vessel.Name);
            AddName(names, LastSegment(Normalize(vessel.RepoUrl)));
            AddName(names, LastSegment(Normalize(vessel.LocalPath)));
            return names;
        }

        private static void AddName(List<string> names, string? name)
        {
            string value = (name ?? String.Empty).Trim();
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
            if (value.Length == 0 || value == "." || value == "..") return;
            names.Add(value);
        }

        private static string[] SplitFirstSegment(string? path)
        {
            string normalized = Normalize(path);
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            int slash = normalized.IndexOf('/');
            if (slash <= 0 || slash == normalized.Length - 1) return Array.Empty<string>();
            string first = normalized.Substring(0, slash);
            if (first == "." || first == "..") return Array.Empty<string>();
            return new string[] { first, normalized.Substring(slash + 1) };
        }

        private static string LastSegment(string value)
        {
            int slash = value.LastIndexOf('/');
            return slash < 0 ? value : value.Substring(slash + 1);
        }

        private static string Normalize(string? value)
        {
            return (value ?? String.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        #endregion
    }
}
