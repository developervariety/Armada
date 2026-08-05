namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Maintenance sweep that self-heals mission-branch accumulation: for every vessel, armada/*
    /// branches whose tip is already an ancestor of the default branch (i.e. their work landed) are
    /// deleted per the vessel's branch-cleanup policy, so landings that skipped cleanup on some path
    /// do not accumulate hundreds of dead branches. Unmerged branches are NEVER touched -- failed or
    /// in-flight work must be preserved. Only local deletion runs by default; LocalAndRemote also
    /// deletes the origin branch through the vessel's working checkout.
    /// </summary>
    public class BranchCleanupSweepService
    {
        #region Private-Members

        private string _Header = "[BranchCleanupSweepService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;
        private IBranchInventory _Inventory;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service; must also implement <see cref="IBranchInventory"/>.</param>
        public BranchCleanupSweepService(LoggingModule logging, DatabaseDriver database, ArmadaSettings settings, IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Inventory = git as IBranchInventory
                ?? throw new InvalidOperationException("IGitService implementation must also implement IBranchInventory");
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one maintenance sweep across every vessel. Returns per-vessel counts; failures are
        /// logged and emitted as events, never fatal to the sweep.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Sweep result.</returns>
        public async Task<BranchCleanupSweepResult> SweepAsync(CancellationToken token = default)
        {
            BranchCleanupSweepResult result = new BranchCleanupSweepResult();
            List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);

            foreach (Vessel vessel in vessels)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (String.IsNullOrWhiteSpace(vessel.LocalPath) || !Directory.Exists(vessel.LocalPath))
                {
                    result.SkippedVessels++;
                    continue;
                }

                BranchCleanupPolicyEnum policy = vessel.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
                if (policy == BranchCleanupPolicyEnum.None)
                {
                    result.SkippedVessels++;
                    continue;
                }

                string defaultBranch = String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch!;
                try
                {
                    IReadOnlyList<string> branches = await _Inventory.EnumerateLocalBranchesAsync(vessel.LocalPath, "armada/", token).ConfigureAwait(false);
                    foreach (string branch in branches)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (String.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Merged-only guard: a branch whose tip is an ancestor of the default branch
                        // carries landed work and is safe to remove. Unmerged branches are preserved
                        // even when their mission is long terminal.
                        bool merged;
                        try
                        {
                            merged = await _Inventory.IsAncestorAsync(vessel.LocalPath, branch, defaultBranch, token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _Logging.Warn(_Header + "ancestry check failed for " + branch + " on vessel " + vessel.Id + ": " + ex.Message);
                            continue;
                        }
                        if (!merged)
                        {
                            result.KeptUnmerged++;
                            continue;
                        }

                        await SweepMergedBranchAsync(vessel, branch, policy, result, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "sweep failed for vessel " + vessel.Id + ": " + ex.Message);
                }
            }

            if (result.SweptLocal > 0 || result.SweptRemote > 0)
            {
                _Logging.Info(_Header + "sweep complete: " + result.SweptLocal + " local, " + result.SweptRemote
                    + " remote branch(es) removed; " + result.KeptUnmerged + " unmerged preserved");
            }
            return result;
        }

        #endregion

        #region Private-Methods

        private async Task SweepMergedBranchAsync(Vessel vessel, string branch, BranchCleanupPolicyEnum policy, BranchCleanupSweepResult result, CancellationToken token)
        {
            bool localOk = false;
            try
            {
                await _Git.DeleteLocalBranchAsync(vessel.LocalPath!, branch, token).ConfigureAwait(false);
                localOk = true;
                result.SweptLocal++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                _Logging.Warn(_Header + "failed to delete merged branch " + branch + " from vessel " + vessel.Id + ": " + ex.Message);
            }

            bool remoteOk = false;
            if (policy == BranchCleanupPolicyEnum.LocalAndRemote)
            {
                if (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory))
                {
                    _Logging.Warn(_Header + "LocalAndRemote sweep of " + branch + " skipped for vessel " + vessel.Id
                        + ": no working checkout to delete the origin branch from");
                }
                else
                {
                    try
                    {
                        await _Git.DeleteRemoteBranchAsync(vessel.WorkingDirectory!, branch, token).ConfigureAwait(false);
                        remoteOk = true;
                        result.SweptRemote++;
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        _Logging.Warn(_Header + "failed to delete remote branch " + branch + " for vessel " + vessel.Id + ": " + ex.Message);
                    }
                }
            }

            await EmitSweptEventAsync(vessel, branch, localOk, remoteOk, token).ConfigureAwait(false);
        }

        private async Task EmitSweptEventAsync(Vessel vessel, string branch, bool localOk, bool remoteOk, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent
                {
                    EventType = "branch_cleanup.swept",
                    EntityType = "vessel",
                    EntityId = vessel.Id,
                    VesselId = vessel.Id,
                    Message = "Maintenance sweep removed merged branch " + branch + " (local: " + localOk + ", remote: " + remoteOk + ")",
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        vesselId = vessel.Id,
                        branch,
                        localDeleted = localOk,
                        remoteDeleted = remoteOk
                    })
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record branch sweep event: " + ex.Message);
            }
        }

        #endregion
    }
}
