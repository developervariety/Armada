namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.Extensions.FileSystemGlobbing;
    using SyslogLogging;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Expands read-context requests (host paths or globs) into read-only
    /// <see cref="PrestagedFile"/> entries staged under <c>_refs/</c> in the dock worktree.
    /// Enforces per-file byte, total byte, and file-count guards from
    /// <see cref="CodeIndexSettings"/>. On any guard breach, unreadable source, or
    /// zero-match glob, returns a clear actionable error and no partial results.
    /// </summary>
    public class ReadContextStager
    {
        #region Private-Members

        private string _Header = "[ReadContextStager] ";
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public ReadContextStager(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Expands all <paramref name="requests"/> into read-only prestaged file entries
        /// under <c>_refs/</c>, enforcing size guards from <paramref name="settings"/>.
        /// </summary>
        /// <param name="requests">
        /// List of read-context requests. Null or empty returns success with an empty
        /// <see cref="StageResult.Entries"/> list.
        /// </param>
        /// <param name="hostRootForRelativePaths">
        /// Absolute host directory used to resolve relative <see cref="ReadContextRequest.SourceGlob"/>
        /// values and to enforce source containment.
        /// </param>
        /// <param name="settings">
        /// Code-index settings supplying the per-file, total-bytes, and file-count caps.
        /// </param>
        /// <returns>
        /// A <see cref="StageResult"/> with produced entries on success, or with a non-null
        /// <see cref="StageResult.Error"/> on the first violation.
        /// </returns>
        public StageResult Stage(List<ReadContextRequest>? requests, string hostRootForRelativePaths, CodeIndexSettings settings)
        {
            if (requests == null || requests.Count == 0)
                return new StageResult();

            if (String.IsNullOrEmpty(hostRootForRelativePaths))
                return new StageResult { Error = "hostRootForRelativePaths is empty; cannot stage read context" };

            if (settings == null)
                return new StageResult { Error = "settings is null; cannot enforce size guards" };

            string resolvedRoot;
            try
            {
                resolvedRoot = Path.GetFullPath(hostRootForRelativePaths)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                return new StageResult { Error = "could not resolve hostRootForRelativePaths '" + hostRootForRelativePaths + "': " + ex.Message };
            }

            List<PrestagedFile> entries = new List<PrestagedFile>();
            long totalBytes = 0;
            int totalFiles = 0;

            foreach (ReadContextRequest req in requests)
            {
                if (req == null) continue;

                string glob = req.SourceGlob ?? "";
                if (String.IsNullOrWhiteSpace(glob))
                    return new StageResult { Error = "ReadContextRequest has empty SourceGlob" };

                bool isGlob = ContainsGlobChars(glob);
                List<string> matchedPaths;
                string? findError = isGlob
                    ? FindGlobMatches(glob, resolvedRoot, out matchedPaths)
                    : FindExplicitPath(glob, resolvedRoot, out matchedPaths);

                if (findError != null)
                    return new StageResult { Error = findError };

                foreach (string sourceAbsolute in matchedPaths)
                {
                    string normalizedSource;
                    try
                    {
                        normalizedSource = Path.GetFullPath(sourceAbsolute);
                    }
                    catch (Exception ex)
                    {
                        return new StageResult { Error = "could not resolve source path '" + sourceAbsolute + "': " + ex.Message };
                    }

                    if (!IsWithinRoot(normalizedSource, resolvedRoot))
                        return new StageResult
                        {
                            Error = "source path '" + sourceAbsolute + "' resolves outside the allowed host root '" + resolvedRoot + "'"
                        };

                    long fileBytes;
                    try
                    {
                        fileBytes = new FileInfo(normalizedSource).Length;
                    }
                    catch (Exception ex)
                    {
                        return new StageResult { Error = "could not stat source '" + sourceAbsolute + "': " + ex.Message };
                    }

                    if (fileBytes > settings.MaxReadContextFileBytes)
                        return new StageResult
                        {
                            Error = "source file '" + sourceAbsolute + "' is " + fileBytes + " bytes, which exceeds MaxReadContextFileBytes limit of " + settings.MaxReadContextFileBytes + " bytes"
                        };

                    if (totalBytes + fileBytes > settings.MaxReadContextTotalBytes)
                        return new StageResult
                        {
                            Error = "adding '" + sourceAbsolute + "' (" + fileBytes + " bytes) would exceed MaxReadContextTotalBytes limit of " + settings.MaxReadContextTotalBytes + " bytes (accumulated: " + totalBytes + " bytes)"
                        };

                    if (totalFiles + 1 > settings.MaxReadContextFileCount)
                        return new StageResult
                        {
                            Error = "staging '" + sourceAbsolute + "' would exceed MaxReadContextFileCount limit of " + settings.MaxReadContextFileCount + " files"
                        };

                    string relativePath = GetRelativePath(normalizedSource, resolvedRoot);
                    string destPath = BuildDestPath(relativePath, req.DestSubPath);

                    entries.Add(new PrestagedFile(normalizedSource, destPath) { ReadOnly = true });
                    totalBytes += fileBytes;
                    totalFiles++;

                    _Logging.Debug(_Header + "staged read-context: " + normalizedSource + " -> " + destPath);
                }
            }

            return new StageResult { Entries = entries };
        }

        #endregion

        #region Private-Methods

        private static bool ContainsGlobChars(string path)
        {
            return path.IndexOfAny(new char[] { '*', '?', '[' }) >= 0;
        }

        private static string? FindExplicitPath(string sourceGlob, string resolvedRoot, out List<string> matched)
        {
            matched = new List<string>();

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(sourceGlob)
                    ? Path.GetFullPath(sourceGlob)
                    : Path.GetFullPath(Path.Combine(resolvedRoot, sourceGlob));
            }
            catch (Exception ex)
            {
                return "could not resolve source path '" + sourceGlob + "': " + ex.Message;
            }

            if (!File.Exists(resolved))
                return "source path '" + sourceGlob + "' (resolved: '" + resolved + "') does not exist or is not a file";

            matched.Add(resolved);
            return null;
        }

        private static string? FindGlobMatches(string glob, string resolvedRoot, out List<string> matched)
        {
            matched = new List<string>();

            // Split the glob into a fixed base directory prefix and the pattern suffix.
            // The base dir is formed from all leading path segments that contain no glob chars.
            string normalizedGlob = glob.Replace('\\', '/');
            string[] segments = normalizedGlob.Split('/');

            List<string> baseSegments = new List<string>();
            List<string> patternSegments = new List<string>();
            bool inPattern = false;

            foreach (string seg in segments)
            {
                if (!inPattern && ContainsGlobChars(seg))
                    inPattern = true;

                if (inPattern)
                    patternSegments.Add(seg);
                else
                    baseSegments.Add(seg);
            }

            string baseDir;
            if (baseSegments.Count == 0)
            {
                baseDir = resolvedRoot;
            }
            else
            {
                string joined = String.Join("/", baseSegments);
                try
                {
                    baseDir = Path.IsPathRooted(joined)
                        ? Path.GetFullPath(joined)
                        : Path.GetFullPath(Path.Combine(resolvedRoot, joined));
                }
                catch (Exception ex)
                {
                    return "could not resolve base directory for glob '" + glob + "': " + ex.Message;
                }
            }

            if (!Directory.Exists(baseDir))
                return "base directory '" + baseDir + "' for glob '" + glob + "' does not exist";

            string pattern = patternSegments.Count > 0
                ? String.Join("/", patternSegments)
                : "**/*";

            Matcher matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(pattern);

            IEnumerable<string> fullPaths;
            try
            {
                fullPaths = matcher.GetResultsInFullPath(baseDir);
            }
            catch (Exception ex)
            {
                return "glob expansion failed for '" + glob + "': " + ex.Message;
            }

            foreach (string fullPath in fullPaths)
            {
                if (File.Exists(fullPath))
                    matched.Add(fullPath);
            }

            if (matched.Count == 0)
                return "glob '" + glob + "' matched no files under '" + baseDir + "'";

            return null;
        }

        private static bool IsWithinRoot(string absolutePath, string normalizedRoot)
        {
            return absolutePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || absolutePath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || String.Equals(absolutePath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelativePath(string absolutePath, string normalizedRoot)
        {
            if (absolutePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(normalizedRoot.Length + 1).Replace('\\', '/');
            if (absolutePath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(normalizedRoot.Length + 1).Replace('\\', '/');
            return absolutePath.Replace('\\', '/');
        }

        private static string BuildDestPath(string relativePath, string? destSubPath)
        {
            relativePath = relativePath.TrimStart('/', '\\');

            if (!String.IsNullOrEmpty(destSubPath))
            {
                string subPath = destSubPath!.Trim().Trim('/', '\\').Replace('\\', '/');
                return "_refs/" + subPath + "/" + relativePath;
            }

            return "_refs/" + relativePath;
        }

        #endregion
    }
}
