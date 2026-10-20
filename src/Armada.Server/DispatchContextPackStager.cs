namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;

    /// <summary>
    /// Builds and stages the code context pack for each mission of a dispatch. Every dispatch path that
    /// creates captain work calls this one seam: operator dispatch, the objective scheduler and the
    /// autonomous rescue. A mission's pack is built for its own goal, with the files and sibling inputs its
    /// brief names and its start ref. A mission whose pack cannot be staged is never skipped in silence:
    /// its description gains a line that names the reason, and a <see cref="NotStagedEventType"/> event
    /// records it.
    /// </summary>
    public sealed class DispatchContextPackStager
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded when a mission's context pack is not staged.
        /// </summary>
        public const string NotStagedEventType = "code_context.not_staged";

        /// <summary>
        /// Prefix of the description line a mission gains when its pack is not staged.
        /// </summary>
        public const string NotStagedLinePrefix = "Context pack: not staged.";

        /// <summary>
        /// Destination of a staged pack inside the dock.
        /// </summary>
        public const string ContextPackDestPath = "_briefing/context-pack.md";

        #endregion

        #region Private-Members

        private readonly DatabaseDriver? _Database;
        private readonly ICodeIndexService? _CodeIndexService;
        private readonly ArmadaSettings? _Settings;
        private readonly LoggingModule? _Logging;
        private readonly string _Header = "[DispatchContextPackStager] ";

        private const string _ModeAuto = "auto";
        private const string _ModeOff = "off";
        private const string _ModeForce = "force";
        private const int _DefaultTokenBudget = 5000;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database used to record not-staged events; null records none.</param>
        /// <param name="codeIndexService">Code index service; null means no pack can be built.</param>
        /// <param name="settings">Armada settings; null uses the defaults.</param>
        /// <param name="logging">Logging module; null logs nothing.</param>
        public DispatchContextPackStager(
            DatabaseDriver? database,
            ICodeIndexService? codeIndexService,
            ArmadaSettings? settings,
            LoggingModule? logging)
        {
            _Database = database;
            _CodeIndexService = codeIndexService;
            _Settings = settings;
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build and stage a pack for every mission in the request whose effective mode is not off.
        /// </summary>
        /// <param name="request">The dispatch's missions and failure policy.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null when the dispatch may continue; otherwise the error that refuses it.</returns>
        public async Task<string?> PrepareAsync(DispatchContextPackRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            List<MissionDescription> missions = request.Missions;
            if (missions.Count == 0) return null;

            string dispatchMode;
            if (!TryNormalizeMode(request.CodeContextMode, _ModeAuto, out dispatchMode))
                return "invalid codeContextMode: " + request.CodeContextMode + ". Expected auto, off, or force.";

            if (!(_Settings?.CodeIndex?.Enabled ?? true))
            {
                if (request.RefuseOnFailure
                    && (String.Equals(dispatchMode, _ModeForce, StringComparison.Ordinal)
                        || missions.Any(m => m != null
                            && String.Equals(m.CodeContextMode?.Trim(), _ModeForce, StringComparison.OrdinalIgnoreCase))))
                {
                    return "code context force requested but code indexing is disabled";
                }

                LogInfo("code context preparation skipped for " + request.Source + " dispatch because code indexing is disabled");
                return null;
            }

            bool requirePack = _Settings?.CodeIndex?.RequireContextPackWhenEnabled ?? true;
            for (int i = 0; i < missions.Count; i++)
            {
                MissionDescription mission = missions[i];
                if (mission == null) continue;

                string mode;
                if (!TryNormalizeMode(mission.CodeContextMode, dispatchMode, out mode))
                {
                    if (request.RefuseOnFailure)
                        return "invalid codeContextMode for mission '" + mission.Title + "': " + mission.CodeContextMode + ". Expected auto, off, or force.";
                    await RecordNotStagedAsync(request, mission, "its code context mode '" + mission.CodeContextMode + "' is not auto, off or force", token).ConfigureAwait(false);
                    continue;
                }

                if (String.Equals(mode, _ModeOff, StringComparison.Ordinal))
                    continue;

                bool force = String.Equals(mode, _ModeForce, StringComparison.Ordinal);
                bool refuseRequired = request.RefuseOnFailure && (force || requirePack);

                string query = BuildQuery(mission);
                if (String.IsNullOrWhiteSpace(query))
                {
                    if (request.RefuseOnFailure && force)
                        return "code context force requested for mission '" + mission.Title + "' but no query could be built";
                    if (refuseRequired)
                        return "code context required for mission '" + mission.Title + "' but no query could be built (provide CodeContextQuery, title, or description)";

                    await RecordNotStagedAsync(request, mission, "no search goal could be built from the mission title or description", token).ConfigureAwait(false);
                    continue;
                }

                if (_CodeIndexService == null)
                {
                    if (request.RefuseOnFailure && force)
                        return "code context force requested but code index service is unavailable";
                    if (refuseRequired)
                        return "code context required for mission '" + mission.Title + "' but the code index service is unavailable";

                    await RecordNotStagedAsync(request, mission, "the code index service is unavailable on this dispatch path", token).ConfigureAwait(false);
                    continue;
                }

                string missionText = (mission.Title ?? "") + "\n\n" + (mission.Description ?? "");
                ContextPackRequest contextRequest = new ContextPackRequest
                {
                    VesselId = request.VesselId,
                    Goal = query,
                    TokenBudget = request.TokenBudget ?? _DefaultTokenBudget,
                    MaxResults = request.MaxResults,
                    AssignedPaths = ContextPackManifest.ExtractAssignedPaths(missionText),
                    SiblingPaths = ContextPackManifest.ExtractSiblingPaths(missionText),
                    StartRef = String.IsNullOrWhiteSpace(mission.StartFromRef) ? null : mission.StartFromRef!.Trim()
                };

                // The pack is built for this mission's own goal. The vessel's cached baseline pack answers a
                // generic architecture question for another goal, so it is never staged. When the full goal
                // pack cannot be built, a lexical-only pack for the same goal is the fallback.
                try
                {
                    Stopwatch totalWatch = Stopwatch.StartNew();
                    LogInfo(
                        "code context phase start for mission '" + mission.Title + "' vessel " + request.VesselId
                        + " source=" + request.Source + " mode=" + mode
                        + " assignedFiles=" + contextRequest.AssignedPaths.Count
                        + " timeoutMs=" + ((int)GetTimeout().TotalMilliseconds));

                    ContextPackResponse contextPack = await BuildWithTimeoutAsync(_CodeIndexService, contextRequest).ConfigureAwait(false);
                    totalWatch.Stop();
                    LogInfo(
                        "code context for mission '" + mission.Title + "': goal_pack"
                        + " totalMs=" + totalWatch.ElapsedMilliseconds
                        + " searchMs=" + contextPack.Metrics?.SearchElapsedMs
                        + " summarizerMs=" + contextPack.Metrics?.SummarizerElapsedMs
                        + " includedFiles=" + contextPack.Metrics?.IncludedFileCount);

                    // A vessel with no code index has no code context to give, and generating a pack never
                    // indexes it. Only force refuses; every other dispatch continues and records the reason.
                    if (!contextPack.Available)
                    {
                        string unavailable = contextPack.UnavailableReason + ": " + contextPack.Message;
                        if (request.RefuseOnFailure && force)
                            return "code context force requested for mission '" + mission.Title + "' but no code context is available (" + unavailable + ")";

                        await RecordNotStagedAsync(request, mission, "no code context is available for this vessel (" + unavailable + ")", token).ConfigureAwait(false);
                        continue;
                    }

                    if (contextPack.PrestagedFiles == null || contextPack.PrestagedFiles.Count == 0)
                    {
                        if (refuseRequired)
                            return "code context generation returned no prestaged files for mission '" + mission.Title + "'";

                        await RecordNotStagedAsync(request, mission, "code context generation returned no prestaged files", token).ConfigureAwait(false);
                        continue;
                    }

                    MergeGeneratedPrestagedFiles(mission, contextPack.PrestagedFiles);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (TimeoutException ex)
                {
                    string? lexicalFailure = await TryStageLexicalGoalPackAsync(mission, contextRequest, "the goal pack timed out: " + ex.Message).ConfigureAwait(false);
                    if (lexicalFailure == null)
                        continue;

                    // A stalled index or embedding backend never blocks a dispatch that may continue. Keep the
                    // established "code context generation failed" prefix; callers match on it.
                    if (refuseRequired)
                        return "code context generation failed for mission '" + mission.Title + "': " + ex.Message
                            + "; " + lexicalFailure
                            + " (raise " + CodeContextTimeouts.TimeoutEnvVar + " or dispatch with codeContextMode=off)";

                    await RecordNotStagedAsync(request, mission, "pack generation timed out (" + ex.Message + ") and " + lexicalFailure, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string? lexicalFailure = await TryStageLexicalGoalPackAsync(mission, contextRequest, "the goal pack failed: " + ex.Message).ConfigureAwait(false);
                    if (lexicalFailure == null)
                        continue;

                    if (refuseRequired)
                        return "code context generation failed for mission '" + mission.Title + "': " + ex.Message + "; " + lexicalFailure;

                    await RecordNotStagedAsync(request, mission, "pack generation failed (" + ex.Message + ") and " + lexicalFailure, token).ConfigureAwait(false);
                }
            }

            return null;
        }

        /// <summary>
        /// Append the not-staged line to a mission description, once. The line is the reason the captain
        /// quotes in its <c>Pack:</c> report line.
        /// </summary>
        /// <param name="description">Mission description.</param>
        /// <param name="reason">Why no pack was staged.</param>
        /// <returns>The description with the line appended.</returns>
        public static string AppendNotStagedLine(string? description, string reason)
        {
            string current = description ?? "";
            if (current.Contains(NotStagedLinePrefix, StringComparison.Ordinal)) return current;
            string line = NotStagedLinePrefix + " Reason: " + reason
                + ". Search the worktree directly and report `Pack: not staged` with this reason.";
            return String.IsNullOrWhiteSpace(current) ? line : current.TrimEnd() + "\n\n" + line;
        }

        /// <summary>
        /// The search goal for a mission: its explicit code context query, otherwise its title and description.
        /// </summary>
        /// <param name="mission">Mission description.</param>
        /// <returns>The goal; empty when the mission has no text.</returns>
        public static string BuildQuery(MissionDescription mission)
        {
            if (mission == null) return "";
            if (!String.IsNullOrWhiteSpace(mission.CodeContextQuery))
                return mission.CodeContextQuery.Trim();

            string title = mission.Title ?? "";
            string description = mission.Description ?? "";
            if (String.IsNullOrWhiteSpace(description)) return title.Trim();
            if (String.IsNullOrWhiteSpace(title)) return description.Trim();
            return title.Trim() + "\n\n" + description.Trim();
        }

        #endregion

        #region Private-Methods

        private async Task RecordNotStagedAsync(DispatchContextPackRequest request, MissionDescription mission, string reason, CancellationToken token)
        {
            LogWarning("code context skipped for mission '" + mission.Title + "' (" + request.Source + " dispatch, vessel " + request.VesselId + "): "
                + reason + "; dispatch continues without a context pack");
            mission.Description = AppendNotStagedLine(mission.Description, reason);

            if (_Database == null) return;
            try
            {
                ArmadaEvent evt = new ArmadaEvent(
                    NotStagedEventType,
                    "Context pack not staged for mission '" + mission.Title + "' (" + request.Source + " dispatch): " + reason);
                evt.TenantId = request.TenantId;
                evt.UserId = request.UserId;
                evt.EntityType = "vessel";
                evt.EntityId = request.VesselId;
                evt.VesselId = request.VesselId;
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarning("could not record " + NotStagedEventType + " for mission '" + mission.Title + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Stage a lexical-only pack for the mission's own goal when the full goal pack could not be built.
        /// The lexical pack reads only the local index (no query embedding, graph expansion or summary), so it
        /// survives a slow or failing embedding or summarizer backend. A pack built for any other goal, such as
        /// the vessel's cached baseline, is never staged in its place.
        /// </summary>
        /// <returns>Null when a lexical goal pack was staged; otherwise why it was not.</returns>
        private async Task<string?> TryStageLexicalGoalPackAsync(MissionDescription mission, ContextPackRequest contextRequest, string reason)
        {
            if (_CodeIndexService == null) return "the code index service is unavailable";
            ContextPackRequest lexicalRequest = new ContextPackRequest
            {
                VesselId = contextRequest.VesselId,
                Goal = contextRequest.Goal,
                TokenBudget = contextRequest.TokenBudget,
                MaxResults = contextRequest.MaxResults,
                FastPackOnly = true,
                LexicalOnly = true,
                AssignedPaths = new List<string>(contextRequest.AssignedPaths),
                SiblingPaths = new List<string>(contextRequest.SiblingPaths),
                StartRef = contextRequest.StartRef
            };

            ContextPackResponse lexical;
            try
            {
                ICodeIndexService codeIndex = _CodeIndexService;
                lexical = await RunWithTimeoutAsync(
                    "code context lexical goal pack",
                    cancellationToken => codeIndex.BuildContextPackAsync(lexicalRequest, cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarning("code context lexical goal pack for mission '" + mission.Title + "' failed: " + ex.Message);
                return "the lexical goal pack also failed: " + ex.Message;
            }

            if (!lexical.Available || lexical.PrestagedFiles == null || lexical.PrestagedFiles.Count == 0)
                return "the lexical goal pack staged no file";

            LogWarning(
                "code context for mission '" + mission.Title + "': lexical_goal_pack because " + reason
                + "; the staged pack ranks chunks by the goal's words only");
            MergeGeneratedPrestagedFiles(mission, lexical.PrestagedFiles);
            return null;
        }

        private static async Task<ContextPackResponse> BuildWithTimeoutAsync(
            ICodeIndexService codeIndexService,
            ContextPackRequest contextRequest)
        {
            TimeSpan timeout = GetTimeout();
            CancellationTokenSource timeoutCts = new CancellationTokenSource();
            Task<ContextPackResponse> buildTask;

            try
            {
                buildTask = codeIndexService.BuildContextPackAsync(contextRequest, timeoutCts.Token);
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }

            Task completed = await Task.WhenAny(buildTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != buildTask)
            {
                try { timeoutCts.Cancel(); }
                catch (ObjectDisposedException) { }

                _ = buildTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        timeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                throw new TimeoutException(
                    "code context generation exceeded " + timeout.TotalSeconds.ToString("F0") + " seconds");
            }

            try
            {
                return await buildTask.ConfigureAwait(false);
            }
            finally
            {
                timeoutCts.Dispose();
            }
        }

        private static async Task<T> RunWithTimeoutAsync<T>(
            string operationName,
            Func<CancellationToken, Task<T>> operation)
        {
            TimeSpan timeout = GetTimeout();
            CancellationTokenSource timeoutCts = new CancellationTokenSource();
            Task<T> operationTask;

            try
            {
                operationTask = operation(timeoutCts.Token);
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }

            Task completed = await Task.WhenAny(operationTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != operationTask)
            {
                try { timeoutCts.Cancel(); }
                catch (ObjectDisposedException) { }

                // Observe the abandoned task's exception so it does not surface as unobserved.
                _ = operationTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        timeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                throw new TimeoutException(
                    operationName + " exceeded " + timeout.TotalSeconds.ToString("F0") + " seconds");
            }

            try
            {
                return await operationTask.ConfigureAwait(false);
            }
            finally
            {
                timeoutCts.Dispose();
            }
        }

        private static TimeSpan GetTimeout()
        {
            return CodeContextTimeouts.Resolve(CodeContextTimeouts.DefaultDispatchTimeoutMs);
        }

        private static bool TryNormalizeMode(string? value, string fallback, out string normalized)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                normalized = fallback;
                return true;
            }

            string candidate = value.Trim().ToLowerInvariant();
            if (String.Equals(candidate, _ModeAuto, StringComparison.Ordinal)
                || String.Equals(candidate, _ModeOff, StringComparison.Ordinal)
                || String.Equals(candidate, _ModeForce, StringComparison.Ordinal))
            {
                normalized = candidate;
                return true;
            }

            normalized = fallback;
            return false;
        }

        private void MergeGeneratedPrestagedFiles(MissionDescription mission, List<PrestagedFile> generatedFiles)
        {
            if (generatedFiles == null || generatedFiles.Count == 0) return;

            List<PrestagedFile> merged = mission.PrestagedFiles ?? new List<PrestagedFile>();
            foreach (PrestagedFile generated in generatedFiles)
            {
                if (generated == null) continue;

                bool duplicateDest = false;
                foreach (PrestagedFile existing in merged)
                {
                    if (existing == null) continue;
                    if (String.Equals(existing.DestPath, generated.DestPath, StringComparison.Ordinal))
                    {
                        duplicateDest = true;
                        break;
                    }
                }

                if (duplicateDest)
                {
                    LogWarning("skipping generated code context prestaged file because destPath already exists: " + generated.DestPath);
                    continue;
                }

                merged.Add(new PrestagedFile(generated.SourcePath ?? "", generated.DestPath ?? ContextPackDestPath));
            }

            mission.PrestagedFiles = merged.Count > 0 ? merged : null;
        }

        private void LogInfo(string message)
        {
            _Logging?.Info(_Header + message);
        }

        private void LogWarning(string message)
        {
            _Logging?.Warn(_Header + message);
        }

        #endregion
    }
}
