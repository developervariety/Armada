namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Resolves the running admiral's self-vessel by matching vessel WorkingDirectory against
    /// AppContext.BaseDirectory, retrieves the landed default-branch HEAD from the bare repo,
    /// computes behind-by count, and evaluates drift via <see cref="BuildDriftEvaluator"/>.
    /// </summary>
    public class BuildDriftService : IBuildDriftService
    {
        #region Private-Members

        private string _Header = "[BuildDriftService] ";
        private IGitService _Git;
        private DatabaseDriver _Database;
        private string? _RunningCommit;
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="git">Git service for commit resolution.</param>
        /// <param name="database">Database driver for vessel enumeration.</param>
        /// <param name="runningCommit">Commit SHA the running server was built from, or null if not embedded.</param>
        /// <param name="logging">Logging module.</param>
        public BuildDriftService(
            IGitService git,
            DatabaseDriver database,
            string? runningCommit,
            LoggingModule logging)
        {
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _RunningCommit = runningCommit;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<BuildDriftReport> GetReportAsync(CancellationToken token = default)
        {
            try
            {
                Vessel? selfVessel = await ResolveSelfVesselAsync(token).ConfigureAwait(false);

                if (selfVessel == null)
                {
                    _Logging.Debug(_Header + "self-vessel resolution: no match found for BaseDirectory " + AppContext.BaseDirectory);
                    return BuildDriftEvaluator.Evaluate(_RunningCommit, null, 0);
                }

                _Logging.Debug(_Header + "self-vessel resolved: " + selfVessel.Id + " (" + selfVessel.WorkingDirectory + ")");

                string? repoPath = !String.IsNullOrEmpty(selfVessel.LocalPath)
                    ? selfVessel.LocalPath
                    : selfVessel.WorkingDirectory;

                string? landedCommit = null;
                try
                {
                    landedCommit = await _Git.GetHeadCommitHashAsync(repoPath!, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to read HEAD commit from " + repoPath + ": " + ex.Message);
                }

                int behindBy = 0;
                if (!String.IsNullOrEmpty(_RunningCommit) && !String.IsNullOrEmpty(landedCommit)
                    && !String.Equals(_RunningCommit, landedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        behindBy = await _Git.GetCommitCountBetweenAsync(repoPath!, _RunningCommit, landedCommit, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "failed to count commits between " + _RunningCommit + " and " + landedCommit + ": " + ex.Message);
                    }
                }

                return BuildDriftEvaluator.Evaluate(_RunningCommit, landedCommit, behindBy);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "drift report generation failed: " + ex.Message);
                return BuildDriftEvaluator.Evaluate(_RunningCommit, null, 0);
            }
        }

        #endregion

        #region Private-Methods

        private async Task<Vessel?> ResolveSelfVesselAsync(CancellationToken token)
        {
            string baseDir = AppContext.BaseDirectory;
            if (String.IsNullOrEmpty(baseDir)) return null;

            List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            foreach (Vessel vessel in vessels)
            {
                if (String.IsNullOrEmpty(vessel.WorkingDirectory)) continue;

                string normalizedVesselPath = vessel.WorkingDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (baseDir.StartsWith(
                    normalizedVesselPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                    || String.Equals(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        normalizedVesselPath, StringComparison.OrdinalIgnoreCase))
                {
                    return vessel;
                }
            }

            return null;
        }

        #endregion
    }
}
