namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Mines completed-mission captain logs for the four pack-usage buckets that drive
    /// pack-curate hint proposals: prestaged-files-Read, prestaged-files-ignored,
    /// grep-discovered, and Edited (v2-F1).
    /// Lives at the data layer. Tested in isolation against fixture log strings.
    /// </summary>
    public class PackUsageMiner
    {
        #region Public-Members

        /// <summary>Default location for per-mission captain logs (relative to data dir).</summary>
        public const string DefaultMissionLogSubdirectory = "logs/missions";

        #endregion

        #region Private-Members

        private readonly string _MissionLogDirectory;

        // Heuristic regexes recognising tool-call records across ClaudeCode / Codex / Cursor
        // captain runtimes. Tools each runtime emits in slightly different shapes:
        //   - ClaudeCode `--verbose --print` JSON event stream: tool_use objects with name/input.
        //   - Codex log lines: `tool: read_file path=...`.
        //   - Cursor log lines: bracketed `[Read] path=...`.
        // We accept all variants and de-duplicate paths per bucket. Each runtime gets its own
        // regex; .NET regex disallows duplicate named groups in a single pattern so we
        // chain matches across the per-runtime regexes instead of unioning.
        private static readonly Regex[] _ReadPathRegexes = new[]
        {
            new Regex(@"""name""\s*:\s*""Read""[^}]*?""file_path""\s*:\s*""(?<p>[^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bread_file\b[^=]*?\bpath\s*=\s*[""']?(?<p>[^""'\s]+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\bRead\(\s*[""'](?<p>[^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\[Read\][^=]*?\bpath\s*=\s*[""']?(?<p>[^""'\s]+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        private static readonly Regex[] _EditPathRegexes = new[]
        {
            new Regex(@"""name""\s*:\s*""(?:Edit|Write|MultiEdit)""[^}]*?""file_path""\s*:\s*""(?<p>[^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(?:edit_file|write_file|apply_patch)\b[^=]*?\bpath\s*=\s*[""']?(?<p>[^""'\s]+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(?:Edit|Write|MultiEdit)\(\s*[""'](?<p>[^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\[(?:Edit|Write|MultiEdit)\][^=]*?\bpath\s*=\s*[""']?(?<p>[^""'\s]+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        private static readonly Regex _SearchToolRegex = new Regex(
            @"(?:""name""\s*:\s*""(?:Glob|Grep|armada_code_search)""" +
            @"|\b(?:grep|glob|armada_code_search)\b[^=]*?\bpattern\s*=" +
            @"|\[(?:Glob|Grep|CodeSearch)\])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate with a custom mission-log directory (defaults to %ARMADA_DATA%/logs/missions).</summary>
        public PackUsageMiner(string missionLogDirectory)
        {
            if (String.IsNullOrEmpty(missionLogDirectory))
                throw new ArgumentNullException(nameof(missionLogDirectory));
            _MissionLogDirectory = missionLogDirectory;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Mine a single mission's captain log for the four pack-usage buckets.
        /// Returns an empty triple if the log is missing or unreadable; the brief
        /// assembly notes 'log unavailable' so the consolidator can flag low confidence.
        /// </summary>
        /// <param name="mission">Mission to mine. Uses <c>mission.Id</c> to locate the log file
        ///   and <c>mission.PrestagedFiles</c> to bucket reads as from-pack vs discovered.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Pack-usage triple for the mission. Never null; returns empty triple on error.</returns>
        public async Task<PackUsageTriple> MineAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            PackUsageTriple triple = new PackUsageTriple
            {
                MissionId = mission.Id,
                LogAvailable = false
            };

            string logPath = Path.Combine(_MissionLogDirectory, mission.Id + ".log");
            if (!File.Exists(logPath))
            {
                return triple;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(logPath, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return triple;
            }

            triple.LogAvailable = true;
            return Mine(mission, content);
        }

        /// <summary>
        /// Mine a literal log content string. Public for testing in isolation against fixture strings.
        /// </summary>
        /// <param name="mission">Mission whose PrestagedFiles drive the bucketing decision.</param>
        /// <param name="logContent">Literal captain log text.</param>
        /// <returns>Pack-usage triple.</returns>
        public static PackUsageTriple Mine(Mission mission, string logContent)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (logContent == null) logContent = "";

            HashSet<string> readPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> editPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<(int Offset, string Path)> readMatches = new List<(int, string)>();
            List<int> searchOffsets = new List<int>();

            foreach (Regex regex in _ReadPathRegexes)
            {
                foreach (Match match in regex.Matches(logContent))
                {
                    string raw = match.Groups["p"].Value;
                    string normalized = NormalizePath(raw);
                    if (String.IsNullOrEmpty(normalized)) continue;
                    readPaths.Add(normalized);
                    readMatches.Add((match.Index, normalized));
                }
            }

            foreach (Regex regex in _EditPathRegexes)
            {
                foreach (Match match in regex.Matches(logContent))
                {
                    string raw = match.Groups["p"].Value;
                    string normalized = NormalizePath(raw);
                    if (!String.IsNullOrEmpty(normalized)) editPaths.Add(normalized);
                }
            }

            foreach (Match match in _SearchToolRegex.Matches(logContent))
            {
                searchOffsets.Add(match.Index);
            }

            HashSet<string> prestaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mission.PrestagedFiles != null)
            {
                foreach (PrestagedFile pf in mission.PrestagedFiles)
                {
                    string normalized = NormalizePath(pf.DestPath);
                    if (!String.IsNullOrEmpty(normalized)) prestaged.Add(normalized);
                }
            }

            PackUsageTriple triple = new PackUsageTriple
            {
                MissionId = mission.Id,
                LogAvailable = true
            };

            // Bucket 1: filesReadFromPack -- prestaged files that were Read.
            // Bucket 2: filesIgnoredFromPack -- prestaged files NEVER Read.
            foreach (string staged in prestaged)
            {
                if (readPaths.Contains(staged))
                {
                    triple.FilesReadFromPack.Add(staged);
                }
                else
                {
                    triple.FilesIgnoredFromPack.Add(staged);
                }
            }

            // Bucket 3: filesGrepDiscovered -- non-prestaged files Read shortly after a search-tool call.
            // "Shortly after" is approximated by document offset proximity (within 4000 chars of any search match).
            HashSet<string> grepDiscovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((int readOffset, string normalized) in readMatches)
            {
                if (prestaged.Contains(normalized)) continue;

                bool nearSearch = false;
                foreach (int searchOffset in searchOffsets)
                {
                    if (readOffset > searchOffset && (readOffset - searchOffset) <= 4000)
                    {
                        nearSearch = true;
                        break;
                    }
                }

                if (nearSearch) grepDiscovered.Add(normalized);
            }

            foreach (string p in grepDiscovered)
                triple.FilesGrepDiscovered.Add(p);

            // Bucket 4: filesEdited -- normalize and de-dupe.
            foreach (string edited in editPaths)
            {
                triple.FilesEdited.Add(edited);
            }

            return triple;
        }

        #endregion

        #region Private-Methods

        /// <summary>Normalize a raw path for case-insensitive comparison and forward-slash form.</summary>
        private static string NormalizePath(string? raw)
        {
            if (String.IsNullOrEmpty(raw)) return "";
            string trimmed = raw.Trim().Trim('"', '\'');
            if (String.IsNullOrEmpty(trimmed)) return "";
            return trimmed.Replace('\\', '/').TrimStart('/');
        }

        #endregion
    }

    /// <summary>
    /// The four-bucket pack-usage triple produced by <see cref="PackUsageMiner"/>.
    /// </summary>
    public sealed class PackUsageTriple
    {
        /// <summary>Mission these buckets belong to.</summary>
        public string MissionId { get; set; } = "";

        /// <summary>Whether the captain log file existed and was readable.</summary>
        public bool LogAvailable { get; set; }

        /// <summary>Prestaged files the captain Read.</summary>
        public List<string> FilesReadFromPack { get; set; } = new List<string>();

        /// <summary>Prestaged files the captain never Read (selector over-included).</summary>
        public List<string> FilesIgnoredFromPack { get; set; } = new List<string>();

        /// <summary>Non-prestaged files the captain Read after a Glob/Grep/code-search (selector miss).</summary>
        public List<string> FilesGrepDiscovered { get; set; } = new List<string>();

        /// <summary>Files the captain Edited or Wrote (highest-signal indicator of relevance).</summary>
        public List<string> FilesEdited { get; set; } = new List<string>();
    }
}
