namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Resolves a running mission's live log the same way the remote-control query path does — the
    /// captain pointer file first, the mission log file second — and reads a bounded tail of it.
    /// The read shares the file with the writing process, so it never blocks a captain, and it is
    /// the only filesystem access the screen performs.
    /// </summary>
    public class CaptainLogTailReader : ICaptainLogTailReader
    {
        #region Private-Members

        private readonly ArmadaSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Application settings; the log directory is read from it per call.</param>
        public CaptainLogTailReader(ArmadaSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<string?> ReadTailAsync(Mission mission, int lines, CancellationToken token)
        {
            if (mission == null) return null;
            if (lines < 1) lines = 1;

            string? path = ResolveLogPath(mission);
            if (String.IsNullOrEmpty(path)) return null;

            string[] all = await ReadLinesSharedAsync(path!, token).ConfigureAwait(false);
            if (all.Length == 0) return String.Empty;

            int start = Math.Max(0, all.Length - lines);
            return String.Join("\n", all[start..]);
        }

        #endregion

        #region Private-Methods

        private string? ResolveLogPath(Mission mission)
        {
            if (!String.IsNullOrEmpty(mission.CaptainId))
            {
                string pointerPath = Path.Combine(_Settings.LogDirectory, "captains", mission.CaptainId + ".current");
                if (File.Exists(pointerPath))
                {
                    try
                    {
                        string target = File.ReadAllText(pointerPath).Trim();
                        if (target.Length > 0 && File.Exists(target)) return target;
                    }
                    catch (IOException)
                    {
                        // The pointer is rewritten between missions. A read that lands on the rewrite
                        // falls through to the mission log rather than failing the sweep.
                    }
                }
            }

            string missionPath = Path.Combine(_Settings.LogDirectory, "missions", mission.Id + ".log");
            return File.Exists(missionPath) ? missionPath : null;
        }

        private static async Task<string[]> ReadLinesSharedAsync(string path, CancellationToken token)
        {
            List<string> lines = new List<string>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using (StreamReader reader = new StreamReader(fs))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                    {
                        lines.Add(line);
                    }
                }
            }
            return lines.ToArray();
        }

        #endregion
    }
}
