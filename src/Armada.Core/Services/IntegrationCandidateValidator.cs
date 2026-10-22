namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// The one validation path for an integration candidate, shared by the direct LocalMerge landing and
    /// the merge queue: it runs the vessel's required checks against the exact merged commit before the
    /// caller advances or publishes the target branch, and records the verdict as evidence.
    /// </summary>
    /// <remarks>
    /// A branch's own Checks measure the branch, not the branch merged with newer target work, so a clean
    /// textual merge of two individually green branches can still break the target. The required checks
    /// are the Build and UnitTest commands of the vessel's resolved workflow profile, executed through
    /// <see cref="CheckRunService"/> in an isolated checkout of the candidate commit, under the host-wide
    /// command slot. A Check that already passed at exactly the candidate commit with the same command is
    /// reused rather than run again, so a fast-forward landing of a reviewed tip costs no second gate.
    /// <para>
    /// The records carry no voyage and no mission, so they never decide a branch-review gate; the evidence
    /// event links them to the landing instead. The caller holds the vessel's repository slot; this class
    /// takes no repository lock of its own, and the host slot is acquired inside the Check execution, so
    /// the lock order stays vessel first, host second, as in every other landing path.
    /// </para>
    /// </remarks>
    public class IntegrationCandidateValidator
    {
        #region Public-Members

        /// <summary>
        /// Event type of the evidence record written for every validated candidate, whatever its outcome.
        /// </summary>
        public const string EvidenceEventType = "landing.integration_validation";

        #endregion

        #region Private-Members

        private string _Header = "[IntegrationCandidateValidator] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private CheckRunService _CheckRuns;
        private WorkflowProfileService _WorkflowProfiles;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings; the integration validation section is read on every call.</param>
        /// <param name="checkRuns">Check execution service.</param>
        /// <param name="workflowProfiles">Workflow profile resolution.</param>
        public IntegrationCandidateValidator(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            CheckRunService checkRuns,
            WorkflowProfileService workflowProfiles)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _CheckRuns = checkRuns ?? throw new ArgumentNullException(nameof(checkRuns));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate one candidate and record the verdict as an evidence event.
        /// </summary>
        /// <param name="vessel">Vessel whose target branch the candidate would advance.</param>
        /// <param name="candidate">The candidate commits.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verdict; never null. A refusal carries a named reason.</returns>
        public async Task<IntegrationValidationResult> ValidateAsync(Vessel vessel, IntegrationCandidate candidate, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (String.IsNullOrWhiteSpace(candidate.IntegrationCommit)) throw new ArgumentNullException(nameof(candidate.IntegrationCommit));

            candidate.VesselId = vessel.Id;
            IntegrationValidationResult result = await DecideAsync(vessel, candidate, token).ConfigureAwait(false);
            result.Candidate = candidate;

            string line = "integration candidate " + candidate.IntegrationCommit + " for " + candidate.TargetBranch + " on vessel " + vessel.Id
                + " (" + candidate.LandingPath + "): " + result.Outcome + (String.IsNullOrEmpty(result.Reason) ? "" : " -- " + result.Reason);
            if (result.AllowsPublication) _Logging.Info(_Header + line);
            else _Logging.Warn(_Header + line);

            await RecordEvidenceAsync(result, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Read the most recent evidence recorded for a merge queue entry. Any read or parse failure returns
        /// null, so a caller that needs evidence treats an unreadable record as no evidence.
        /// </summary>
        /// <param name="mergeEntryId">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The latest recorded verdict, or null when none can be read.</returns>
        public async Task<IntegrationValidationResult?> ReadLatestMergeEntryEvidenceAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(mergeEntryId)) return null;
            try
            {
                List<ArmadaEvent> events = await _Database.Events.EnumerateByEntityAsync("merge_entry", mergeEntryId, 50, token).ConfigureAwait(false);
                ArmadaEvent? latest = events
                    .Where(e => e != null && String.Equals(e.EventType, EvidenceEventType, StringComparison.Ordinal) && !String.IsNullOrWhiteSpace(e.Payload))
                    .OrderByDescending(e => e.CreatedUtc)
                    .FirstOrDefault();
                if (latest == null) return null;
                return JsonSerializer.Deserialize<IntegrationValidationResult>(latest.Payload!);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read integration evidence for merge entry " + mergeEntryId + "; treating it as absent: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Private-Methods

        private async Task<IntegrationValidationResult> DecideAsync(Vessel vessel, IntegrationCandidate candidate, CancellationToken token)
        {
            IntegrationValidationSettings policy = _Settings.IntegrationValidation;
            if (!policy.Enabled)
                return Verdict(IntegrationValidationOutcomeEnum.Disabled, "integration validation is disabled in settings");

            if (policy.NoChecksRequiredVesselIds.Any(id => String.Equals(id, vessel.Id, StringComparison.Ordinal)))
                return Verdict(IntegrationValidationOutcomeEnum.NotRequired, "the vessel's policy requires no integration checks");

            AuthContext auth = BuildSystemAuth(vessel);
            WorkflowProfile? profile;
            try
            {
                profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Verdict(IntegrationValidationOutcomeEnum.Unavailable,
                    "integration_validation_unavailable: the vessel's workflow profile could not be resolved: " + ex.Message);
            }

            List<CheckRunTypeEnum> required = new List<CheckRunTypeEnum>();
            if (profile != null && !String.IsNullOrWhiteSpace(profile.BuildCommand)) required.Add(CheckRunTypeEnum.Build);
            if (profile != null && !String.IsNullOrWhiteSpace(profile.UnitTestCommand)) required.Add(CheckRunTypeEnum.UnitTest);
            if (required.Count == 0)
            {
                return Verdict(IntegrationValidationOutcomeEnum.CommandMissing,
                    "integration_validation_command_missing: vessel " + vessel.Name + " requires integration checks, but "
                    + (profile == null ? "no active workflow profile resolves for it" : "its workflow profile " + profile.Name + " defines no Build or UnitTest command")
                    + ", so the merged commit " + candidate.IntegrationCommit + " cannot be measured. Add a Build or UnitTest command to the profile, "
                    + "or list the vessel in integrationValidation.noChecksRequiredVesselIds.");
            }

            IntegrationValidationResult passed = Verdict(IntegrationValidationOutcomeEnum.Passed, String.Empty);
            List<string> summaries = new List<string>();
            foreach (CheckRunTypeEnum type in required)
            {
                string command = _WorkflowProfiles.ResolveCommand(profile!, type) ?? String.Empty;
                CheckRun? reused = await FindPassingRecordAsync(vessel, type, command, candidate.IntegrationCommit, token).ConfigureAwait(false);
                if (reused != null)
                {
                    passed.CheckRunIds.Add(reused.Id);
                    passed.ReusedCheckRunIds.Add(reused.Id);
                    summaries.Add(type + " reused " + reused.Id);
                    continue;
                }

                CheckRun run;
                try
                {
                    run = await _CheckRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        WorkflowProfileId = profile!.Id,
                        Type = type,
                        CommitHash = candidate.IntegrationCommit,
                        DiffBaseCommit = candidate.TargetBaseCommit,
                        Label = "Integration candidate " + ShortCommit(candidate.IntegrationCommit) + " for " + candidate.TargetBranch
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    IntegrationValidationResult unavailable = Verdict(IntegrationValidationOutcomeEnum.Unavailable,
                        "integration_validation_unavailable: the " + type + " check could not run against merged commit "
                        + candidate.IntegrationCommit + ": " + ex.Message);
                    unavailable.CheckRunIds.AddRange(passed.CheckRunIds);
                    unavailable.ReusedCheckRunIds.AddRange(passed.ReusedCheckRunIds);
                    return unavailable;
                }

                passed.CheckRunIds.Add(run.Id);
                if (run.Status != CheckRunStatusEnum.Passed)
                {
                    IntegrationValidationResult failed = Verdict(IntegrationValidationOutcomeEnum.Failed,
                        "integration_validation_failed: the " + type + " check " + run.Id + " ended " + run.Status
                        + (run.ExitCode.HasValue ? " (exit " + run.ExitCode.Value + ")" : "")
                        + " against merged commit " + candidate.IntegrationCommit + " of " + (candidate.SourceBranch ?? "the source branch")
                        + " into " + candidate.TargetBranch + " at " + (candidate.TargetBaseCommit ?? "the target tip")
                        + ". The branch may pass alone; the combined tree does not."
                        + (String.IsNullOrWhiteSpace(run.Summary) ? "" : " " + run.Summary));
                    failed.CheckRunIds.AddRange(passed.CheckRunIds);
                    failed.ReusedCheckRunIds.AddRange(passed.ReusedCheckRunIds);
                    return failed;
                }

                summaries.Add(type + " passed " + run.Id);
            }

            passed.Reason = String.Join("; ", summaries);
            return passed;
        }

        /// <summary>
        /// A Passed record for this vessel, type and command at exactly the candidate commit, or null. A read
        /// failure returns null, so an unreadable history costs a run rather than a false green.
        /// </summary>
        private async Task<CheckRun?> FindPassingRecordAsync(Vessel vessel, CheckRunTypeEnum type, string command, string commit, CancellationToken token)
        {
            try
            {
                CheckRunQuery query = new CheckRunQuery
                {
                    VesselId = vessel.Id,
                    Type = type,
                    Status = CheckRunStatusEnum.Passed,
                    PageNumber = 1,
                    PageSize = 100,
                    IncludeOutput = false
                };
                EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
                string expectedCommand = NormalizeCommand(command);
                return page.Objects
                    .Where(run => run != null
                        && run.Status == CheckRunStatusEnum.Passed
                        && String.Equals(run.VesselId, vessel.Id, StringComparison.Ordinal)
                        && !String.IsNullOrWhiteSpace(run.CommitHash)
                        && String.Equals(run.CommitHash!.Trim(), commit.Trim(), StringComparison.OrdinalIgnoreCase)
                        && String.Equals(NormalizeCommand(run.Command), expectedCommand, StringComparison.Ordinal))
                    .OrderByDescending(run => run.CreatedUtc)
                    .FirstOrDefault();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read earlier " + type + " checks for vessel " + vessel.Id + "; the check runs again: " + ex.Message);
                return null;
            }
        }

        private async Task RecordEvidenceAsync(IntegrationValidationResult result, CancellationToken token)
        {
            IntegrationCandidate candidate = result.Candidate;
            try
            {
                ArmadaEvent evt = new ArmadaEvent(EvidenceEventType,
                    "Integration candidate " + ShortCommit(candidate.IntegrationCommit) + " for " + candidate.TargetBranch + ": " + result.Outcome);
                bool queue = !String.IsNullOrWhiteSpace(candidate.MergeEntryId);
                evt.EntityType = queue ? "merge_entry" : "mission";
                evt.EntityId = queue ? candidate.MergeEntryId : candidate.MissionId;
                evt.MissionId = candidate.MissionId;
                evt.VesselId = candidate.VesselId;
                evt.TenantId = candidate.TenantId;
                evt.UserId = candidate.UserId;
                evt.Payload = JsonSerializer.Serialize(result);
                if (String.IsNullOrEmpty(evt.TenantId))
                    await EventOwnerScope.ApplyAsync(_Database, evt, token).ConfigureAwait(false);
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record integration evidence for candidate " + candidate.IntegrationCommit + ": " + ex.Message);
            }
        }

        private static IntegrationValidationResult Verdict(IntegrationValidationOutcomeEnum outcome, string reason)
        {
            return new IntegrationValidationResult { Outcome = outcome, Reason = reason };
        }

        private static AuthContext BuildSystemAuth(Vessel vessel)
        {
            return AuthContext.Authenticated(
                String.IsNullOrWhiteSpace(vessel.TenantId) ? Constants.DefaultTenantId : vessel.TenantId!,
                String.IsNullOrWhiteSpace(vessel.UserId) ? Constants.DefaultUserId : vessel.UserId!,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Integration validation");
        }

        private static string NormalizeCommand(string? command)
        {
            if (String.IsNullOrWhiteSpace(command)) return String.Empty;
            return String.Join(" ", command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ShortCommit(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return "(unknown)";
            string trimmed = commit.Trim();
            return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
        }

        #endregion
    }
}
