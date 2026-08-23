namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Classifies a change set by whether it could possibly have fixed anything.
    /// </summary>
    /// <remarks>
    /// A mission is normally judged by whether it RAN: the process started, the captain reported,
    /// the pipeline advanced. That measure cannot separate a rescue that repaired the defect from
    /// one that spent a day writing about it. Both look identical from the outside, and the second
    /// is more expensive, because it consumes the recovery budget and returns the pipeline to the
    /// same failure with nothing learned.
    /// <para>
    /// Documentation is classified separately rather than ignored. A docs change is legitimate
    /// work in its own right - it is simply not evidence that a reported defect was addressed, and
    /// conflating the two is what let a rescue report success for changing a single file of prose.
    /// </para>
    /// </remarks>
    public static class ChangeSubstanceClassifier
    {
        #region Private-Members

        // Extensions that carry narrative rather than behavior. A change confined to these cannot
        // alter what the software does.
        private static readonly HashSet<string> _DocumentationExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".txt", ".rst", ".adoc", ".asciidoc"
        };

        // Extension-less files that are conventionally prose.
        private static readonly HashSet<string> _DocumentationFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "readme", "changelog", "license", "licence", "notice", "authors", "contributing", "codeowners"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify a set of changed repository paths.
        /// </summary>
        /// <param name="changedPaths">
        /// Repository-relative paths that the mission changed. Null or empty means nothing changed.
        /// </param>
        /// <returns>The substance of the change set.</returns>
        public static ChangeSubstanceEnum Classify(IEnumerable<string>? changedPaths)
        {
            if (changedPaths == null) return ChangeSubstanceEnum.None;

            bool sawAnything = false;

            foreach (string path in changedPaths)
            {
                if (String.IsNullOrWhiteSpace(path)) continue;
                sawAnything = true;

                // One file that can carry behavior is enough to make the whole set substantive,
                // so there is no need to look at the rest.
                if (!IsDocumentation(path)) return ChangeSubstanceEnum.Substantive;
            }

            return sawAnything ? ChangeSubstanceEnum.DocumentationOnly : ChangeSubstanceEnum.None;
        }

        /// <summary>
        /// Whether one repository-relative path is documentation rather than behavior.
        /// </summary>
        /// <param name="path">Repository-relative path.</param>
        /// <returns>True when the path carries narrative only.</returns>
        public static bool IsDocumentation(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;

            string normalized = path.Replace('\\', '/').Trim();

            // Anything under a documentation directory, at any depth. Checked before the extension
            // so an image or sample inside docs/ counts as documentation too.
            foreach (string segment in normalized.Split('/'))
            {
                if (String.Equals(segment, "docs", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(segment, "doc", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            int lastSlash = normalized.LastIndexOf('/');
            string fileName = lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;
            if (fileName.Length == 0) return false;

            int dot = fileName.LastIndexOf('.');
            if (dot <= 0)
            {
                // No extension: judge by the conventional prose file names.
                return _DocumentationFileNames.Contains(fileName);
            }

            string extension = fileName.Substring(dot);
            if (_DocumentationExtensions.Contains(extension)) return true;

            // CHANGELOG.md is already covered by the extension; CHANGELOG.rst likewise. A bare
            // stem match catches CHANGELOG.old and similar.
            string stem = fileName.Substring(0, dot);
            return _DocumentationFileNames.Contains(stem);
        }

        #endregion
    }
}
