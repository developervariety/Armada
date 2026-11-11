namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using SyslogLogging;
    using Armada.Core.Models;

    /// <summary>
    /// Performs the actual file-copy work for <see cref="Mission.PrestagedFiles"/>.
    /// Runs on the Admiral host after a dock worktree has been materialised and
    /// before the captain process is spawned. Single-host topology only.
    /// </summary>
    public class PrestagedFileCopier
    {
        #region Private-Members

        private string _Header = "[PrestagedFileCopier] ";
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public PrestagedFileCopier(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy all entries from the supplied list into the worktree. Returns null
        /// on success, or a human-readable error message on first failure. The
        /// caller is responsible for failing the mission and surfacing the error.
        /// Caller should treat any non-null return as a hard failure.
        /// Content-based entries (where <see cref="PrestagedFile.Content"/> is
        /// non-null) are written directly as UTF-8 no-BOM text with LF line endings
        /// rather than copied from a source path.
        /// </summary>
        /// <param name="entries">Validated prestaged file entries; may be null/empty.</param>
        /// <param name="worktreePath">Absolute path to the dock worktree root.</param>
        /// <param name="allowIdenticalRestoredFiles">True only after this mission's saved work was applied to the clean checkout.</param>
        /// <returns>Null on success, or a failure reason describing what went wrong.</returns>
        public string? CopyAll(List<PrestagedFile>? entries, string worktreePath, bool allowIdenticalRestoredFiles = false)
        {
            if (entries == null || entries.Count == 0) return null;
            if (String.IsNullOrEmpty(worktreePath))
                return "worktreePath is empty -- cannot stage prestaged files";

            string fullWorktree;
            try
            {
                fullWorktree = Path.GetFullPath(worktreePath);
            }
            catch (Exception ex)
            {
                return "could not resolve worktreePath '" + worktreePath + "': " + ex.Message;
            }

            foreach (PrestagedFile entry in entries)
            {
                if (entry == null) continue;
                string source = entry.SourcePath ?? "";
                string dest = entry.DestPath ?? "";

                // PrestagedFileValidator treats '/' and '\' as separators on every platform, so the
                // copier must resolve them the same way. Without this a Windows-authored dest path
                // such as "..\escape.txt" is a traversal on Windows but a single odd filename on a
                // Unix admiral, and the containment check below never sees the escape.
                string normalizedDest = dest
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);

                string destAbsolute;
                try
                {
                    destAbsolute = Path.GetFullPath(Path.Combine(fullWorktree, normalizedDest));
                }
                catch (Exception ex)
                {
                    return "could not resolve destination path for '" + dest + "': " + ex.Message;
                }

                // Defense-in-depth: ensure the resolved absolute destination is
                // contained within the worktree root. Validation should already
                // have caught '..' and absolute paths, but a final check on the
                // resolved path rejects traversal and sibling-prefix paths.
                if (!PathContainment.IsWithin(fullWorktree, destAbsolute)
                    || String.Equals(
                        Path.TrimEndingDirectorySeparator(destAbsolute),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullWorktree)),
                        StringComparison.Ordinal))
                {
                    return "prestaged file destPath '" + dest + "' resolves outside the worktree root";
                }

                if (allowIdenticalRestoredFiles)
                {
                    try
                    {
                        if (HasLinkWithinRoot(fullWorktree, destAbsolute))
                            return "restored prestaged destination contains a link: " + dest;
                    }
                    catch (Exception ex)
                    {
                        return "could not inspect restored prestaged destination '" + dest + "': " + ex.Message;
                    }
                }

                if (File.Exists(destAbsolute))
                {
                    if (allowIdenticalRestoredFiles)
                    {
                        try
                        {
                            if ((File.GetAttributes(destAbsolute) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                                return "restored prestaged destination contains a link or non-file: " + dest;
                            using (Stream expected = entry.Content != null
                                ? new MemoryStream(new UTF8Encoding(false).GetBytes(entry.Content.Replace("\r\n", "\n").Replace("\r", "\n")))
                                : File.OpenRead(source))
                            using (Stream actual = File.OpenRead(destAbsolute))
                            {
                                if (!SameBytes(expected, actual))
                                    return "restored prestaged destination differs from its assigned input: " + dest;
                            }
                            if (entry.ReadOnly) SetReadOnlyAttribute(destAbsolute);
                            _Logging.Info(_Header + "verified restored prestaged file: " + destAbsolute);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            return "could not verify restored prestaged file '" + dest + "': " + ex.Message;
                        }
                    }
                    return "prestaged file destPath already exists in worktree: " + dest;
                }

                string? destDir = Path.GetDirectoryName(destAbsolute);
                if (!String.IsNullOrEmpty(destDir))
                {
                    try
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Error(_Header + "failed to create directory " + destDir +
                            " while staging " + source + " -> " + destAbsolute + ": " + ex.Message);
                        return "could not create destination directory '" + destDir + "': " + ex.Message;
                    }
                }

                if (entry.Content != null)
                {
                    try
                    {
                        string normalized = entry.Content.Replace("\r\n", "\n").Replace("\r", "\n");
                        File.WriteAllText(destAbsolute, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }
                    catch (Exception ex)
                    {
                        _Logging.Error(_Header + "failed to write content-based prestaged file -> " +
                            destAbsolute + ": " + ex.Message);
                        return "failed to write content to '" + dest + "': " + ex.Message;
                    }

                    _Logging.Info(_Header + "prestaged file written from content: -> " + destAbsolute);
                    if (entry.ReadOnly) SetReadOnlyAttribute(destAbsolute);
                }
                else
                {
                    try
                    {
                        File.Copy(source, destAbsolute, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Error(_Header + "failed to copy prestaged file " + source +
                            " -> " + destAbsolute + ": " + ex.Message);
                        return "failed to copy '" + source + "' to '" + dest + "': " + ex.Message;
                    }

                    _Logging.Info(_Header + "prestaged file copied: " + source + " -> " + destAbsolute);
                    if (entry.ReadOnly) SetReadOnlyAttribute(destAbsolute);
                }
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private static bool HasLinkWithinRoot(string root, string destination)
        {
            for (string? path = destination; path != null; path = Path.GetDirectoryName(path))
            {
                if (new FileInfo(path).LinkTarget != null) return true;
                if (String.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal))
                    return false;
            }
            throw new IOException("destination is not within the worktree root");
        }

        private static bool SameBytes(Stream expected, Stream actual)
        {
            if (expected.Length != actual.Length) return false;
            byte[] left = new byte[8192];
            byte[] right = new byte[8192];
            while (expected.Position < expected.Length)
            {
                int count = (int)Math.Min(left.Length, expected.Length - expected.Position);
                expected.ReadExactly(left.AsSpan(0, count));
                actual.ReadExactly(right.AsSpan(0, count));
                if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
            }
            return true;
        }

        private void SetReadOnlyAttribute(string path)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(path);
                File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to set read-only attribute on '" + path + "': " + ex.Message);
            }
        }

        #endregion
    }
}
