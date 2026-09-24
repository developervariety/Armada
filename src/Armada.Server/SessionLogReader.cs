namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;

    /// <summary>
    /// The one reader for mission and captain session logs. REST, WebSocket and MCP read a log page only through it,
    /// so every surface resolves the same file, filters the same runtime noise, clamps the page the same way, and
    /// redacts every secret-shaped value before the text leaves the server.
    /// </summary>
    public static class SessionLogReader
    {
        #region Public-Methods

        /// <summary>
        /// Resolve the log file of a mission: the canonical <c>&lt;id&gt;.log</c> when it holds text, otherwise the
        /// newest non-empty <c>&lt;id&gt;.*.log</c> sidecar, otherwise the canonical file when it exists.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>The file path, or null when the mission has no log.</returns>
        public static string? ResolveMissionLogPath(string logDirectory, string missionId)
        {
            if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentNullException(nameof(logDirectory));
            if (String.IsNullOrWhiteSpace(missionId)) return null;

            string missionLogDir = Path.Combine(logDirectory, "missions");
            string canonicalPath = Path.Combine(missionLogDir, missionId + ".log");
            if (File.Exists(canonicalPath) && new FileInfo(canonicalPath).Length > 0) return canonicalPath;
            if (!Directory.Exists(missionLogDir)) return File.Exists(canonicalPath) ? canonicalPath : null;

            FileInfo? sidecar = Directory.GetFiles(missionLogDir, missionId + ".*.log")
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (sidecar != null) return sidecar.FullName;
            return File.Exists(canonicalPath) ? canonicalPath : null;
        }

        /// <summary>
        /// Resolve the current log file of a captain through its <c>&lt;id&gt;.current</c> pointer file.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The file path, or null when the captain has no current log.</returns>
        public static async Task<string?> ResolveCaptainLogPathAsync(string logDirectory, string captainId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentNullException(nameof(logDirectory));
            if (String.IsNullOrWhiteSpace(captainId)) return null;

            string pointerPath = Path.Combine(logDirectory, "captains", captainId + ".current");
            if (!File.Exists(pointerPath)) return null;
            string target;
            try
            {
                target = (await ReadTextSharedAsync(pointerPath, token).ConfigureAwait(false)).Trim();
            }
            catch (IOException)
            {
                return null;
            }
            return File.Exists(target) ? target : null;
        }

        /// <summary>
        /// Read one page of a mission's log. Runtime noise is filtered before the page is cut, so line numbers are
        /// stable across surfaces. A formatted page interprets the lines by their observed event shapes, because a
        /// mission can outlive its captain assignment.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="missionId">Mission identifier, already read under the caller's scope.</param>
        /// <param name="offset">Requested first line; null or negative starts at the first line.</param>
        /// <param name="lines">Requested line count; null uses <paramref name="defaultLines"/>, and the count is at least one.</param>
        /// <param name="defaultLines">The surface's documented default page size.</param>
        /// <param name="formatted">Return typed readable entries instead of raw text.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The redacted page.</returns>
        public static async Task<MissionLogResponse> ReadMissionLogAsync(
            string logDirectory,
            string missionId,
            int? offset,
            int? lines,
            int defaultLines,
            bool formatted = false,
            CancellationToken token = default)
        {
            string? path = ResolveMissionLogPath(logDirectory, missionId);
            LogPage page = await ReadPageAsync(path, true, offset, lines, defaultLines, formatted, AgentRuntimeEnum.Custom, token).ConfigureAwait(false);
            return new MissionLogResponse
            {
                MissionId = missionId,
                Log = page.Log,
                Lines = page.Lines,
                TotalLines = page.TotalLines,
                Entries = page.Entries,
                EntriesTruncated = page.EntriesTruncated
            };
        }

        /// <summary>
        /// Read one page of a captain's current log.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="captain">Captain, already read under the caller's scope.</param>
        /// <param name="offset">Requested first line; null or negative starts at the first line.</param>
        /// <param name="lines">Requested line count; null uses <paramref name="defaultLines"/>, and the count is at least one.</param>
        /// <param name="defaultLines">The surface's documented default page size.</param>
        /// <param name="formatted">Return typed readable entries interpreted for the captain's runtime.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The redacted page.</returns>
        public static async Task<CaptainLogResponse> ReadCaptainLogAsync(
            string logDirectory,
            Captain captain,
            int? offset,
            int? lines,
            int defaultLines,
            bool formatted = false,
            CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            string? path = await ResolveCaptainLogPathAsync(logDirectory, captain.Id, token).ConfigureAwait(false);
            LogPage page = await ReadPageAsync(path, false, offset, lines, defaultLines, formatted, captain.Runtime, token).ConfigureAwait(false);
            return new CaptainLogResponse
            {
                CaptainId = captain.Id,
                Log = page.Log,
                Lines = page.Lines,
                TotalLines = page.TotalLines,
                Entries = page.Entries,
                EntriesTruncated = page.EntriesTruncated
            };
        }

        #endregion

        #region Private-Methods

        private static async Task<LogPage> ReadPageAsync(
            string? path,
            bool filterNoise,
            int? offset,
            int? lines,
            int defaultLines,
            bool formatted,
            AgentRuntimeEnum runtime,
            CancellationToken token)
        {
            LogPage empty = new LogPage { Entries = formatted ? new List<FormattedLogLine>() : null };
            if (String.IsNullOrEmpty(path)) return empty;

            string[] allLines;
            try
            {
                allLines = await ReadLinesSharedAsync(path, token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A log that is locked, rotated or deleted mid-read reads as empty rather than failing the request.
                return empty;
            }
            catch (UnauthorizedAccessException)
            {
                return empty;
            }

            if (filterNoise) allLines = RuntimeLogNoiseFilter.Filter(allLines);
            int first = Math.Max(0, offset ?? 0);
            int count = Math.Max(1, lines ?? defaultLines);
            string[] slice = allLines.Skip(first).Take(count).ToArray();

            if (formatted)
            {
                List<FormattedLogLine> entries = RuntimeLogFormatter.FormatPage(slice, runtime, out bool truncated);
                return new LogPage
                {
                    Log = String.Join("\n", entries.Select(entry => entry.Text)),
                    Lines = entries.Count,
                    TotalLines = allLines.Length,
                    Entries = entries,
                    EntriesTruncated = truncated
                };
            }

            return new LogPage
            {
                Log = RuntimeLogFormatter.RedactSecrets(String.Join("\n", slice)),
                Lines = slice.Length,
                TotalLines = allLines.Length
            };
        }

        private static async Task<string> ReadTextSharedAsync(string path, CancellationToken token)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                return await reader.ReadToEndAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task<string[]> ReadLinesSharedAsync(string path, CancellationToken token)
        {
            List<string> result = new List<string>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                {
                    result.Add(line);
                }
            }
            return result.ToArray();
        }

        #endregion

        #region Private-Types

        private sealed class LogPage
        {
            public string Log { get; set; } = String.Empty;
            public int Lines { get; set; }
            public int TotalLines { get; set; }
            public List<FormattedLogLine>? Entries { get; set; }
            public bool EntriesTruncated { get; set; }
        }

        #endregion
    }
}
