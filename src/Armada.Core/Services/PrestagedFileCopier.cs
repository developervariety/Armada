namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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
        /// </summary>
        /// <param name="entries">Validated prestaged file entries; may be null/empty.</param>
        /// <param name="worktreePath">Absolute path to the dock worktree root.</param>
        /// <returns>Null on success, or a failure reason describing what went wrong.</returns>
        public string? CopyAll(List<PrestagedFile>? entries, string worktreePath)
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

                string destAbsolute;
                try
                {
                    destAbsolute = Path.GetFullPath(Path.Combine(fullWorktree, dest));
                }
                catch (Exception ex)
                {
                    return "could not resolve destination path for '" + dest + "': " + ex.Message;
                }

                // Defense-in-depth: ensure the resolved absolute destination is
                // contained within the worktree root. Validation should already
                // have caught '..' and absolute paths, but a final check on the
                // resolved path catches anything odd like symlink hops.
                string normalizedRoot = fullWorktree.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!destAbsolute.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !destAbsolute.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return "prestaged file destPath '" + dest + "' resolves outside the worktree root";
                }

                if (File.Exists(destAbsolute))
                {
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
            }

            return null;
        }

        #endregion
    }
}
