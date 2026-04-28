namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Models;

    /// <summary>
    /// Validates prestaged file specifications submitted with a mission dispatch.
    /// Validation runs at the dispatch boundary on the Admiral host, before any
    /// dock is created and before any file copy is attempted.
    /// </summary>
    public static class PrestagedFileValidator
    {
        #region Public-Members

        /// <summary>
        /// Maximum number of prestaged-file entries permitted per mission.
        /// </summary>
        public const int MaxEntriesPerMission = 50;

        /// <summary>
        /// Maximum total bytes summed over all source files for a single mission.
        /// 50 MB. Larger files should be checked into the repository or fetched
        /// at runtime by the captain rather than being copied through dispatch.
        /// </summary>
        public const long MaxTotalBytesPerMission = 50L * 1024L * 1024L;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate a list of prestaged file specs. Returns a list of error
        /// messages; an empty list means the input is valid. The error messages
        /// always include the offending path so the caller can surface a clear
        /// rejection reason.
        /// </summary>
        /// <param name="entries">Entries to validate. Null is treated as empty.</param>
        /// <returns>List of error messages, empty when valid.</returns>
        public static List<string> Validate(List<PrestagedFile>? entries)
        {
            List<string> errors = new List<string>();
            if (entries == null || entries.Count == 0) return errors;

            if (entries.Count > MaxEntriesPerMission)
            {
                errors.Add(
                    "prestagedFiles count " + entries.Count + " exceeds maximum of " +
                    MaxEntriesPerMission + " entries per mission");
                return errors;
            }

            long totalBytes = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                PrestagedFile entry = entries[i];
                if (entry == null)
                {
                    errors.Add("prestagedFiles[" + i + "] is null");
                    continue;
                }

                string source = entry.SourcePath ?? "";
                string dest = entry.DestPath ?? "";

                if (String.IsNullOrWhiteSpace(source))
                {
                    errors.Add("prestagedFiles[" + i + "].sourcePath is empty");
                    continue;
                }
                if (String.IsNullOrWhiteSpace(dest))
                {
                    errors.Add("prestagedFiles[" + i + "].destPath is empty");
                    continue;
                }

                if (!Path.IsPathRooted(source))
                {
                    errors.Add("prestagedFiles[" + i + "].sourcePath must be an absolute path: " + source);
                    continue;
                }

                // destPath must be relative (not rooted, no drive letter on Windows).
                if (Path.IsPathRooted(dest))
                {
                    errors.Add("prestagedFiles[" + i + "].destPath must be relative, not absolute: " + dest);
                    continue;
                }

                // Block leading slashes / backslashes that would otherwise be tolerated
                // by Path.Combine. "/x" and "\\x" are not absolute on every platform but
                // are still semantically attempts to escape the worktree root.
                if (dest.StartsWith("/") || dest.StartsWith("\\"))
                {
                    errors.Add("prestagedFiles[" + i + "].destPath must not begin with a path separator: " + dest);
                    continue;
                }

                // Reject any '..' segment after splitting on either separator.
                string[] segments = dest.Split(new[] { '/', '\\' }, StringSplitOptions.None);
                bool hasParent = false;
                bool allEmpty = true;
                foreach (string segment in segments)
                {
                    if (segment == "..")
                    {
                        hasParent = true;
                        break;
                    }
                    if (!String.IsNullOrEmpty(segment))
                    {
                        allEmpty = false;
                    }
                }
                if (hasParent)
                {
                    errors.Add("prestagedFiles[" + i + "].destPath must not contain '..' segments: " + dest);
                    continue;
                }
                if (allEmpty)
                {
                    errors.Add("prestagedFiles[" + i + "].destPath has no usable segments: " + dest);
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(source);
                }
                catch (Exception ex)
                {
                    errors.Add("prestagedFiles[" + i + "].sourcePath could not be inspected (" + ex.Message + "): " + source);
                    continue;
                }

                if (!info.Exists)
                {
                    // Catch the directory case explicitly for a clearer error.
                    if (Directory.Exists(source))
                    {
                        errors.Add("prestagedFiles[" + i + "].sourcePath must be a file, not a directory: " + source);
                    }
                    else
                    {
                        errors.Add("prestagedFiles[" + i + "].sourcePath does not exist: " + source);
                    }
                    continue;
                }

                totalBytes += info.Length;
                if (totalBytes > MaxTotalBytesPerMission)
                {
                    errors.Add(
                        "prestagedFiles total bytes exceeds maximum of " + MaxTotalBytesPerMission +
                        " (" + (MaxTotalBytesPerMission / (1024L * 1024L)) + " MB) at entry " + i + ": " + source);
                    return errors;
                }
            }

            return errors;
        }

        #endregion
    }
}
