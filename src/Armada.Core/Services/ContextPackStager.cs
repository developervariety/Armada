namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Generates and stages <c>_briefing/context-pack.md</c> into the captain's worktree
    /// during dock provisioning, applying a strict time budget and degrading to a
    /// search-only fast pack on budget-exceed or large-repo detection.
    /// Never throws -- errors are logged as warnings so pack-gen never fails a mission.
    /// </summary>
    public class ContextPackStager
    {
        #region Private-Members

        private string _Header = "[ContextPackStager] ";
        private ICodeIndexService _CodeIndex;
        private DatabaseDriver _Database;
        private LoggingModule _Logging;
        private int _ContextPackBudgetMs;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="codeIndex">Code index service used to build context packs.</param>
        /// <param name="database">Database driver for event emission.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="contextPackBudgetMs">
        /// Soft time budget in milliseconds for a full pack attempt before falling back to the
        /// search-only fast-pack path. Clamped to 100-120000.
        /// </param>
        public ContextPackStager(
            ICodeIndexService codeIndex,
            DatabaseDriver database,
            LoggingModule logging,
            int contextPackBudgetMs = 8000)
        {
            _CodeIndex = codeIndex ?? throw new ArgumentNullException(nameof(codeIndex));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            if (contextPackBudgetMs < 100) contextPackBudgetMs = 100;
            if (contextPackBudgetMs > 120000) contextPackBudgetMs = 120000;
            _ContextPackBudgetMs = contextPackBudgetMs;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Generate and stage a code context pack for the given mission into the worktree.
        /// Returns without staging when the mode is <c>off</c> or a pack is already present.
        /// Falls back to the fast-pack path when the vessel is large or the budget is exceeded,
        /// emitting a <c>code_index.pack_fast_fallback</c> event in either case.
        /// Swallows all exceptions so pack-gen never fails the mission.
        /// </summary>
        /// <param name="mission">Mission being provisioned.</param>
        /// <param name="vessel">Vessel the mission runs against.</param>
        /// <param name="worktreePath">Absolute path to the captain's worktree root.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task GenerateAndStageAsync(
            Mission mission,
            Vessel vessel,
            string worktreePath,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));

            try
            {
                // 1. Mode "off" -- do nothing.
                if (String.Equals(mission.CodeContextMode, "off", StringComparison.OrdinalIgnoreCase))
                {
                    _Logging.Debug(_Header + "code context mode is off for mission " + mission.Id + " -- skipping");
                    return;
                }

                // 2. Already staged (force-staged at dispatch time) -- leave it untouched.
                string packAbsPath = Path.Combine(worktreePath, "_briefing", "context-pack.md");
                if (File.Exists(packAbsPath))
                {
                    _Logging.Debug(_Header + "context-pack.md already present for mission " + mission.Id + " -- skipping generation");
                    return;
                }

                // 3. Build request.
                string goal = !String.IsNullOrWhiteSpace(mission.CodeContextQuery)
                    ? mission.CodeContextQuery
                    : (mission.Title ?? "") + "\n\n" + (mission.Description ?? "");

                ContextPackRequest request = new ContextPackRequest
                {
                    VesselId = vessel.Id,
                    Goal = goal,
                    TokenBudget = mission.CodeContextTokenBudget ?? 3000,
                    MaxResults = mission.CodeContextMaxResults
                };

                // 4. Decide fast vs full.
                bool useFastPack = await _CodeIndex.ShouldUseFastPackAsync(vessel.Id, token).ConfigureAwait(false);
                string fastReason = "large_repo";

                if (!useFastPack)
                {
                    using (CancellationTokenSource budgetCts = new CancellationTokenSource(_ContextPackBudgetMs))
                    using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, budgetCts.Token))
                    {
                        try
                        {
                            ContextPackResponse fullResponse = await _CodeIndex
                                .BuildContextPackAsync(request, linked.Token)
                                .ConfigureAwait(false);

                            StageResponse(fullResponse, worktreePath);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            if (token.IsCancellationRequested)
                                return;

                            // Budget expired -- fall through to fast pack.
                            useFastPack = true;
                            fastReason = "budget_exceeded";
                        }
                        catch (TimeoutException)
                        {
                            useFastPack = true;
                            fastReason = "budget_exceeded";
                        }
                        catch (Exception ex)
                        {
                            _Logging.Warn(_Header + "full pack attempt failed for mission " + mission.Id + ": " + ex.Message + " -- falling back to fast pack");
                            useFastPack = true;
                            fastReason = "budget_exceeded";
                        }
                    }
                }

                // 5. Fast pack path (large-repo threshold or fallback).
                if (useFastPack)
                {
                    await EmitFastFallbackEventAsync(mission, vessel, fastReason).ConfigureAwait(false);

                    request.FastPackOnly = true;
                    ContextPackResponse fastResponse = await _CodeIndex
                        .BuildContextPackAsync(request, token)
                        .ConfigureAwait(false);

                    StageResponse(fastResponse, worktreePath);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "context pack generation failed for mission " + mission.Id + " (swallowed): " + ex.Message);
            }
        }

        #endregion

        #region Private-Methods

        private void StageResponse(ContextPackResponse response, string worktreePath)
        {
            if (response == null || response.PrestagedFiles == null || response.PrestagedFiles.Count == 0)
                return;

            PrestagedFileCopier copier = new PrestagedFileCopier(_Logging);
            string? failure = copier.CopyAll(response.PrestagedFiles, worktreePath);
            if (failure != null)
                _Logging.Warn(_Header + "failed to stage context pack: " + failure);
        }

        private async Task EmitFastFallbackEventAsync(Mission mission, Vessel vessel, string reason)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent("code_index.pack_fast_fallback", "context pack fast fallback triggered");
                evt.EntityType = "mission";
                evt.EntityId = mission.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = vessel.Id;
                evt.VoyageId = mission.VoyageId;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    vesselId = vessel.Id,
                    missionId = mission.Id,
                    reason = reason,
                    budgetMs = _ContextPackBudgetMs
                });
                await _Database.Events.CreateAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit fast fallback event: " + ex.Message);
            }
        }

        #endregion
    }
}
