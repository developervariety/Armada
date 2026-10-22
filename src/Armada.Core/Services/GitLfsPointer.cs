namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// Recognizes a Git LFS pointer: the short text a repository stores in place of a file kept through Git
    /// LFS. A revision read with <c>git show</c> returns the pointer, not the file, so code that reads a cited
    /// file's lines must never treat a pointer as the file.
    /// </summary>
    public static class GitLfsPointer
    {
        #region Public-Members

        /// <summary>
        /// The first line of every Git LFS pointer.
        /// </summary>
        public const string VersionLine = "version https://git-lfs.github.com/spec/v1";

        #endregion

        #region Private-Members

        private const string _OidPrefix = "oid sha256:";

        private const string _SizePrefix = "size ";

        // A pointer is a few key-value lines; a file with more lines is content, whatever its first line says.
        private const int _MaxPointerLines = 16;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether file lines are a Git LFS pointer.
        /// </summary>
        /// <param name="lines">The lines read from a revision.</param>
        /// <returns>True when the lines are a pointer, not the file's content.</returns>
        public static bool IsPointer(IReadOnlyList<string>? lines)
        {
            return TryParse(lines, out string _, out long _);
        }

        /// <summary>
        /// Read the object id and size from Git LFS pointer text.
        /// </summary>
        /// <param name="content">The text read from a revision.</param>
        /// <param name="oid">The SHA-256 of the file's content, in lowercase hex.</param>
        /// <param name="size">The file's size in bytes.</param>
        /// <returns>True when the text is a pointer.</returns>
        public static bool TryParse(string? content, out string oid, out long size)
        {
            oid = "";
            size = 0;
            if (String.IsNullOrEmpty(content) || !content.StartsWith(VersionLine, StringComparison.Ordinal)) return false;
            return TryParse(content.Replace("\r\n", "\n").Split('\n'), out oid, out size);
        }

        /// <summary>
        /// Read the object id and size from Git LFS pointer lines.
        /// </summary>
        /// <param name="lines">The lines read from a revision.</param>
        /// <param name="oid">The SHA-256 of the file's content, in lowercase hex.</param>
        /// <param name="size">The file's size in bytes.</param>
        /// <returns>True when the lines are a pointer.</returns>
        public static bool TryParse(IReadOnlyList<string>? lines, out string oid, out long size)
        {
            oid = "";
            size = 0;
            if (lines == null || lines.Count < 3 || lines.Count > _MaxPointerLines) return false;
            if (!String.Equals(lines[0].TrimEnd(), VersionLine, StringComparison.Ordinal)) return false;
            string? foundOid = null;
            long? foundSize = null;
            for (int index = 1; index < lines.Count; index++)
            {
                string line = lines[index].TrimEnd();
                if (line.Length == 0)
                {
                    if (index == lines.Count - 1) continue;
                    return false;
                }
                if (line.StartsWith(_OidPrefix, StringComparison.Ordinal))
                {
                    string value = line.Substring(_OidPrefix.Length);
                    if (value.Length != 64 || !IsLowerHex(value)) return false;
                    foundOid = value;
                }
                else if (line.StartsWith(_SizePrefix, StringComparison.Ordinal))
                {
                    if (!Int64.TryParse(line.Substring(_SizePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long value)) return false;
                    foundSize = value;
                }
                else if (line.IndexOf(' ') <= 0)
                {
                    return false;
                }
            }
            if (foundOid == null || foundSize == null) return false;
            oid = foundOid;
            size = foundSize.Value;
            return true;
        }

        #endregion

        #region Private-Methods

        private static bool IsLowerHex(string value)
        {
            foreach (char c in value)
            {
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
            }
            return true;
        }

        #endregion
    }
}
